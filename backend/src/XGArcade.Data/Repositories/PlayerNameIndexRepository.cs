using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerNameIndexRepository(XGArcadeDbContext dbContext) : IPlayerNameIndexRepository
{
    // REQ-208's 2026-07-26 correction: a query must match BOTH as a prefix of
    // the whole normalized name (unchanged) AND as a prefix of any individual
    // word within it (e.g. "ibrah" matching "zlatan ibrahimovic" via its
    // second word) — see PlayerNameIndexWord's doc comment for why that
    // second condition is answered from its own indexed table rather than a
    // Contains()/leading-wildcard LIKE '%query%' against NormalizedName
    // (COMP-10 is bulk-imported and can hold a large number of rows; a
    // leading-wildcard match can't use a plain B-tree index and would become
    // a sequential scan at that scale).
    //
    // Two-phase, both phases index-backed:
    //  1. Identify candidate PlayerIds via two independent plain StartsWith
    //     scans — wholeNameMatchIds against (NormalizedName)'s existing
    //     index, wordMatchIds against PlayerNameIndexWord's (Word) index —
    //     unioned as scalar Guids. Deduplicating on the scalar id (a value
    //     type, compared by value under any provider) rather than unioning
    //     the two IQueryable<PlayerNameIndex> branches directly sidesteps a
    //     provider difference: a real relational provider translates entity
    //     Union into a genuine SQL UNION (which dedupes by column value), but
    //     the InMemory test provider has no equivalent guarantee for
    //     AsNoTracking-materialized entity instances (no identity map, so two
    //     materializations of the same row are two distinct objects). A
    //     scalar Guid Union has none of that ambiguity.
    //  2. Fetch the actual rows for those ids in one indexed (primary key)
    //     lookup, ordered and limited for display.
    public async Task<IReadOnlyList<PlayerNameIndex>> SearchByPrefixAsync(
        string normalizedQuery, int limit, CancellationToken cancellationToken = default)
    {
        var wholeNameMatchIds = dbContext.PlayerNameIndexEntries
            .Where(pni => pni.NormalizedName.StartsWith(normalizedQuery))
            .Select(pni => pni.PlayerId);

        var wordMatchIds = dbContext.PlayerNameIndexWords
            .Where(w => w.Word.StartsWith(normalizedQuery))
            .Select(w => w.PlayerId);

        var matchingPlayerIds = await wholeNameMatchIds
            .Union(wordMatchIds)
            .ToListAsync(cancellationToken);

        return await dbContext.PlayerNameIndexEntries
            .AsNoTracking()
            .Where(pni => matchingPlayerIds.Contains(pni.PlayerId))
            .OrderBy(pni => pni.NormalizedName)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task UpsertManyAsync(IEnumerable<PlayerNameIndex> entries, CancellationToken cancellationToken = default)
    {
        var entryList = entries as IReadOnlyCollection<PlayerNameIndex> ?? entries.ToList();
        if (entryList.Count == 0)
            return;

        // Keyed by PlayerId — same "correct in place, don't just blindly
        // insert" discipline as ReferenceDataSeeder.SeedAsync (see that
        // class's doc comment / S-037's precedent): a re-run of the bulk
        // import must update an already-indexed player rather than throwing
        // on the unique PlayerId key or silently duplicating.
        var playerIds = entryList.Select(e => e.PlayerId).ToList();
        var existing = await dbContext.PlayerNameIndexEntries
            .Where(pni => playerIds.Contains(pni.PlayerId))
            .ToDictionaryAsync(pni => pni.PlayerId, cancellationToken);

        // Existing per-word rows for these players, loaded up front (never
        // ExecuteDeleteAsync — see coding-guidelines.md's load-then-
        // SaveChangesAsync rule; ExecuteDelete/Update can't be translated by
        // the InMemory test provider) so a re-run can add/remove individual
        // words without duplicating or leaving stale ones behind when a name
        // changes between imports (e.g. Wikidata correcting a name).
        var existingWordsByPlayer = (await dbContext.PlayerNameIndexWords
                .Where(w => playerIds.Contains(w.PlayerId))
                .ToListAsync(cancellationToken))
            .GroupBy(w => w.PlayerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var entry in entryList)
        {
            if (existing.TryGetValue(entry.PlayerId, out var existingEntry))
            {
                existingEntry.PrimaryName = entry.PrimaryName;
                existingEntry.NormalizedName = entry.NormalizedName;
                existingEntry.BirthYear = entry.BirthYear;
                existingEntry.PrimaryNationality = entry.PrimaryNationality;
            }
            else
            {
                dbContext.PlayerNameIndexEntries.Add(entry);
            }

            ReconcileWords(entry, existingWordsByPlayer.GetValueOrDefault(entry.PlayerId, []));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Splits NormalizedName into its per-word rows (PlayerNameIndexWord —
    // see that entity's doc comment) and reconciles them against whatever
    // words already exist for this player, rather than blindly deleting and
    // re-inserting every row on every upsert — same "correct in place"
    // discipline as the parent entry's own fields just above.
    private void ReconcileWords(PlayerNameIndex entry, List<PlayerNameIndexWord> currentWords)
    {
        var newWords = entry.NormalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var currentWordSet = currentWords.Select(w => w.Word).ToHashSet();

        foreach (var stale in currentWords.Where(w => !newWords.Contains(w.Word)))
            dbContext.PlayerNameIndexWords.Remove(stale);

        foreach (var word in newWords.Where(w => !currentWordSet.Contains(w)))
            dbContext.PlayerNameIndexWords.Add(new PlayerNameIndexWord { PlayerId = entry.PlayerId, Word = word });
    }
}

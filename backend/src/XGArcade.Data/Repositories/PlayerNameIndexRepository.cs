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
    //
    //     Quality-gate correction, 2026-07-26 (ADR-0044's Consequences
    //     section now says so explicitly): each branch is capped with its
    //     own `OrderBy(...).Take(limit)` BEFORE the union, not just once at
    //     the very end. Autocomplete allows queries as short as
    //     PlayerAutocompleteEndpoints.MinQueryLength (2 chars), and against
    //     COMP-10's bulk-imported scale a short common prefix can match a
    //     very large number of rows in either branch — pulling every
    //     matching id into memory before limiting (the original shape)
    //     defeats ADR-0044's whole point of staying bounded at scale. Each
    //     branch's own OrderBy is on the same column it filters
    //     (NormalizedName / Word respectively), so it's still a plain
    //     index-backed range scan, not an extra sort pass. Capping each
    //     branch at `limit` (not a multiple of it) is a deliberate,
    //     best-effort choice, not a proof of exact global correctness: since
    //     the word branch is ordered by Word rather than by the final
    //     display order (NormalizedName), the two branches' first `limit`
    //     rows are not guaranteed to contain the true alphabetically-first
    //     `limit` matches across the full union once total matches exceed
    //     `limit` in both branches at once. Autocomplete has no requirement
    //     for exact global ordering under overflow (REQ-208 requires a
    //     match be found, not a specific ranking) — trading that theoretical
    //     edge case away for a guaranteed-bounded query is the right call
    //     here; do not "fix" it by removing the per-branch Take without
    //     re-reading ADR-0044's Consequences section first.
    //  2. Fetch the actual rows for those ids in one indexed (primary key)
    //     lookup, ordered and limited for display.
    public async Task<IReadOnlyList<PlayerNameIndex>> SearchByPrefixAsync(
        string normalizedQuery, int limit, CancellationToken cancellationToken = default)
    {
        var wholeNameMatchIds = dbContext.PlayerNameIndexEntries
            .Where(pni => pni.NormalizedName.StartsWith(normalizedQuery))
            .OrderBy(pni => pni.NormalizedName)
            .Select(pni => pni.PlayerId)
            .Take(limit);

        var wordMatchIds = dbContext.PlayerNameIndexWords
            .Where(w => w.Word.StartsWith(normalizedQuery))
            .OrderBy(w => w.Word)
            .Select(w => w.PlayerId)
            .Take(limit);

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

    // REQ-211 (2026-07-27 fix): a cheap, index-backed EXACT match against
    // NormalizedName's existing index — deliberately NOT StartsWith (that's
    // SearchByPrefixAsync's job, a different, looser contract this gate must
    // never share). Used only to decide whether GridGameModule's guess-time
    // live-lookup fallback should even run — see this interface's own doc
    // comment on this method.
    public async Task<bool> ExistsByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default) =>
        await dbContext.PlayerNameIndexEntries.AsNoTracking().AnyAsync(pni => pni.NormalizedName == normalizedName, cancellationToken);

    public async Task UpsertManyAsync(IEnumerable<PlayerNameIndex> entries, CancellationToken cancellationToken = default)
    {
        var entryList = entries as IReadOnlyCollection<PlayerNameIndex> ?? entries.ToList();
        if (entryList.Count == 0)
            return;

        // 2026-08-02 fix: a single batch can contain the same PlayerId more
        // than once — confirmed live on import-player-name-index's birth-year-1970
        // slice, after 57,157 rows across birth years 1939-1969 had already
        // imported cleanly (NOTES.md's 2026-08-02 entry has the full
        // incident; the exact Wikidata-side trigger for that specific
        // response is not confirmed from this sandbox, same "can't reach
        // wikidata.org to verify" limitation as every other Wikidata data
        // question here). Without collapsing this up front, the second
        // occurrence's ReconcileWords call below tries to re-Add
        // PlayerNameIndexWord rows the first occurrence already staged (in
        // this same unsaved batch) for the same PlayerId — EF's change
        // tracker rejects the second Add for an identical {PlayerId, Word}
        // key immediately, before SaveChangesAsync ever runs a query. "Last
        // occurrence wins" matches the last-write-wins rule this method
        // already applies across separate runs (see
        // ImportAsync_SameQidInTwoBirthYearSlices... in
        // PlayerNameIndexImporterTests).
        entryList = entryList.GroupBy(e => e.PlayerId).Select(g => g.Last()).ToList();

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
    //
    // Internal, not private (2026-07-27 fix): reused as-is by
    // PlayerNameIndexWordBackfiller (XGArcade.Data.Seeding, same assembly)
    // so the one-time backfill for rows imported before PlayerNameIndexWord
    // existed can never drift from this method's own reconciliation logic —
    // see that backfiller's doc comment for why the backfill is needed.
    internal void ReconcileWords(PlayerNameIndex entry, List<PlayerNameIndexWord> currentWords)
    {
        var newWords = entry.NormalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var currentWordSet = currentWords.Select(w => w.Word).ToHashSet();

        foreach (var stale in currentWords.Where(w => !newWords.Contains(w.Word)))
            dbContext.PlayerNameIndexWords.Remove(stale);

        foreach (var word in newWords.Where(w => !currentWordSet.Contains(w)))
            dbContext.PlayerNameIndexWords.Add(new PlayerNameIndexWord { PlayerId = entry.PlayerId, Word = word });
    }
}

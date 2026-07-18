using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerNameIndexRepository(XGArcadeDbContext dbContext) : IPlayerNameIndexRepository
{
    public async Task<IReadOnlyList<PlayerNameIndex>> SearchByPrefixAsync(
        string normalizedQuery, int limit, CancellationToken cancellationToken = default) =>
        await dbContext.PlayerNameIndexEntries
            .AsNoTracking()
            .Where(pni => pni.NormalizedName.StartsWith(normalizedQuery))
            .OrderBy(pni => pni.NormalizedName)
            .Take(limit)
            .ToListAsync(cancellationToken);

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
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

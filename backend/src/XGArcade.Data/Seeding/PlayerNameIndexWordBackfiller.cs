using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Seeding;

// One-time backfill for PlayerNameIndexWord (bug-bundle fix, 2026-07-27):
// PlayerNameIndexWord rows (ADR-0044, migration 20260726120000_
// AddPlayerNameIndexWord) are only ever populated as a side effect of
// PlayerNameIndexRepository.UpsertManyAsync running again for a given
// player — a PlayerNameIndex row imported BEFORE that migration shipped has
// zero PlayerNameIndexWord rows and silently fails any surname-only
// name-index lookup (e.g. searching "Seedorf" for "Clarence Seedorf") until
// the next full import-player-name-index re-run. NOTES.md has no record of
// a full re-run since 2026-07-18, well before the word-index migration
// (2026-07-26), so this backlog is real, not hypothetical. Idempotent and
// safe to run on every startup, same shape as
// PlayerNormalizedFullNameBackfiller: EF's change tracker only emits
// inserts/deletes for players whose word set actually needs correcting.
// Deliberately reuses PlayerNameIndexRepository.ReconcileWords (internal,
// not duplicated) rather than re-deriving the split-on-space logic here, so
// this backfill can never silently drift from UpsertManyAsync's own
// word-reconciliation rules.
//
// Loads every PlayerNameIndex row rather than only ones missing words —
// simpler, and safe at Tier 0's player-pool scale (a few thousand rows,
// same reasoning PlayerNormalizedFullNameBackfiller and
// GetPlayersMissingPhotoAsync's own doc comments already give for this
// table's size class).
public static class PlayerNameIndexWordBackfiller
{
    public static async Task BackfillAsync(XGArcadeDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var entries = await dbContext.PlayerNameIndexEntries.ToListAsync(cancellationToken);
        if (entries.Count == 0)
            return;

        var playerIds = entries.Select(e => e.PlayerId).ToList();
        var existingWordsByPlayer = (await dbContext.PlayerNameIndexWords
                .Where(w => playerIds.Contains(w.PlayerId))
                .ToListAsync(cancellationToken))
            .GroupBy(w => w.PlayerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var repository = new PlayerNameIndexRepository(dbContext);
        foreach (var entry in entries)
            repository.ReconcileWords(entry, existingWordsByPlayer.GetValueOrDefault(entry.PlayerId, []));

        // One SaveChangesAsync call for the whole backfill — load-then-
        // SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteUpdateAsync/ExecuteDeleteAsync (the InMemory test provider
        // can't translate them).
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

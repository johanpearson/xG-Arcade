using Microsoft.EntityFrameworkCore;

namespace XGArcade.Data.Seeding;

// One-time backfill for Player.NormalizedFullName (S-009): rows synced
// before this column existed (or before PlayerNameNormalizer.Normalize
// gained punctuation-stripping, same story) have a stale/empty value in the
// database — Player.FullName's setter only recomputes NormalizedFullName
// when FullName is *assigned* in application code, never merely loaded from
// the database, so an un-backfilled row would silently and permanently fail
// every guess-time name match (S-009's GetPlayersByNormalizedFullNameAsync)
// with no error or log line. Idempotent and safe to re-run: EF's change
// tracker only emits an UPDATE for rows whose recomputed value actually
// differs from what's currently stored.
public static class PlayerNormalizedFullNameBackfiller
{
    public static async Task BackfillAsync(XGArcadeDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var players = await dbContext.Players.ToListAsync(cancellationToken);
        foreach (var player in players)
        {
            // Re-assigning (not just reading) FullName re-runs its setter's
            // NormalizedFullName side effect against the *current*
            // PlayerNameNormalizer implementation.
            var fullName = player.FullName;
            player.FullName = fullName;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

using Microsoft.EntityFrameworkCore;

namespace XGArcade.Data.Seeding;

// 2026-08-01 live-incident follow-up to ADR-0052: manually-triggered, one-off
// maintenance tool (Program.cs's `clear-pair-lookup-failures` CLI verb) for
// clearing pairs stuck at PlayerCacheWarmingService.PersistentFailureThreshold
// (or above) — that class skips such a pair forever, with no live query ever
// re-attempted, until an operator clears its PairLookupFailure row (see that
// class's own "persistent-failure tracking" doc comment).
//
// Deliberately pair-scoped rather than club-name-scoped like
// StaleClubAttributeCleaner: that tool's whole purge surface is "every pair
// touching a named club, on either side" — correct for its own purpose (a
// wrong-QID/query-shape correction where ALL data for that club is suspect),
// but far too broad here. The incident this class responds to (2026-08-01,
// 125 Club x Club pairs stuck at ConsecutiveFailureCount >= 2, collectively
// touching all 32 seeded clubs) confirmed that using StaleClubAttributeCleaner
// to clear them would also wipe every OTHER pair's cached PlayerAttribute/
// PlayerData for those same 32 clubs — roughly 850 pairs' worth of perfectly
// good cached data — just to clear 125 broken failure markers. This class
// touches only PairLookupFailure rows already at/above the threshold, and
// nothing else: no PlayerAttribute, no PlayerData, no ConfirmedLowMatchPair.
// Clearing just the failure marker is correct and sufficient — it doesn't
// erase any cached data, it just lets PlayerCacheWarmingService give the pair
// a fresh live-query attempt on its next run instead of skipping it forever.
//
// Idempotent and safe to re-run: a pair already cleared (or never stuck)
// simply isn't matched again, same as StaleClubAttributeCleaner's own
// "safe to run again" behavior.
public static class PairLookupFailureCleaner
{
    // Must stay in sync with PlayerCacheWarmingService.PersistentFailureThreshold
    // (XGArcade.Games.XGGrid) — duplicated here rather than referenced
    // directly because XGArcade.Data sits below XGArcade.Games.XGGrid in the
    // project-reference graph (Games.XGGrid references Data, never the
    // reverse — see COMP-06's boundary rule), so this project cannot depend
    // on that constant without introducing a circular reference. If that
    // threshold ever changes, this literal must be updated to match, or this
    // tool will clear rows PlayerCacheWarmingService would not yet consider
    // "stuck" (too low) or fail to clear rows it does (too high).
    private const int PersistentFailureThreshold = 2;

    // Returns the removed rows' pair names, formatted to match
    // PlayerCacheWarmingService's own "Failing pairs:" log line
    // ("{FirstAttributeValue} x {SecondAttributeValue}") so an operator can
    // visually cross-reference this run's output against a prior warm-
    // player-cache run's summary.
    public static async Task<IReadOnlyList<string>> ClearPersistentFailuresAsync(
        XGArcadeDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var stuckFailures = await dbContext.PairLookupFailures
            .Where(f => f.ConsecutiveFailureCount >= PersistentFailureThreshold)
            .ToListAsync(cancellationToken);

        var pairNames = stuckFailures
            .Select(f => $"{f.FirstAttributeValue} x {f.SecondAttributeValue}")
            .ToList();

        dbContext.PairLookupFailures.RemoveRange(stuckFailures);

        // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteDeleteAsync — the InMemory test provider can't translate it.
        await dbContext.SaveChangesAsync(cancellationToken);

        return pairNames;
    }
}

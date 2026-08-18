using Microsoft.EntityFrameworkCore;

namespace XGArcade.Data.Seeding;

// S-141: manually-triggered, one-off operational tool
// (Program.cs/CliVerbDispatcher's `reset-path-target-cycle` CLI verb) for
// resetting xG Path's REQ-1208/ADR-0058 target-cycle bookkeeping
// (PathTargetCycle's singleton row + every PathCycleTargetUsage row) after
// S-137 (birth-year floor), S-138 (two-seeded-club requirement), S-139
// (B-team exclusion), and S-140 (regional/national regex fix) substantially
// narrowed the eligible player pool those four stories together define.
//
// Why this is needed: PathTargetCycle.ObservedPoolSize self-corrects for
// free — XGPathGameModule.GenerateInstanceAsync always overwrites it with
// the freshly-computed pool size on every generation, old or new rules. But
// UsedInCycleCount and the PathCycleTargetUsage rows it's derived from do
// NOT self-correct: they were accumulated by counting distinct players
// selected as targets against the OLD, larger pre-S-137-140 pool. Left in
// place, the very next post-S-137-140 generation would keep comparing a
// stale "already used" count/row-set (scored against the old rules) to a
// freshly-narrowed pool size — understating how much of the NEW pool is
// actually still available, and potentially triggering ADR-0058's tolerant
// rollover early (or late) for the wrong reason. Wiping both tables gives
// the next generation a clean CycleNumber 1 baseline, scored purely against
// the new, post-S-137-140 pool.
//
// Deliberately wipes ALL PathCycleTargetUsage rows, not just the current
// cycle's — a stale row from an already-rolled-over cycle is normally inert
// (PathInstanceRepository.GetUsedPlayerIdsInCycleAsync only ever reads rows
// matching the CURRENT CycleNumber), but this reset also restarts
// CycleNumber back at 1 by deleting the PathTargetCycle singleton entirely,
// so a leftover CycleNumber-1 row from a previous life of "cycle 1" would
// no longer be inert — it would incorrectly count as already-used against
// the fresh cycle. Deleting every row, not just current-cycle ones, avoids
// that collision.
//
// Idempotent and safe to re-run: if xG Path has never generated a round yet
// (no PathTargetCycle row — see PathTargetCycle's own doc comment on that
// being its own "never generated" state, mirrored by
// AdminXGPathEndpoints's `HasData=false` branch), this is a no-op that
// still succeeds, not an error.
public static class PathTargetCycleResetter
{
    public record ResetResult(int RemovedUsageCount, bool CycleRowExisted);

    public static async Task<ResetResult> ResetAsync(
        XGArcadeDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var usageRows = await dbContext.PathCycleTargetUsages.ToListAsync(cancellationToken);
        dbContext.PathCycleTargetUsages.RemoveRange(usageRows);

        var cycleRow = await dbContext.PathTargetCycles.FirstOrDefaultAsync(cancellationToken);
        if (cycleRow is not null)
            dbContext.PathTargetCycles.Remove(cycleRow);

        // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteDeleteAsync — the InMemory test provider can't translate it.
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ResetResult(usageRows.Count, cycleRow is not null);
    }
}

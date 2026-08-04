namespace XGArcade.Data.Entities;

// Games.XGPath (COMP-11) entity — REQ-1208/ADR-0058's per-player "already
// used as a target in the current cycle" record. One row per (PlayerId,
// CycleNumber) selection — a player is recorded at most once for a given
// cycle, since once selected they're excluded from that same cycle's
// remaining generations (PathInstanceRepository.GetUsedPlayerIdsInCycleAsync
// dedups anyway, but the unique index below is the real guarantee).
//
// Rows from earlier, now-rolled-over cycles are never deleted — ADR-0058
// nowhere requires purging history on rollover, and only rows matching
// PathTargetCycle's current CycleNumber are ever consulted (
// GetUsedPlayerIdsInCycleAsync) or written to (AddInstanceWithCycleUsageAsync)
// — a stale row from a completed cycle is simply never read again, the same
// "inert, never blocks anyone" contract REQ-1208 requires for a player who
// drops out of the live eligible pool between generations.
public class PathCycleTargetUsage
{
    public Guid Id { get; set; }
    public required Guid PlayerId { get; set; }
    public required int CycleNumber { get; set; }
}

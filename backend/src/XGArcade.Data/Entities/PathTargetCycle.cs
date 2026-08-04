namespace XGArcade.Data.Entities;

// Games.XGPath (COMP-11) entity — REQ-1208/ADR-0058's cycle-tracking state.
// This is xG Path's own persisted data (never a field on the shared Player
// entity, COMP-06 — ADR-0058 explicitly rejects that alternative, on the
// same cross-game-leakage reasoning ADR-0042 already established for
// PlayerCareerStint vs. PlayerAttribute).
//
// Exactly one row exists once the first xG Path round has ever generated —
// created lazily by IPathInstanceRepository.GetOrCreateCycleStateAsync, the
// same idempotent-singleton-row shape ILeagueRepository.
// GetOrCreateGlobalLeagueAsync already establishes for League's "at most one
// Type=global row" invariant. REQ-1209's "no data yet" admin state is simply
// "no row exists yet" — IPathInstanceRepository.GetCycleStateAsync (the
// read-only counterpart used by the admin endpoint) returns null in that
// case and never creates one itself.
public class PathTargetCycle
{
    // Fixed, well-known id (see PathInstanceRepository.SingletonCycleId) —
    // this table only ever holds one row, so the id itself carries no
    // meaning beyond guaranteeing at most one row can ever be inserted.
    public Guid Id { get; set; }

    // Starts at 1 when the row is first created, incremented by 1 each time
    // ADR-0058's tolerant rollover rule fires (remaining-unused-in-cycle
    // count drops below what a generation needs).
    public required int CycleNumber { get; set; }

    // REQ-1209: the eligible pool size (REQ-1201's structural checks
    // narrowed by ADR-0056's familiarity filter) as observed at the most
    // recent xG Path round generation — persisted directly so the admin
    // read endpoint never has to recompute it (which would mean a live
    // Wikidata familiarity check, exactly what REQ-1209 forbids for a
    // read-only view).
    public required int ObservedPoolSize { get; set; }

    // REQ-1209: how many distinct players have been recorded as used
    // (PathCycleTargetUsage rows) since CycleNumber's cycle began — updated
    // alongside every PathCycleTargetUsage insert in the same unit of work,
    // rather than derived by a COUNT query on every admin read.
    public required int UsedInCycleCount { get; set; }

    // REQ-1209: when the most recently COMPLETED cycle (i.e. the cycle
    // immediately before CycleNumber) finished rolling over. Null until the
    // first rollover ever happens.
    public DateTime? LastCycleCompletedAt { get; set; }
}

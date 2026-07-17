namespace XGArcade.Core.Rounds;

// REQ-301's "configured... so play frequency can be adjusted without a code
// change" — RoundDuration's default value is now appsettings-bound
// (RoundScheduling:RoundDurationHours, read in Program.cs, same pattern as
// Internal:JobToken), while this options object itself stays a plain
// singleton (still just GameKey/RoundDuration/AllowGuessChange/GridSize) —
// only how RoundDuration's value is sourced changed, not this type's shape.
// generate-round.yml's cron controls how often the scheduler job actually
// runs; RoundDuration controls how long each generated Round then stays
// active. /internal/generate-round also accepts a per-call
// roundDurationHours override (see InternalRoundEndpoints.cs) for a one-off
// workflow_dispatch — that override never touches this singleton.
//
// generate-round.yml's cron is now daily (0 6 * * *), not coupled to this
// value the way the old Tue/Fri cadence was: RoundGenerationService's own
// idempotency check (GetLatestByGameKeyAsync + "upcoming round already
// exists" early return) makes a daily firing a no-op on days when the
// current round hasn't ended yet, so a new round is actually generated
// roughly every RoundDuration (chain-driven via EndTime, not cron-driven),
// while the cron's own max gap is a constant 24h — a comfortable, constant
// safety margin under any RoundDuration >= 24h, unlike the old exact-gap
// equality this needed hand-verifying against every time either value
// changed. See generate-round.yml's header and NOTES.md for the full
// derivation.
public class RoundSchedulingOptions
{
    public required string GameKey { get; set; }
    public required TimeSpan RoundDuration { get; set; }
    public bool AllowGuessChange { get; set; } = true;

    // Tier 0 has no admin-driven GridTemplate management yet (S-007) — the
    // scheduler endpoint find-or-creates a template of this size on demand,
    // same as /internal/grid/generate already does.
    public int GridSize { get; set; } = 3;
}

namespace XGArcade.Core.Rounds;

// REQ-301's "configured... so play frequency can be adjusted without a code
// change" — RoundDuration's default value is now appsettings-bound
// (RoundScheduling:RoundDurationHours, read in Program.cs, same pattern as
// Internal:JobToken), while this options object itself stays a plain
// singleton (still just GameKey/RoundDuration/AllowGuessChange) — only how
// RoundDuration's value is sourced changed, not this type's shape.
// Each GameKey's own round-generation cron controls how often that
// GameKey's scheduler job actually runs (generate-round.yml's shared cron
// through S-084/ADR-0051; generate-grid-round.yml/generate-path-round.yml's
// independent crons as of S-136/ADR-0072); RoundDuration controls how long
// each generated Round then stays active. /internal/generate-round also
// accepts a per-call roundDurationHours override (see
// InternalRoundEndpoints.cs) for a one-off workflow_dispatch — that
// override never touches this singleton.
//
// Each GameKey's own cron is daily (0 6 * * *), not coupled to this value
// the way the old Tue/Fri cadence was: RoundGenerationService's own
// idempotency check (GetLatestByGameKeyAsync + "upcoming round already
// exists" early return) makes a daily firing a no-op on days when the
// current round hasn't ended yet, so a new round is actually generated
// roughly every RoundDuration (chain-driven via EndTime, not cron-driven),
// while the cron's own max gap is a constant 24h — a comfortable, constant
// safety margin under any RoundDuration >= 24h, unlike the old exact-gap
// equality this needed hand-verifying against every time either value
// changed. See generate-grid-round.yml's/generate-path-round.yml's headers,
// ADR-0027, ADR-0072, and NOTES.md for the full derivation.
//
// S-084 (REQ-1202): one instance of this type is now registered per
// GameKey (xg-grid, xg-path), resolved via IRoundSchedulingOptionsResolver
// rather than injected directly — GridSize moved off this type onto
// GridGenerationOptions (Games.XGGrid) since it's xG-Grid-specific
// generation config, not a generic scheduling concern every GameKey shares.
// Don't add an equivalent xG-Path-only field (e.g. PuzzleCount) here either
// — see PathGenerationOptions (Games.XGPath) for where that lives instead.
//
// S-136 (ADR-0072): each GameKey's round-generation workflow is now its own
// file with its own daily cron (generate-grid-round.yml, generate-path-round.yml),
// no longer a single generate-round.yml looping over both GameKeys. This
// options type's shape is unaffected — it was already fully per-GameKey
// (ADR-0051) — but if RoundDuration for either GameKey is ever configured
// below 24h, or either workflow's cron cadence changes away from daily,
// ADR-0027's "RoundDuration >= cron's max gap" invariant must be re-derived
// independently for that GameKey's own workflow.
// ADR-0102 (S-204): the "a new round is actually generated roughly every
// RoundDuration (chain-driven via EndTime, not cron-driven)" claim in this
// class's own doc comment above is no longer true for every GameKey — it
// still holds for xg-grid/xg-path, but "xg-predict"'s RoundDuration is a
// dead fallback for round-generation timing purposes: XGPredictGameModule
// always supplies GameInstance.SuggestedStartTime/SuggestedEndTime (real
// fixture kickoff timing), which RoundGenerationService prefers
// unconditionally over chain-math whenever a module supplies them. This
// options type's own shape/registration is otherwise unaffected — every
// GameKey still needs a RoundSchedulingOptions entry (see
// RoundSchedulingOptionsResolver.Resolve, called unconditionally).
public class RoundSchedulingOptions
{
    public required string GameKey { get; set; }
    public required TimeSpan RoundDuration { get; set; }
    public bool AllowGuessChange { get; set; } = true;
}

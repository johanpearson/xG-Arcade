namespace XGArcade.Core.Rounds;

// REQ-301's "configured... so play frequency can be adjusted without a code
// change" — Tier 0 configures this via a plain C# default (same pattern as
// Games.XGGrid's GridGenerationOptions, not appsettings-bound), registered
// once in Program.cs. generate-round.yml's cron controls how often the
// scheduler job actually runs; RoundDuration controls how long each
// generated Round then stays active.
//
// These two ARE coupled, despite generation being framed as an idempotency
// check rather than a counter: RoundDuration must be at least as long as
// the LONGEST gap between two consecutive cron firings, or a round can
// fully close before the next scheduled run ever generates its successor —
// exactly the "dead app" gap REQ-301 exists to prevent, just caused by
// misconfiguration instead of a failure. See Program.cs's registration of
// this options object for the worked example against generate-round.yml's
// actual cron, and NOTES.md for the full derivation.
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

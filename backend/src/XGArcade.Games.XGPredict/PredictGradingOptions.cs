namespace XGArcade.Games.XGPredict;

// REQ-1305/ADR-0097 Decision §1: a plain class, NOT appsettings-bound —
// same "plain class, no appsettings binding" shape as
// XGArcade.Games.XGGrid.GridGenerationOptions (that class's own doc
// comment), following ScoringRules's own "exact values are an
// implementation detail, not specified by the REQ text" precedent
// (PredictPointsPerComponent, MaxPointsPerCell).
//
// Deliberately NOT a field on RoundSchedulingOptions/added there for
// "xg-predict": it has nothing to do with round scheduling/duration
// (RoundDurationHours) — only with how long after ITS OWN kickoff one
// specific match is expected to have finished playing, which
// PredictGradingService's grading query (IPredictInstanceRepository.
// GetMatchesReadyForGradingAsync) uses directly, independent of any
// Round/round-scheduling concept (ADR-0097's own kickoff-implies-lock
// simplification — this service never reads Round at all).
public class PredictGradingOptions
{
    // ~2h15m: 90 minutes of regulation play + a margin for stoppage time,
    // half-time, and any short delay before API-Football's own feed
    // updates — comfortably past when an ordinary Premier League match
    // (no extra time; this competition doesn't use it) should have
    // finished. Not specified by REQ-1305's own text (see this class's own
    // doc comment above) — a reasonable, documented default rather than a
    // precisely-derived value; revisit only if real grading runs show
    // matches being checked meaningfully before or after they've actually
    // finished.
    public TimeSpan TypicalMatchDuration { get; set; } = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(15);
}

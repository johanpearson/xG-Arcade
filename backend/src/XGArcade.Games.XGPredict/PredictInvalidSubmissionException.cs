namespace XGArcade.Games.XGPredict;

// REQ-1302: thrown by ScoreSubmissionAsync when a submitted prediction's
// goal counts are invalid (negative) — a rejected ordinary gameplay
// submission, not an id-resolution failure, so this deliberately does NOT
// derive from Core.Games.GameEntityNotFoundException (that type's own doc
// comment scopes it to a malformed/stale instanceId/cellId, not an
// otherwise-valid submission whose content fails validation). Split out
// from PredictScoringException (quality-gate fix, 2026-08-30) — the two
// failure modes were originally conflated under one type, which would have
// made a future GameEntityNotFoundException-based catch site treat an
// ordinary "you typed a negative score" rejection the same as a stale/
// malformed id, a real bug worth avoiding even though no Core-side caller
// exists yet to catch either type (see this class's own project doc
// comment / ADR-0096's own explicit deferred-wiring scope).
public class PredictInvalidSubmissionException(string message) : Exception(message);

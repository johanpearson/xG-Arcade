namespace XGArcade.Core.Games;

// REQ-1504: the submission shape a future Core-side/API caller will hand to
// XGHigherLowerGameModule.ScoreSubmissionAsync via its object-typed
// `submission` parameter — lives in Core.Games (not Games.XGHigherLower)
// alongside GuessSubmission/PredictionSubmission for the same ADR-0003
// boundary reason: a Core-side caller must be able to construct the
// concrete submission object without depending on any specific game's own
// project. No caller in Core constructs one yet — GuessSubmissionService is
// not wired to "xg-higher-lower" (S-226/S-227's scope, see
// XGHigherLowerGameModule's own doc comment) — this type exists now so that
// boundary stays available rather than requiring a later move, the same
// precedent PredictionSubmission's own doc comment sets.
//
// Deliberately NO CellId, unlike GuessSubmission/PredictionSubmission: those
// two games let the player pick which of several concurrently-open cells/
// matches a submission targets. xG Higher/Lower's progression is strictly
// sequential and entirely server-determined — there is always exactly one
// "current" hidden comparator for a given (instance, user) attempt, resolved
// from the persisted HigherLowerAttempt row itself (REQ-1504) — so there is
// nothing else for the caller to identify beyond the guessed direction.
public sealed record HigherLowerSubmission(HigherLowerDirection Direction);

// REQ-1504: "Higher" is correct only if the hidden comparator's value is
// strictly greater than the current baseline's; "Lower" only if strictly
// less — no third outcome is possible (REQ-1502 already excludes an exact
// tie anywhere in the sequence). An enum, not a raw string, for type safety
// at the Core.Games boundary — an invalid direction is rejected by the
// compiler/model binder rather than failing deep inside
// XGHigherLowerGameModule's own parsing.
public enum HigherLowerDirection
{
    Higher,
    Lower,
}

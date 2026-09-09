using XGArcade.Core.Games;

namespace XGArcade.Games.XGHigherLower;

// Thrown when an instanceId in GetCellIdsAsync doesn't resolve to a real
// HigherLowerInstance — a malformed/stale request, not an ordinary
// gameplay outcome. Mirrors PredictScoringException's/PathScoringException's/
// GuessScoringException's naming/role for the equivalent "not found" failure
// mode in their own game modules, including deriving from
// Core.Games.GameEntityNotFoundException (not System.Exception directly) —
// see that base type's own doc comment for why. Not yet used by
// ScoreSubmissionAsync (still NotImplementedException — that's S-225's job,
// see XGHigherLowerGameModule's own doc comment); this type exists now so
// S-225 has a ready-made "not found" exception to reuse rather than
// reinventing one, the same way PredictScoringException already covers both
// ScoreSubmissionAsync and GetCellIdsAsync's not-found cases in that module.
public class HigherLowerScoringException(string message) : GameEntityNotFoundException(message);

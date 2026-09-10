using XGArcade.Core.Games;

namespace XGArcade.Games.XGHigherLower;

// Thrown when an instanceId in GetCellIdsAsync doesn't resolve to a real
// HigherLowerInstance — a malformed/stale request, not an ordinary
// gameplay outcome. Mirrors PredictScoringException's/PathScoringException's/
// GuessScoringException's naming/role for the equivalent "not found" failure
// mode in their own game modules, including deriving from
// Core.Games.GameEntityNotFoundException (not System.Exception directly) —
// see that base type's own doc comment for why. Used by both
// ScoreSubmissionAsync and GetCellIdsAsync in XGHigherLowerGameModule for
// their equivalent "instanceId doesn't resolve" not-found case.
public class HigherLowerScoringException(string message) : GameEntityNotFoundException(message);

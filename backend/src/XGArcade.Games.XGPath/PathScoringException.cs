using XGArcade.Core.Games;

namespace XGArcade.Games.XGPath;

// Thrown when an instanceId or cellId (puzzle id) doesn't resolve to a real
// PathInstance/PathPuzzle — a malformed/stale request, not an ordinary
// gameplay outcome. Used by GetCellIdsAsync (REQ-1202) and
// ScoreSubmissionAsync (REQ-1204, S-082). Mirrors GuessScoringException's
// naming/role (XGArcade.Games.XGGrid).
//
// Derives from Core.Games.GameEntityNotFoundException (not System.Exception
// directly) so the shared, game-agnostic XGArcade.Api.Guesses.GuessEndpoints
// can catch this and XGGrid's GuessScoringException with a single catch
// clause on the shared base type, without needing compile-time knowledge of
// either game's own concrete exception type. This type still exists (rather
// than every throw site constructing GameEntityNotFoundException directly)
// so XGPath-specific code/tests can keep referencing a XGPath-owned name.
public class PathScoringException(string message) : GameEntityNotFoundException(message);

using XGArcade.Core.Games;

namespace XGArcade.Games.XGGrid;

// Thrown by GridGameModule.ScoreSubmissionAsync when the submitted cellId
// doesn't resolve to a real cell within the given grid instance — a
// malformed/stale request, not an ordinary "incorrect guess" outcome.
//
// Derives from Core.Games.GameEntityNotFoundException (not System.Exception
// directly) so the shared, game-agnostic XGArcade.Api.Guesses.GuessEndpoints
// can catch this and XGPath's PathScoringException with a single catch
// clause on the shared base type, without needing compile-time knowledge of
// either game's own concrete exception type. This type still exists (rather
// than every throw site constructing GameEntityNotFoundException directly)
// so XGGrid-specific code/tests can keep referencing a XGGrid-owned name.
public class GuessScoringException(string message) : GameEntityNotFoundException(message);

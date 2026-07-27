namespace XGArcade.Games.XGPath;

// Thrown when an instanceId or cellId (puzzle id) doesn't resolve to a real
// PathInstance/PathPuzzle — a malformed/stale request, not an ordinary
// gameplay outcome. Used by GetCellIdsAsync (REQ-1202) and
// ScoreSubmissionAsync (REQ-1204, S-082). Mirrors GuessScoringException's
// naming/role (XGArcade.Games.XGGrid) — XGArcade.Api.Guesses.GuessEndpoints
// catches both types and returns the same 404, since it's a single
// game-agnostic endpoint (see that file's own comment for the boundary
// judgment call this implies).
public class PathScoringException(string message) : Exception(message);

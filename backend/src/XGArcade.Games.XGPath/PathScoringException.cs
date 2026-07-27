namespace XGArcade.Games.XGPath;

// Thrown when an instanceId doesn't resolve to a real PathInstance — a
// malformed/stale request, not an ordinary gameplay outcome. Used by
// GetCellIdsAsync today (REQ-1202); ScoreSubmissionAsync (REQ-1204, S-082)
// is expected to reuse it for the equivalent "instance not found" case
// once implemented. Mirrors GuessScoringException's naming/role
// (XGArcade.Games.XGGrid).
public class PathScoringException(string message) : Exception(message);

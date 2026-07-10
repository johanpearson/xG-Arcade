namespace XGArcade.Games.XGGrid;

// Thrown by GridGameModule.ScoreSubmissionAsync when the submitted cellId
// doesn't resolve to a real cell within the given grid instance — a
// malformed/stale request, not an ordinary "incorrect guess" outcome.
public class GuessScoringException(string message) : Exception(message);

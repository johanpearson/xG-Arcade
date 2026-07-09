namespace XGArcade.Games.XGGrid;

// REQ-101: thrown when generation can't produce a valid grid within
// MaxAttempts, or the reference tables don't have enough distinct values to
// even attempt one. Callers are expected to log this as an error per
// REQ-101's acceptance criteria ("generation aborts and logs an error").
public class GridGenerationException(string message) : Exception(message);

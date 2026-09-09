namespace XGArcade.Games.XGHigherLower;

// REQ-1502: thrown when GenerateInstanceAsync can't produce a valid xG
// Higher/Lower instance — either no candidate stat category (REQ-1501) has
// a large enough eligible-player pool to even attempt a full-length
// sequence, or every bounded attempt (HigherLowerGenerationOptions.
// MaxAttemptsPerCategory) at building a full-length no-tie/no-repeat
// sequence (REQ-1502) for every candidate category failed. Mirrors
// PredictGenerationException's/PathGenerationException's/
// GridGenerationException's naming/role for the equivalent failure mode in
// this game module — REQ-1502's own "abort rather than produce a degraded
// instance" rule: never persist a shorter-than-configured sequence.
// Callers are expected to log this — the throw site itself does not.
public class HigherLowerGenerationException(string message) : Exception(message);

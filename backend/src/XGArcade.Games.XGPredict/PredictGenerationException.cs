namespace XGArcade.Games.XGPredict;

// REQ-1301: thrown when GenerateInstanceAsync can't produce a valid xG
// Predict instance — either the requested PredictTemplate doesn't exist, or
// the upcoming gameweek's fixture list has fewer than the configured match
// count (REQ-1301's "abort rather than generate a degraded round" case,
// same pattern REQ-101/103 already establish for xG Grid). Mirrors
// PathGenerationException's/GridGenerationException's naming/role for the
// equivalent failure mode in this game module. Callers are expected to log
// this — the throw site itself does not.
public class PredictGenerationException(string message) : Exception(message);

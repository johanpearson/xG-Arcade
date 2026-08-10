namespace XGArcade.Games.XGPath;

// REQ-1201/REQ-1202: thrown when GenerateInstanceAsync can't produce a
// valid xG Path instance — either the requested PathTemplate doesn't
// exist, or the eligible-target pool (REQ-1201) has fewer than the
// configured puzzle count N (REQ-1202 requires exactly N puzzles, never
// fewer). Mirrors GridGenerationException's naming/role
// (XGArcade.Games.XGGrid) for the equivalent failure mode in this game
// module.
public class PathGenerationException(string message) : Exception(message);

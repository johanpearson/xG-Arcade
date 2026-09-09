namespace XGArcade.Games.XGHigherLower;

// REQ-1504: thrown by ScoreSubmissionAsync when a guess arrives against an
// attempt that has already ended — whether by an incorrect guess or by
// reaching the end of the Round's fixed comparator sequence with every
// comparator guessed correctly. Mirrors PredictRoundLockedException's exact
// shape (plain Exception, not GameEntityNotFoundException-derived — this is
// not a "the id doesn't resolve to anything" failure the way
// HigherLowerScoringException is; it's an ordinary rejected-submission
// outcome against a real, found attempt). No catcher exists yet anywhere in
// the codebase — expected and correct for this story, same "flag it now,
// resolve the mapping later" discipline PredictRoundLockedException's own
// doc comment already establishes; S-227's dedicated endpoints are expected
// to catch this the way PredictEndpoints catches PredictRoundLockedException.
public class HigherLowerAttemptEndedException(string message) : Exception(message);

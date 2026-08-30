namespace XGArcade.Games.XGPredict;

// REQ-1303/ADR-0096 §4: thrown by ScoreSubmissionAsync when a prediction
// submission arrives at or after the round-level lock instant (the
// earliest of the round's 5 matches' own KickoffUtc) — REGARDLESS of which
// specific match is being predicted, since the whole round locks at the
// first match's kickoff, not each match's own (REQ-1303's exploit-
// prevention rule). No catcher exists yet anywhere in the codebase — this
// is expected and correct for this story (ADR-0096 §4): the type exists now
// so the follow-up wiring story that builds a real submission endpoint has
// a loud, specific signal to catch instead of inventing one under time
// pressure, the same "flag it now, resolve the mapping later" discipline
// ADR-0011/ADR-0057 already used for LiveLookupUnavailableException.
public class PredictRoundLockedException(string message) : Exception(message);

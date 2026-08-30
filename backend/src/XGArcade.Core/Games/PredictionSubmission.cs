namespace XGArcade.Core.Games;

// ADR-0096 §3: the submission shape a future Core-side caller will hand to
// XGPredictGameModule.ScoreSubmissionAsync via its object-typed `submission`
// parameter, for REQ-1302. Lives in Core.Games (not Games.XGPredict)
// alongside GuessSubmission/ScoreResult for the same reason GuessSubmission
// does — a Core-side caller must be able to construct the concrete
// submission object without depending on any specific game's own project
// (ADR-0003's boundary: Core never references a game-specific type). No
// caller in Core constructs one yet (that wiring is a follow-up story, see
// ADR-0096 §3/Context item 3) — this type exists now so that boundary stays
// available rather than requiring a later move.
public sealed record PredictionSubmission(Guid CellId, int HomeGoals, int AwayGoals);

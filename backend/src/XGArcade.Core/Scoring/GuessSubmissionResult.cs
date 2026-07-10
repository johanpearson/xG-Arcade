namespace XGArcade.Core.Scoring;

// Every distinct reason a submission can be rejected — REQ-202's "distinct,
// specific reason — never a generic message" applies to how the API layer
// maps each of these, not just to the guess-change-policy case it was
// written about.
public enum GuessSubmissionOutcome
{
    Accepted,
    RoundNotFound,
    RoundNotActive,
    CellAlreadySolved,
    NoAttemptsRemaining,
    GuessChangeNotAllowed,
}

public class GuessSubmissionResult
{
    public required GuessSubmissionOutcome Outcome { get; init; }
    public bool IsCorrect { get; init; }
    public int AttemptCount { get; init; }
    public bool Locked { get; init; }

    public static GuessSubmissionResult Rejected(GuessSubmissionOutcome outcome) =>
        new() { Outcome = outcome };

    public static GuessSubmissionResult Accepted(bool isCorrect, int attemptCount, bool locked) =>
        new() { Outcome = GuessSubmissionOutcome.Accepted, IsCorrect = isCorrect, AttemptCount = attemptCount, Locked = locked };
}

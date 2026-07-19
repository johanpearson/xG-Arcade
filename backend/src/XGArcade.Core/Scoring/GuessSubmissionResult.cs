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

    // Frontend name-display fix: the canonical, properly-cased Player.FullName
    // for a correct guess — never the raw as-typed text (that stays on the
    // Guess row's own SubmittedName, unaffected). Null whenever IsCorrect is
    // false; there is no real player to display a name for.
    public string? ResolvedPlayerName { get; init; }

    // REQ-214: the resolved player's Wikidata photo (Player.PhotoUrl),
    // alongside ResolvedPlayerName — same null-whenever-not-IsCorrect rule,
    // plus null whenever Wikidata has no P18 for this player (never an
    // error either way; the field is simply absent).
    public string? ResolvedPlayerPhotoUrl { get; init; }

    public static GuessSubmissionResult Rejected(GuessSubmissionOutcome outcome) =>
        new() { Outcome = outcome };

    public static GuessSubmissionResult Accepted(
        bool isCorrect, int attemptCount, bool locked, string? resolvedPlayerName = null, string? resolvedPlayerPhotoUrl = null) =>
        new()
        {
            Outcome = GuessSubmissionOutcome.Accepted,
            IsCorrect = isCorrect,
            AttemptCount = attemptCount,
            Locked = locked,
            ResolvedPlayerName = resolvedPlayerName,
            ResolvedPlayerPhotoUrl = resolvedPlayerPhotoUrl,
        };
}

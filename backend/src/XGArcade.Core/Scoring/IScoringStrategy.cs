using XGArcade.Data.Entities;

namespace XGArcade.Core.Scoring;

// ADR-0040: Core.Scoring resolves one IScoringStrategy per Round.GameKey
// (mirroring IGameModuleResolver's shape for game logic, see
// Games/IGameModuleResolver.cs) to compute a correct guess's locked
// FinalUniquenessScore/FinalPoints. The incorrect-guess/unanswered-cell
// case never calls this — that scores the same fixed worst-case penalty
// regardless of GameKey, directly in
// ScoreLockingService.LockRoundScoresAsync (ADR-0021), untouched by this
// abstraction.
//
// Only xG Grid's inputs are modeled today (a cell's other correct guesses
// + the answer given). xG Path's clue-efficiency formula needs a
// different input shape (clues used, not other guessers) — ADR-0040's
// "Consequences"/follow-up note explicitly leaves that parameter shape
// unfixed for now, not something this story needs to pre-solve.
public interface IScoringStrategy
{
    string GameKey { get; }

    // correctGuessesForCell/myAnswerPlayerId: same contract as
    // UniquenessCalculator.Calculate — every Guess for the cell where
    // IsCorrect is true, and the PlayerAnswerId of the guess being scored
    // (itself a member of correctGuessesForCell).
    ScoringResult ScoreCorrectGuess(IReadOnlyCollection<Guess> correctGuessesForCell, Guid myAnswerPlayerId);
}

// Minimal result shape for a single correct guess's locked score.
// FinalUniquenessScore is nullable because a strategy may have no
// uniqueness concept at all (ADR-0040) — not only "not yet computed".
public record ScoringResult(double? FinalUniquenessScore, int FinalPoints);

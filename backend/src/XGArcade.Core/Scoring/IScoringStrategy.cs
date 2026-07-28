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
// S-083/REQ-1206 resolved ADR-0040's follow-up: xG Grid's uniqueness
// formula and xG Path's clue-efficiency formula share this one parameter
// shape. guess carries everything path-specific (PlayerAnswerId,
// AttemptCount) that used to need its own bare parameter, and
// maxAttemptsForCell reuses ADR-0041's existing per-cell concept
// (IGameModule.GetMaxAttemptsForCellAsync) rather than inventing a
// path-specific one — so this interface stays plain-data-in, with no
// compile-time dependency on any specific game's types.
public interface IScoringStrategy
{
    string GameKey { get; }

    // guess: the specific correct Guess row being scored (IsCorrect is
    // always true for every call — ScoreLockingService never calls this
    // for an incorrect/unanswered guess, see ScoreLockingService's own
    // ADR-0021 branch). A member of correctGuessesForCell.
    //
    // correctGuessesForCell: same contract as UniquenessCalculator.Calculate
    // — every Guess for the cell where IsCorrect is true. Ignored by
    // strategies with no uniqueness concept (e.g. ClueEfficiencyScoringStrategy).
    //
    // maxAttemptsForCell: the cell's max-attempts value (ADR-0041),
    // resolved once per cell by ScoreLockingService itself (never by the
    // strategy) and passed in as a plain int. Ignored by strategies with
    // no attempt-cap concept (e.g. UniquenessScoringStrategy).
    ScoringResult ScoreCorrectGuess(Guess guess, IReadOnlyCollection<Guess> correctGuessesForCell, int maxAttemptsForCell);
}

// Minimal result shape for a single correct guess's locked score.
// FinalUniquenessScore is nullable because a strategy may have no
// uniqueness concept at all (ADR-0040) — not only "not yet computed".
public record ScoringResult(double? FinalUniquenessScore, int FinalPoints);

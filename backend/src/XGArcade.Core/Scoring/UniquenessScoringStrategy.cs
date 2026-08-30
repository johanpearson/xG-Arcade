using XGArcade.Data.Entities;

namespace XGArcade.Core.Scoring;

// ADR-0040: xG Grid's REQ-204/205 formula, extracted unchanged from
// ScoreLockingService — a pure wrap of UniquenessCalculator.Calculate +
// ScoringRules.PointsFromUniqueScore, same math, same order of
// operations, no reimplementation.
//
// GameKey is supplied by the composition root (Program.cs) at
// registration time, never hardcoded here — XGArcade.Core must not
// reference XGArcade.Games.XGGrid's GridGameModule.XGGridGameKey constant
// directly (ADR-0003). Same shape as RoundSchedulingOptions.GameKey.
public class UniquenessScoringStrategy : IScoringStrategy
{
    public required string GameKey { get; init; }

    // ADR-0021's golf-style direction, unchanged by ADR-0095's later
    // xG-Predict-only exception.
    public bool LowerIsBetter => true;

    // maxAttemptsForCell: no uniqueness-scoring use for this (xG Grid's
    // attempt cap doesn't factor into REQ-204/205's formula) — ignored,
    // same as ScoringResult.FinalUniquenessScore being non-null here is
    // this strategy's own concept, not maxAttemptsForCell's.
    public ScoringResult ScoreCorrectGuess(Guess guess, IReadOnlyCollection<Guess> correctGuessesForCell, int maxAttemptsForCell)
    {
        var uniqueScore = UniquenessCalculator.Calculate(correctGuessesForCell, guess.PlayerAnswerId!.Value);
        return new ScoringResult(uniqueScore, ScoringRules.PointsFromUniqueScore(uniqueScore));
    }
}

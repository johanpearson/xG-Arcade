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

    public ScoringResult ScoreCorrectGuess(IReadOnlyCollection<Guess> correctGuessesForCell, Guid myAnswerPlayerId)
    {
        var uniqueScore = UniquenessCalculator.Calculate(correctGuessesForCell, myAnswerPlayerId);
        return new ScoringResult(uniqueScore, ScoringRules.PointsFromUniqueScore(uniqueScore));
    }
}

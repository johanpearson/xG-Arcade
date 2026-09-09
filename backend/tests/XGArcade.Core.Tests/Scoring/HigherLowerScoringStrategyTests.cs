using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;

namespace XGArcade.Core.Tests.Scoring;

// REQ-1505/docs/requirements-document.md §4.16: xG Higher/Lower's
// FinalPoints-equals-streak-length formula — the simplest of this
// codebase's IScoringStrategy implementations, but still small enough to
// exercise directly rather than via a separate calculator type, same
// precedent as XGPredictScoringStrategyTests/ClueEfficiencyScoringStrategyTests.
public class HigherLowerScoringStrategyTests
{
    // streakLength, expectedPoints. 0 (never got a single guess right), a
    // mid-range value, and HigherLowerGenerationOptions' default
    // ComparatorCount (10) — a full-length streak.
    [TestCase(0, 0)]
    [TestCase(4, 4)]
    [TestCase(10, 10)]
    public void REQ1505_ScoreAttempt_FinalPointsEqualsStreakLength(int streakLength, int expectedPoints)
    {
        var strategy = new HigherLowerScoringStrategy { GameKey = "xg-higher-lower" };

        var result = strategy.ScoreAttempt(streakLength);

        Assert.That(result.FinalPoints, Is.EqualTo(expectedPoints));
    }

    [Test]
    public void REQ1505_ScoreAttempt_FinalUniquenessScoreIsAlwaysNull()
    {
        // xG Higher/Lower has no uniqueness concept at all — null, not
        // merely "not yet computed" (IScoringStrategy/ScoringResult's own
        // doc comments), same precedent as ClueEfficiencyScoringStrategy/
        // XGPredictScoringStrategy.
        var strategy = new HigherLowerScoringStrategy { GameKey = "xg-higher-lower" };

        var result = strategy.ScoreAttempt(5);

        Assert.That(result.FinalUniquenessScore, Is.Null);
    }

    [Test]
    public void REQ1505_LowerIsBetter_ReturnsFalse()
    {
        // Same "higher is better" exception ADR-0095 established for
        // xg-predict — a longer streak is better, not ADR-0021's default
        // golf-style direction. No dedicated ADR exists yet for this
        // specific GameKey's own exception (see this class's own doc
        // comment) — named descriptively rather than against an ADR number.
        var strategy = new HigherLowerScoringStrategy { GameKey = "xg-higher-lower" };

        Assert.That(strategy.LowerIsBetter, Is.False);
    }

    [Test]
    public void REQ1505_ScoreCorrectGuess_ThrowsNotSupportedException()
    {
        // xG Higher/Lower never writes Guess rows, so
        // ScoreLockingService.LockRoundScoresAsync can never call this
        // method for a real "xg-higher-lower" round — architecturally
        // unreachable, not merely "not yet implemented".
        var strategy = new HigherLowerScoringStrategy { GameKey = "xg-higher-lower" };
        var guess = new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            CellId = Guid.NewGuid(),
            SubmittedName = "Someone",
            PlayerAnswerId = Guid.NewGuid(),
            IsCorrect = true,
            AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        };

        Assert.Throws<NotSupportedException>(() => strategy.ScoreCorrectGuess(guess, [guess], maxAttemptsForCell: 1));
    }
}

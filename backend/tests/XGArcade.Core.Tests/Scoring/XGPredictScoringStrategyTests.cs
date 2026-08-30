using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;

namespace XGArcade.Core.Tests.Scoring;

// REQ-1304/ADR-0095/ADR-0096: xG Predict's three-independent-component
// per-match formula (outcome/home-goals/away-goals), higher-is-better —
// the one named exception to ADR-0021's platform-wide golf-style
// direction. Like ClueEfficiencyScoringStrategyTests, this formula is
// small enough to exercise directly rather than via a separate calculator
// type.
//
// "Test level: Unit" per requirements-document.md's REQ-1304 entry — "each
// of the 8 match/no-match combinations across the 3 components, plus an
// exact-scoreline case". Of the 8 (outcome, home-goals, away-goals) match/
// no-match combinations, exactly one is unreachable: (outcome NO match,
// home-goals MATCH, away-goals MATCH) — since outcome is itself derived
// from home/away goals, an exact match on both goal counts always implies
// an outcome match too. The 7 achievable combinations are covered below;
// the exact-scoreline case IS the all-three-match combination, so it is
// not a separate 9th case.
public class XGPredictScoringStrategyTests
{
    // predictedHome, predictedAway, actualHome, actualAway, expectedPoints.
    // Each case's comment names which of the 3 components (outcome/home/
    // away) match, to make the achieved combination explicit rather than
    // left to the reader to re-derive.
    [TestCase(0, 3, 2, 1, 0)]   // none match: predicted AwayWin(0-3) vs actual HomeWin(2-1); no goal matches
    [TestCase(3, 0, 2, 1, 10)]  // outcome only: both HomeWin (3-0 / 2-1); neither goal count matches
    [TestCase(2, 2, 2, 1, 10)]  // home-goals only: predicted Draw(2-2) vs actual HomeWin(2-1); home 2==2 matches, away 2!=1
    [TestCase(0, 1, 2, 1, 10)]  // away-goals only: predicted AwayWin(0-1) vs actual HomeWin(2-1); away 1==1 matches, home 0!=2
    [TestCase(2, 0, 2, 1, 20)]  // outcome + home-goals: both HomeWin, home 2==2 matches, away 0!=1
    [TestCase(2, 1, 3, 1, 20)]  // outcome + away-goals (REQ-1304's own example): both HomeWin, away 1==1 matches, home 2!=3
    [TestCase(2, 1, 2, 1, 30)]  // exact scoreline: all three components match
    public void REQ1304_ScorePrediction_ComputesIndependentComponentSumForEveryAchievableMatchCombination(
        int predictedHomeGoals, int predictedAwayGoals, int actualHomeGoals, int actualAwayGoals, int expectedPoints)
    {
        var strategy = new XGPredictScoringStrategy { GameKey = "xg-predict" };

        var result = strategy.ScorePrediction(predictedHomeGoals, predictedAwayGoals, actualHomeGoals, actualAwayGoals);

        Assert.That(result.FinalPoints, Is.EqualTo(expectedPoints));
    }

    [Test]
    public void REQ1304_ScorePrediction_FinalUniquenessScoreIsAlwaysNull()
    {
        // ADR-0040/ADR-0095: xG Predict has no uniqueness concept at all —
        // null, not merely "not yet computed" (IScoringStrategy/ScoringResult's
        // own doc comments), same precedent as ClueEfficiencyScoringStrategy.
        var strategy = new XGPredictScoringStrategy { GameKey = "xg-predict" };

        var result = strategy.ScorePrediction(2, 1, 2, 1);

        Assert.That(result.FinalUniquenessScore, Is.Null);
    }

    [Test]
    public void ADR0095_LowerIsBetter_ReturnsFalse()
    {
        // ADR-0095's named, single exception to ADR-0021's platform-wide
        // golf-style direction — every other registered IScoringStrategy
        // returns true.
        var strategy = new XGPredictScoringStrategy { GameKey = "xg-predict" };

        Assert.That(strategy.LowerIsBetter, Is.False);
    }

    [Test]
    public void ADR0096_ScoreCorrectGuess_ThrowsNotSupportedException()
    {
        // ADR-0096: xG Predict never writes Guess rows, so
        // ScoreLockingService.LockRoundScoresAsync can never call this
        // method for a real "xg-predict" round — architecturally
        // unreachable, not merely "not yet implemented".
        var strategy = new XGPredictScoringStrategy { GameKey = "xg-predict" };
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

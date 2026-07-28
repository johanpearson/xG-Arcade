using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;

namespace XGArcade.Core.Tests.Scoring;

// REQ-1206/S-083/ADR-0040: xG Path's clue-efficiency formula —
// round(cluesUsed / maxCluesForThisPuzzle * MaxPointsPerCell), golf-style
// (fewer clues used = fewer points = better). Unlike UniquenessScoringStrategy
// (wrapped around the separate UniquenessCalculator, see
// UniquenessCalculatorTests), ClueEfficiencyScoringStrategy's formula is
// small enough to live inline in the strategy itself, so it's exercised
// directly here rather than via a separate calculator type.
//
// "Test level: Unit" per requirements-document.md's REQ-1206 entry — the
// worst-case-when-never-solved and correct-strategy-resolved-for-xg-path
// criteria are covered end-to-end in
// XGArcade.Core.Tests/Rounds/PathScoreLockingServiceTests.cs instead, since
// they exercise ScoreLockingService/ScoringStrategyResolver behavior that
// this pure strategy type doesn't own on its own.
public class ClueEfficiencyScoringStrategyTests
{
    private static Guess CorrectGuess(int attemptCount) => new()
    {
        Id = Guid.NewGuid(),
        RoundId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        CellId = Guid.NewGuid(),
        SubmittedName = "Someone",
        PlayerAnswerId = Guid.NewGuid(),
        IsCorrect = true,
        AttemptCount = attemptCount,
        CreatedAt = DateTime.UtcNow,
    };

    // cluesUsed, maxAttemptsForCell, expectedPoints. maxAttemptsForCell
    // values deliberately include one non-7 case (2, 5) — REQ-1206 requires
    // the formula read maxCluesForThisPuzzle generically via the
    // maxAttemptsForCell parameter, never assume XGPathGameModule's fixed
    // 7-clue constant, so a range that isn't just "out of 7" is needed to
    // prove that.
    [TestCase(1, 7, 14)]   // 1/7*100 = 14.2857.. -> 14
    [TestCase(4, 7, 57)]   // 4/7*100 = 57.1428.. -> 57
    [TestCase(3, 7, 43)]   // 3/7*100 = 42.857..  -> 43
    [TestCase(7, 7, 100)]  // solved on the very last available clue -> worst-case points, same as never solved
    [TestCase(2, 5, 40)]   // non-7 max, exact
    [TestCase(1, 1, 100)]  // edge case: a single-clue puzzle, solved on the only clue available
    public void REQ1206_ScoreCorrectGuess_ComputesRoundedPointsFromCluesUsedOverMaxAttemptsForCell(
        int cluesUsed, int maxAttemptsForCell, int expectedPoints)
    {
        var strategy = new ClueEfficiencyScoringStrategy { GameKey = "xg-path" };
        var guess = CorrectGuess(cluesUsed);

        var result = strategy.ScoreCorrectGuess(guess, [guess], maxAttemptsForCell);

        Assert.That(result.FinalPoints, Is.EqualTo(expectedPoints));
    }

    [TestCase(1, 7)]
    [TestCase(4, 7)]
    [TestCase(7, 7)]
    public void REQ1206_ScoreCorrectGuess_FinalUniquenessScoreIsAlwaysNull(int cluesUsed, int maxAttemptsForCell)
    {
        // ADR-0040: xG Path has no uniqueness concept at all — null, not
        // merely "not yet computed" (IScoringStrategy/ScoringResult's own
        // doc comments).
        var strategy = new ClueEfficiencyScoringStrategy { GameKey = "xg-path" };
        var guess = CorrectGuess(cluesUsed);

        var result = strategy.ScoreCorrectGuess(guess, [guess], maxAttemptsForCell);

        Assert.That(result.FinalUniquenessScore, Is.Null);
    }

    [Test]
    public void REQ1206_ScoreCorrectGuess_IgnoresCorrectGuessesForCell_ScoresOnlyFromCluesUsedAndMaxAttempts()
    {
        // Every player who solves a given xG Path puzzle names the same
        // target player, so how many *other* correct guessers there are
        // (or what they answered) must have zero bearing on this guess's
        // score — unlike UniquenessScoringStrategy, which depends heavily
        // on correctGuessesForCell's contents.
        var strategy = new ClueEfficiencyScoringStrategy { GameKey = "xg-path" };
        var guess = CorrectGuess(attemptCount: 3);
        var otherGuessOnSameCell = CorrectGuess(attemptCount: 1);

        var resultWithLoneGuess = strategy.ScoreCorrectGuess(guess, [guess], maxAttemptsForCell: 7);
        var resultWithManyOthers = strategy.ScoreCorrectGuess(
            guess, [guess, otherGuessOnSameCell, CorrectGuess(7), CorrectGuess(7)], maxAttemptsForCell: 7);

        Assert.That(resultWithLoneGuess.FinalPoints, Is.EqualTo(resultWithManyOthers.FinalPoints));
        Assert.That(resultWithLoneGuess.FinalPoints, Is.EqualTo(43));
    }
}

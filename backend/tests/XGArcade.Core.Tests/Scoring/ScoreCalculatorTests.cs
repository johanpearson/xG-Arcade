using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;

namespace XGArcade.Core.Tests.Scoring;

// REQ-206 (docs/requirements-document.md §4.6): total score per round (and,
// by the same math, an all-time leaderboard total per REQ-401) is the sum
// of FinalPoints across a set of guesses.
public class ScoreCalculatorTests
{
    private static Guess Guess(int? finalPoints) => new()
    {
        Id = Guid.NewGuid(),
        RoundId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        CellId = Guid.NewGuid(),
        SubmittedName = "Someone",
        IsCorrect = finalPoints is not null,
        AttemptCount = 1,
        FinalPoints = finalPoints,
        CreatedAt = DateTime.UtcNow,
    };

    [Test]
    public void REQ206_CalculateTotalPoints_MultipleLockedGuesses_SumsFinalPointsAcrossAllOfThem()
    {
        var guesses = new[] { Guess(100), Guess(50), Guess(0) };

        var total = ScoreCalculator.CalculateTotalPoints(guesses);

        Assert.That(total, Is.EqualTo(150));
    }

    [Test]
    public void REQ206_CalculateTotalPoints_GuessWithNullFinalPoints_ContributesZero()
    {
        // A round still active (not yet closed via REQ-205) has guesses
        // whose FinalPoints is null — it must count as 0, not be skipped
        // in a way that would change other guesses' contribution, and must
        // not throw.
        var guesses = new[] { Guess(100), Guess(null) };

        var total = ScoreCalculator.CalculateTotalPoints(guesses);

        Assert.That(total, Is.EqualTo(100));
    }

    [Test]
    public void REQ206_CalculateTotalPoints_EmptyCollection_SumsToZero()
    {
        // REQ-206: "unanswered cells count as 0 points" — there is no
        // placeholder Guess row for a cell the player never attempted, so
        // an empty collection (e.g. a player who answered nothing in the
        // round) is exactly what that maps to.
        var total = ScoreCalculator.CalculateTotalPoints(Array.Empty<Guess>());

        Assert.That(total, Is.EqualTo(0));
    }
}

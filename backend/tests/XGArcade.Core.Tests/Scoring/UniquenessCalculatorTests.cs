using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;

namespace XGArcade.Core.Tests.Scoring;

// REQ-204 (docs/requirements-document.md §4.4): the pure
// unique_percent = 1 - (players_with_the_same_correct_player /
// players_with_a_correct_guess_for_this_cell) formula, shared by the live
// read path (RoundEndpoints) and REQ-205's round-close locking
// (RoundCloseService). Every case here passes only correct guesses, exactly
// as UniquenessCalculator.Calculate itself requires — the "incorrect
// guesses never enter the calculation" half of REQ-204 is instead pinned
// down at the call-site level, in RoundCloseServiceScoringTests and
// CurrentRoundEndpointTests, since a caller who filters correctly never
// gives this pure method anything to exclude in the first place.
public class UniquenessCalculatorTests
{
    private static Guess CorrectGuess(Guid cellId, Guid playerAnswerId) => new()
    {
        Id = Guid.NewGuid(),
        RoundId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        CellId = cellId,
        SubmittedName = "Someone",
        PlayerAnswerId = playerAnswerId,
        IsCorrect = true,
        AttemptCount = 1,
        CreatedAt = DateTime.UtcNow,
    };

    [Test]
    public void REQ204_Calculate_SingleCorrectGuessForCell_ReturnsZero()
    {
        // A lone correct guesser is 100% of the correct-guess population
        // that picked their answer (1/1) — by the formula, that's 0%
        // unique. This is the intended, literal behavior of the formula,
        // not a bug: uniqueness only ever drops as more distinct correct
        // answers appear among later solvers.
        var cellId = Guid.NewGuid();
        var playerAnswerId = Guid.NewGuid();
        var guesses = new[] { CorrectGuess(cellId, playerAnswerId) };

        var result = UniquenessCalculator.Calculate(guesses, playerAnswerId);

        Assert.That(result, Is.EqualTo(0.0));
    }

    [Test]
    public void REQ204_Calculate_TwoGuessersWithDifferentCorrectAnswers_EachScoresFiftyPercentUnique()
    {
        var cellId = Guid.NewGuid();
        var firstPlayerAnswerId = Guid.NewGuid();
        var secondPlayerAnswerId = Guid.NewGuid();
        var guesses = new[]
        {
            CorrectGuess(cellId, firstPlayerAnswerId),
            CorrectGuess(cellId, secondPlayerAnswerId),
        };

        var firstResult = UniquenessCalculator.Calculate(guesses, firstPlayerAnswerId);
        var secondResult = UniquenessCalculator.Calculate(guesses, secondPlayerAnswerId);

        Assert.That(firstResult, Is.EqualTo(0.5));
        Assert.That(secondResult, Is.EqualTo(0.5));
    }

    [Test]
    public void REQ204_Calculate_MultipleGuessersSharingOneAnswer_ReturnsLowerPercentForThatAnswer()
    {
        // Three correct guessers total; two of them picked the same
        // answer. For the shared answer: 1 - 2/3 ≈ 0.333. For the lone
        // distinct answer: 1 - 1/3 ≈ 0.667 — rarer answers score higher.
        var cellId = Guid.NewGuid();
        var sharedPlayerAnswerId = Guid.NewGuid();
        var distinctPlayerAnswerId = Guid.NewGuid();
        var guesses = new[]
        {
            CorrectGuess(cellId, sharedPlayerAnswerId),
            CorrectGuess(cellId, sharedPlayerAnswerId),
            CorrectGuess(cellId, distinctPlayerAnswerId),
        };

        var sharedResult = UniquenessCalculator.Calculate(guesses, sharedPlayerAnswerId);
        var distinctResult = UniquenessCalculator.Calculate(guesses, distinctPlayerAnswerId);

        Assert.That(sharedResult, Is.EqualTo(1.0 - 2.0 / 3.0).Within(1e-9));
        Assert.That(distinctResult, Is.EqualTo(1.0 - 1.0 / 3.0).Within(1e-9));
        Assert.That(distinctResult, Is.GreaterThan(sharedResult), "a rarer correct answer must score more unique than a commonly-shared one");
    }

    [Test]
    public void REQ204_Calculate_ZeroCorrectGuesses_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            UniquenessCalculator.Calculate(Array.Empty<Guess>(), Guid.NewGuid()));
    }
}

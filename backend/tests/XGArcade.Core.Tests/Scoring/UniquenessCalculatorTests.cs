using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;

namespace XGArcade.Core.Tests.Scoring;

// REQ-204 (docs/requirements-document.md §4.4): the pure
// unique_percent = 1 - (other_correct_guessers_with_the_same_answer /
// other_correct_guessers_for_this_cell) formula (ADR-0020 — the comparison
// excludes the guesser's own guess from both sides), shared by the live
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
    public void REQ204_Calculate_SingleCorrectGuessForCell_ReturnsOne()
    {
        // A lone correct guesser has no *other* correct guesser to compare
        // against yet — ADR-0020: that's vacuously 100% unique, not 0%.
        // Being the first/only correct answer for a cell must score
        // maximally, never minimally.
        var cellId = Guid.NewGuid();
        var playerAnswerId = Guid.NewGuid();
        var guesses = new[] { CorrectGuess(cellId, playerAnswerId) };

        var result = UniquenessCalculator.Calculate(guesses, playerAnswerId);

        Assert.That(result, Is.EqualTo(1.0));
    }

    [Test]
    public void REQ204_Calculate_TwoGuessersWithDifferentCorrectAnswers_EachScoresFullyUnique()
    {
        // Neither guesser's *other* correct guesser (there's exactly one
        // each) shares their answer, so each is 100% unique relative to the
        // other — ADR-0020.
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

        Assert.That(firstResult, Is.EqualTo(1.0));
        Assert.That(secondResult, Is.EqualTo(1.0));
    }

    [Test]
    public void REQ204_Calculate_MultipleGuessersSharingOneAnswer_ReturnsLowerPercentForThatAnswer()
    {
        // Three correct guessers total; two of them (including "me") picked
        // the same answer, one picked a distinct answer. For the shared
        // answer: of my 2 *other* correct guessers, 1 shares my answer —
        // 1 - 1/2 = 0.5. For the distinct answer: of its 2 other correct
        // guessers, 0 share it — 1 - 0/2 = 1.0. Rarer answers still score
        // higher.
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

        Assert.That(sharedResult, Is.EqualTo(0.5).Within(1e-9));
        Assert.That(distinctResult, Is.EqualTo(1.0).Within(1e-9));
        Assert.That(distinctResult, Is.GreaterThan(sharedResult), "a rarer correct answer must score more unique than a commonly-shared one");
    }

    [Test]
    public void REQ204_Calculate_ZeroCorrectGuesses_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            UniquenessCalculator.Calculate(Array.Empty<Guess>(), Guid.NewGuid()));
    }
}

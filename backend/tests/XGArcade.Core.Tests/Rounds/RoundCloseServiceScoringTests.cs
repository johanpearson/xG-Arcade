using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Rounds;
using XGArcade.Core.Scoring;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Tests.Rounds;

// REQ-205 (docs/requirements-document.md §4.5): the "locks
// FinalUniquenessScore/FinalPoints permanently at round close" half built in
// S-011 — see RoundCloseServiceTests for the EndTime-pull-forward half (S-008).
public class RoundCloseServiceScoringTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IRoundRepository _roundRepository = null!;
    private IGuessRepository _guessRepository = null!;
    private RoundCloseService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _roundRepository = new RoundRepository(_dbContext);
        _guessRepository = new GuessRepository(_dbContext);
        _service = new RoundCloseService(_roundRepository, new ScoreLockingService(_guessRepository));
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private async Task<Round> SeedRoundAsync(DateTime startTime, DateTime endTime)
    {
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = "xg-grid",
            GameInstanceId = Guid.NewGuid(),
            StartTime = startTime,
            EndTime = endTime,
            AllowGuessChange = true,
        };
        _dbContext.Rounds.Add(round);
        await _dbContext.SaveChangesAsync();
        return round;
    }

    // Seeds a Guess row directly into the in-memory DbContext — RoundCloseService
    // reads via IGuessRepository.GetByRoundIdAsync, which reads
    // XGArcadeDbContext.Guesses directly, so no special repository test
    // double is needed to exercise it.
    private async Task<Guess> SeedGuessAsync(
        Guid roundId, Guid cellId, bool isCorrect, Guid? playerAnswerId = null)
    {
        var guess = new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = roundId,
            UserId = Guid.NewGuid(),
            CellId = cellId,
            SubmittedName = "Someone",
            PlayerAnswerId = playerAnswerId,
            IsCorrect = isCorrect,
            AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Guesses.Add(guess);
        await _dbContext.SaveChangesAsync();
        return guess;
    }

    [Test]
    public async Task REQ205_CloseRoundAsync_LoneCorrectGuessOnCell_LocksFinalUniquenessScoreOneAndFinalPointsMax()
    {
        // A lone correct guesser has no *other* correct guesser to compare
        // against — ADR-0020: that's vacuously 100% unique, hence full
        // points, not 0.
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var cellId = Guid.NewGuid();
        var playerAnswerId = Guid.NewGuid();
        var guess = await SeedGuessAsync(round.Id, cellId, isCorrect: true, playerAnswerId);

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var persisted = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == guess.Id);
        Assert.That(persisted.FinalUniquenessScore, Is.EqualTo(1.0));
        Assert.That(persisted.FinalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell));
    }

    [Test]
    public async Task REQ205_CloseRoundAsync_TwoCorrectGuessesWithDifferentAnswersOnSameCell_EachLocksFullyUniqueAndMaxPoints()
    {
        // Neither guess's one other correct guesser shares its answer, so
        // both lock as 100% unique — ADR-0020.
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var cellId = Guid.NewGuid();
        var firstAnswerId = Guid.NewGuid();
        var secondAnswerId = Guid.NewGuid();
        var firstGuess = await SeedGuessAsync(round.Id, cellId, isCorrect: true, firstAnswerId);
        var secondGuess = await SeedGuessAsync(round.Id, cellId, isCorrect: true, secondAnswerId);

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var persistedFirst = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == firstGuess.Id);
        var persistedSecond = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == secondGuess.Id);
        Assert.That(persistedFirst.FinalUniquenessScore, Is.EqualTo(1.0));
        Assert.That(persistedFirst.FinalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell));
        Assert.That(persistedSecond.FinalUniquenessScore, Is.EqualTo(1.0));
        Assert.That(persistedSecond.FinalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell));
    }

    [Test]
    public async Task REQ205_CloseRoundAsync_TwoOfThreeCorrectGuessesShareAnAnswer_SharedPairLocksHalfAndDistinctLocksFull()
    {
        // Three correct guessers on one cell; two share the same answer, one
        // is distinct. Each of the sharing pair has 1 *other* correct
        // guesser sharing its answer out of 2 others total (0.5); the
        // distinct answer has 0 out of 2 (1.0) — ADR-0020.
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var cellId = Guid.NewGuid();
        var sharedAnswerId = Guid.NewGuid();
        var distinctAnswerId = Guid.NewGuid();
        var firstSharedGuess = await SeedGuessAsync(round.Id, cellId, isCorrect: true, sharedAnswerId);
        var secondSharedGuess = await SeedGuessAsync(round.Id, cellId, isCorrect: true, sharedAnswerId);
        var distinctGuess = await SeedGuessAsync(round.Id, cellId, isCorrect: true, distinctAnswerId);

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var persistedFirstShared = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == firstSharedGuess.Id);
        var persistedSecondShared = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == secondSharedGuess.Id);
        var persistedDistinct = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == distinctGuess.Id);
        Assert.That(persistedFirstShared.FinalUniquenessScore, Is.EqualTo(0.5));
        Assert.That(persistedFirstShared.FinalPoints, Is.EqualTo(50));
        Assert.That(persistedSecondShared.FinalUniquenessScore, Is.EqualTo(0.5));
        Assert.That(persistedSecondShared.FinalPoints, Is.EqualTo(50));
        Assert.That(persistedDistinct.FinalUniquenessScore, Is.EqualTo(1.0));
        Assert.That(persistedDistinct.FinalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell));
    }

    [Test]
    public async Task REQ205_CloseRoundAsync_IncorrectGuess_LocksNullUniquenessScoreAndZeroPoints_RegardlessOfCellActivity()
    {
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var cellId = Guid.NewGuid();
        var incorrectGuess = await SeedGuessAsync(round.Id, cellId, isCorrect: false);
        // Correct guesses also happen on the same cell, from other players —
        // must have zero bearing on the incorrect guess's locked values.
        await SeedGuessAsync(round.Id, cellId, isCorrect: true, Guid.NewGuid());
        await SeedGuessAsync(round.Id, cellId, isCorrect: true, Guid.NewGuid());

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var persisted = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == incorrectGuess.Id);
        Assert.That(persisted.FinalUniquenessScore, Is.Null);
        Assert.That(persisted.FinalPoints, Is.EqualTo(0));
    }

    // Regression test for review-2026-07-07-design.md finding 2: an earlier
    // draft's denominator counted ALL guesses for a cell, including
    // incorrect/burned attempts. With that bug, this scenario's sole
    // correct guesser would incorrectly score FinalUniquenessScore=0.5
    // (1 - 1/2, treating the incorrect guess as part of the denominator)
    // instead of the correct 1.0 (ADR-0020: a lone correct guesser, with the
    // incorrect guess never entering the denominator, is maximally unique).
    // Pinning this down so it can never silently regress.
    [Test]
    public async Task REQ205_CloseRoundAsync_CellWithOneCorrectAndOneIncorrectGuess_LocksCorrectGuessAtFullNotHalf()
    {
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var cellId = Guid.NewGuid();
        var playerAnswerId = Guid.NewGuid();
        var correctGuess = await SeedGuessAsync(round.Id, cellId, isCorrect: true, playerAnswerId);
        await SeedGuessAsync(round.Id, cellId, isCorrect: false);

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var persisted = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == correctGuess.Id);
        Assert.That(persisted.FinalUniquenessScore, Is.EqualTo(1.0),
            "the incorrect guess on this cell must never be counted in the uniqueness denominator (review-2026-07-07-design.md finding 2)");
        Assert.That(persisted.FinalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell));
    }
}

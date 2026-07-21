using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
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
    private const string GameKey = "xg-grid";

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IRoundRepository _roundRepository = null!;
    private IGuessRepository _guessRepository = null!;
    private FakeGameModule _fakeGameModule = null!;
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
        // Defaults to no cells (ADR-0021's unanswered-cell materialization
        // is then a no-op) — tests that specifically exercise it override
        // GetCellIdsResult explicitly.
        _fakeGameModule = new FakeGameModule(GameKey);
        var gameModuleResolver = new GameModuleResolver([_fakeGameModule]);
        _service = new RoundCloseService(
            _roundRepository,
            new ScoreLockingService(_guessRepository, _roundRepository, gameModuleResolver));
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private async Task<Round> SeedRoundAsync(DateTime startTime, DateTime endTime)
    {
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GameKey,
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
    public async Task REQ205_CloseRoundAsync_LoneCorrectGuessOnCell_LocksFinalUniquenessScoreOneAndFinalPointsZero()
    {
        // A lone correct guesser has no *other* correct guesser to compare
        // against — ADR-0020: that's vacuously 100% unique. ADR-0021:
        // lowest wins, so 100% unique locks the BEST score, 0, not the max.
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var cellId = Guid.NewGuid();
        var playerAnswerId = Guid.NewGuid();
        var guess = await SeedGuessAsync(round.Id, cellId, isCorrect: true, playerAnswerId);

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var persisted = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == guess.Id);
        Assert.That(persisted.FinalUniquenessScore, Is.EqualTo(1.0));
        Assert.That(persisted.FinalPoints, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ205_CloseRoundAsync_TwoCorrectGuessesWithDifferentAnswersOnSameCell_EachLocksFullyUniqueAndZeroPoints()
    {
        // Neither guess's one other correct guesser shares its answer, so
        // both lock as 100% unique (ADR-0020) — the best score, 0, under
        // ADR-0021's lowest-wins model.
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
        Assert.That(persistedFirst.FinalPoints, Is.EqualTo(0));
        Assert.That(persistedSecond.FinalUniquenessScore, Is.EqualTo(1.0));
        Assert.That(persistedSecond.FinalPoints, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ205_CloseRoundAsync_TwoOfThreeCorrectGuessesShareAnAnswer_SharedPairLocksHalfAndDistinctLocksZero()
    {
        // Three correct guessers on one cell; two share the same answer, one
        // is distinct. Each of the sharing pair has 1 *other* correct
        // guesser sharing its answer out of 2 others total (0.5, unaffected
        // by ADR-0021 since the midpoint is its own inverse); the distinct
        // answer has 0 out of 2 (1.0 unique -> 0 points, the best score).
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
        Assert.That(persistedDistinct.FinalPoints, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ205_CloseRoundAsync_IncorrectGuess_LocksNullUniquenessScoreAndMaxPoints_RegardlessOfCellActivity()
    {
        // ADR-0021: an incorrect guess locks at the WORST score
        // (MaxPointsPerCell), not 0 — under lowest-wins, 0 would otherwise
        // tie a wrong answer with the best possible correct one.
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
        Assert.That(persisted.FinalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell));
    }

    // Regression test for review-2026-07-07-design.md finding 2: an earlier
    // draft's denominator counted ALL guesses for a cell, including
    // incorrect/burned attempts. With that bug, this scenario's sole
    // correct guesser would incorrectly score FinalUniquenessScore=0.5
    // (1 - 1/2, treating the incorrect guess as part of the denominator)
    // instead of the correct 1.0 (ADR-0020: a lone correct guesser, with the
    // incorrect guess never entering the denominator, is maximally unique;
    // ADR-0021: which locks the best score, 0, not the max).
    // Pinning this down so it can never silently regress.
    [Test]
    public async Task REQ205_CloseRoundAsync_CellWithOneCorrectAndOneIncorrectGuess_LocksCorrectGuessAtZeroNotFifty()
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
        Assert.That(persisted.FinalPoints, Is.EqualTo(0));
    }

    // ---- ADR-0021: unanswered-cell penalty at round close -----------------

    [Test]
    public async Task REQ206_CloseRoundAsync_ParticipantNeverAttemptedACell_MaterializesItAsAnIncorrectMaxPointsGuess()
    {
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var attemptedCellId = Guid.NewGuid();
        var unattemptedCellId = Guid.NewGuid();
        _fakeGameModule.GetCellIdsResult = _ => [attemptedCellId, unattemptedCellId];
        var participantId = Guid.NewGuid();
        var guess = new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = round.Id,
            UserId = participantId,
            CellId = attemptedCellId,
            SubmittedName = "Someone",
            PlayerAnswerId = Guid.NewGuid(),
            IsCorrect = true,
            AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Guesses.Add(guess);
        await _dbContext.SaveChangesAsync();

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var materialized = await _dbContext.Guesses.AsNoTracking()
            .SingleAsync(g => g.RoundId == round.Id && g.UserId == participantId && g.CellId == unattemptedCellId);
        Assert.That(materialized.IsCorrect, Is.False);
        Assert.That(materialized.PlayerAnswerId, Is.Null);
        Assert.That(materialized.AttemptCount, Is.EqualTo(0), "distinguishes a never-attempted cell from a real wrong guess");
        Assert.That(materialized.FinalUniquenessScore, Is.Null);
        Assert.That(materialized.FinalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell));
    }

    [Test]
    public async Task REQ206_CloseRoundAsync_UserWithNoGuessesInRoundAtAll_NeverGetsAnyMaterializedGuesses()
    {
        // "Unanswered equals wrong guess" only applies to a round a player
        // actually participated in — a user who never opened this round at
        // all must not be penalized for it. A round with zero guesses at
        // all would pass this trivially (MaterializeUnansweredCellsAsync
        // short-circuits on an empty participant set before ever
        // considering anyone), so a real participant is seeded alongside
        // the non-participant to actually exercise the exclusion logic.
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var attemptedCellId = Guid.NewGuid();
        var otherCellId = Guid.NewGuid();
        _fakeGameModule.GetCellIdsResult = _ => [attemptedCellId, otherCellId];
        var participantId = Guid.NewGuid();
        var nonParticipantId = Guid.NewGuid();
        _dbContext.Guesses.Add(new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = round.Id,
            UserId = participantId,
            CellId = attemptedCellId,
            SubmittedName = "Someone",
            PlayerAnswerId = Guid.NewGuid(),
            IsCorrect = true,
            AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var materializedForParticipant = await _dbContext.Guesses.AsNoTracking()
            .SingleOrDefaultAsync(g => g.RoundId == round.Id && g.UserId == participantId && g.CellId == otherCellId);
        Assert.That(materializedForParticipant, Is.Not.Null, "the real participant's unattempted cell must be materialized");

        var anyGuessForNonParticipant = await _dbContext.Guesses.AsNoTracking()
            .AnyAsync(g => g.RoundId == round.Id && g.UserId == nonParticipantId);
        Assert.That(anyGuessForNonParticipant, Is.False);
    }

    [Test]
    public async Task REQ206_CloseRoundAsync_CalledTwice_DoesNotDuplicateMaterializedGuesses()
    {
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var attemptedCellId = Guid.NewGuid();
        var unattemptedCellId = Guid.NewGuid();
        _fakeGameModule.GetCellIdsResult = _ => [attemptedCellId, unattemptedCellId];
        var participantId = Guid.NewGuid();
        _dbContext.Guesses.Add(new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = round.Id,
            UserId = participantId,
            CellId = attemptedCellId,
            SubmittedName = "Someone",
            PlayerAnswerId = Guid.NewGuid(),
            IsCorrect = true,
            AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);
        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var count = await _dbContext.Guesses.AsNoTracking()
            .CountAsync(g => g.RoundId == round.Id && g.UserId == participantId && g.CellId == unattemptedCellId);
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ206_CloseRoundAsync_RoundGameKeyHasNoRegisteredGameModule_ThrowsInvalidOperationException()
    {
        // Defensive regression test: MaterializeUnansweredCellsAsync resolves
        // the round's game module via IGameModuleResolver before it can look
        // up cell ids (ADR-0021, via IGameModule.GetCellIdsAsync per
        // ADR-0003). If a Round's GameKey somehow has no registered
        // IGameModule (a data/deploy inconsistency — never expected in
        // practice, since RoundGenerationService already resolves the same
        // GameKey when the round is created), this must fail loudly
        // (InvalidOperationException, GameModuleResolver's own contract),
        // never silently skip the unanswered-cell penalty and let those
        // cells default to 0 points — the BEST possible score under
        // ADR-0021's lowest-wins model, which would make an unregistered
        // GameKey quietly the most exploitable outcome rather than an error.
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = "some-unregistered-game",
            GameInstanceId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(-1),
            EndTime = DateTime.UtcNow.AddDays(1),
            AllowGuessChange = true,
        };
        _dbContext.Rounds.Add(round);
        // At least one participant, so MaterializeUnansweredCellsAsync
        // doesn't short-circuit on its empty-participant-set check before
        // ever reaching the resolver.
        _dbContext.Guesses.Add(new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = round.Id,
            UserId = Guid.NewGuid(),
            CellId = Guid.NewGuid(),
            SubmittedName = "Someone",
            PlayerAnswerId = Guid.NewGuid(),
            IsCorrect = true,
            AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _service.CloseRoundAsync(round.Id, DateTime.UtcNow));

        Assert.That(ex!.Message, Does.Contain("some-unregistered-game"),
            "the exception must propagate all the way out of CloseRoundAsync, not be caught/swallowed anywhere in RoundCloseService/ScoreLockingService");
    }

    // ---- REQ-717/ADR-0036: a guest's guess counts fully toward another
    // account's uniqueness, never excluded, weighted differently, or
    // flagged ------------------------------------------------------------
    // ScoreLockingService/UniquenessCalculator never query the Users table
    // at all (only Round/Guess) — every test above already exercises this
    // with plain, unlabeled Guids for UserId, which implicitly proves no
    // special-casing is even possible here. This test instead ties that
    // "zero new code path" design claim to two real User rows (one guest,
    // one not) sharing an answer, directly operationalizing REQ-717's
    // "Scoring and uniqueness — no special-casing" acceptance criterion.

    private async Task<Guid> SeedUserAsync(bool isGuest)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = isGuest ? null : $"{Guid.NewGuid()}@example.com",
            DisplayName = isGuest ? $"Guest{Guid.NewGuid():N}"[..12] : $"Player-{Guid.NewGuid():N}",
            EmailConfirmed = !isGuest,
            IsGuest = isGuest,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guess> SeedGuessForUserAsync(Guid roundId, Guid userId, Guid cellId, bool isCorrect, Guid? playerAnswerId = null)
    {
        var guess = new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = roundId,
            UserId = userId,
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
    public async Task REQ717_CloseRoundAsync_GuestSharesAnswerWithARealAccount_CountsFullyTowardTheRealAccountsUniqueness()
    {
        // Three correct guessers on one cell: a guest and a real account
        // share one answer, a second real account has a distinct one —
        // exactly REQ205_CloseRoundAsync_TwoOfThreeCorrectGuessesShareAnAnswer_
        // SharedPairLocksHalfAndDistinctLocksZero's own shape above, except
        // one of the sharing pair is a real Guest User row rather than an
        // unlabeled Guid. If the guest's guess were silently excluded from
        // the "other correct guessers" denominator, the real account
        // sharing its answer would instead score as if uniquely correct
        // (1.0, 0 points) rather than the 0.5/50 it must score here.
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var guestUserId = await SeedUserAsync(isGuest: true);
        var realUserId = await SeedUserAsync(isGuest: false);
        var distinctUserId = await SeedUserAsync(isGuest: false);
        var cellId = Guid.NewGuid();
        var sharedAnswerId = Guid.NewGuid();
        var distinctAnswerId = Guid.NewGuid();
        var guestGuess = await SeedGuessForUserAsync(round.Id, guestUserId, cellId, isCorrect: true, sharedAnswerId);
        var realGuess = await SeedGuessForUserAsync(round.Id, realUserId, cellId, isCorrect: true, sharedAnswerId);
        var distinctGuess = await SeedGuessForUserAsync(round.Id, distinctUserId, cellId, isCorrect: true, distinctAnswerId);

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var persistedReal = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == realGuess.Id);
        var persistedGuest = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == guestGuess.Id);
        var persistedDistinct = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == distinctGuess.Id);
        Assert.That(persistedReal.FinalUniquenessScore, Is.EqualTo(0.5), "the guest's shared guess must count as one of the real account's 'other correct guessers', not be excluded");
        Assert.That(persistedReal.FinalPoints, Is.EqualTo(50));
        Assert.That(persistedGuest.FinalUniquenessScore, Is.EqualTo(0.5), "the guest's own guess is scored identically to any other account — never flagged/excluded from its own uniqueness either");
        Assert.That(persistedGuest.FinalPoints, Is.EqualTo(50));
        Assert.That(persistedDistinct.FinalUniquenessScore, Is.EqualTo(1.0));
        Assert.That(persistedDistinct.FinalPoints, Is.EqualTo(0));
    }
}

using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Core.Scoring;
using XGArcade.Core.Tests.Rounds;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Tests.Scoring;

// REQ-201/202/210 (docs/requirements-document.md §4.2): GuessSubmissionService
// is COMP-04 (Core.Scoring)'s single entry point for guess acceptance —
// REQ-207/208/209's name-resolution work is entirely the owning game
// module's job (GridGameModuleTests covers that), so here the game module is
// a hand-rolled FakeGameModule whose ScoreResult is fully controlled by each
// test, same no-mocking-framework pattern as RoundGenerationServiceTests.
public class GuessSubmissionServiceTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IRoundRepository _roundRepository = null!;
    private IGuessRepository _guessRepository = null!;
    private IPlayerStoreRepository _playerStoreRepository = null!;
    private FakeGameModule _gameModule = null!;

    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _roundRepository = new RoundRepository(_dbContext);
        _guessRepository = new GuessRepository(_dbContext);
        _playerStoreRepository = new PlayerStoreRepository(_dbContext);
        _gameModule = new FakeGameModule("xg-grid");
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private GuessSubmissionService BuildService() =>
        new(_roundRepository, _guessRepository, new GameModuleResolver([_gameModule]), _playerStoreRepository, new FixedTimeProvider(Now));

    private async Task<Guid> SeedPlayerAsync(string fullName)
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = fullName, WikidataQid = $"Qtest-{Guid.NewGuid()}" };
        _dbContext.Players.Add(player);
        await _dbContext.SaveChangesAsync();
        return player.Id;
    }

    private async Task<Round> SeedRoundAsync(DateTime startTime, DateTime endTime, bool allowGuessChange)
    {
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = "xg-grid",
            GameInstanceId = Guid.NewGuid(),
            StartTime = startTime,
            EndTime = endTime,
            AllowGuessChange = allowGuessChange,
        };
        _dbContext.Rounds.Add(round);
        await _dbContext.SaveChangesAsync();
        return round;
    }

    private Task<Round> SeedActiveRoundAsync(bool allowGuessChange = true) =>
        SeedRoundAsync(Now.UtcDateTime.AddDays(-1), Now.UtcDateTime.AddDays(1), allowGuessChange);

    private static void SetNextResult(FakeGameModule gameModule, bool isCorrect, Guid? playerAnswerId = null) =>
        gameModule.ScoreSubmissionResult = (_, _, _) => new ScoreResult { IsCorrect = isCorrect, PlayerAnswerId = playerAnswerId };

    // ---- REQ-201: submit a guess ------------------------------------------

    [Test]
    public async Task REQ201_SubmitGuess_ActiveRound_StoresGuessWithUserCellAnswerAndTimestamp()
    {
        var round = await SeedActiveRoundAsync();
        var userId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        var playerAnswerId = Guid.NewGuid();
        SetNextResult(_gameModule, isCorrect: true, playerAnswerId);
        var service = BuildService();

        var result = await service.SubmitGuessAsync(round.Id, userId, cellId, "Thierry Henry");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.Accepted));
        var stored = await _guessRepository.GetAsync(round.Id, userId, cellId);
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!.RoundId, Is.EqualTo(round.Id));
        Assert.That(stored.UserId, Is.EqualTo(userId));
        Assert.That(stored.CellId, Is.EqualTo(cellId));
        Assert.That(stored.SubmittedName, Is.EqualTo("Thierry Henry"));
        Assert.That(stored.PlayerAnswerId, Is.EqualTo(playerAnswerId));
        Assert.That(stored.CreatedAt, Is.EqualTo(Now.UtcDateTime));
    }

    [TestCase(1, 4, TestName = "REQ201_SubmitGuess_UpcomingRound_RejectedWithRoundNotActive")]
    [TestCase(-4, -1, TestName = "REQ201_SubmitGuess_ClosedRound_RejectedWithRoundNotActive")]
    public async Task REQ201_SubmitGuess_RoundNotCurrentlyActive_RejectedWithRoundNotActive(int startOffsetDays, int endOffsetDays)
    {
        var round = await SeedRoundAsync(
            Now.UtcDateTime.AddDays(startOffsetDays), Now.UtcDateTime.AddDays(endOffsetDays), allowGuessChange: true);
        var service = BuildService();

        var result = await service.SubmitGuessAsync(round.Id, Guid.NewGuid(), Guid.NewGuid(), "Thierry Henry");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.RoundNotActive));
        Assert.That(_gameModule.ScoreSubmissionAsyncCallCount, Is.Zero, "an inactive round must reject before any name resolution work");
    }

    [Test]
    public async Task REQ201_SubmitGuess_UnknownRoundId_RejectedWithRoundNotFound()
    {
        var service = BuildService();

        var result = await service.SubmitGuessAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Thierry Henry");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.RoundNotFound));
    }

    [Test]
    public async Task REQ201_SubmitGuess_Resubmission_OverwritesExistingGuessRow_NotDuplicateInsert()
    {
        var round = await SeedActiveRoundAsync(allowGuessChange: true);
        var userId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        SetNextResult(_gameModule, isCorrect: false);
        var service = BuildService();
        await service.SubmitGuessAsync(round.Id, userId, cellId, "Wrong Name");

        await service.SubmitGuessAsync(round.Id, userId, cellId, "Second Guess");

        var rowCount = await _dbContext.Guesses.CountAsync(g => g.RoundId == round.Id && g.UserId == userId && g.CellId == cellId);
        Assert.That(rowCount, Is.EqualTo(1), "a resubmission must overwrite the existing row, never insert a second one");
        var stored = await _guessRepository.GetAsync(round.Id, userId, cellId);
        Assert.That(stored!.SubmittedName, Is.EqualTo("Second Guess"));
        Assert.That(stored.AttemptCount, Is.EqualTo(2));
    }

    // ---- Frontend name-display fix: canonical name for a correct guess -----

    [Test]
    public async Task SubmitGuess_Correct_ReturnsCanonicalPlayerFullName_NotTheRawAsTypedSubmittedName()
    {
        var round = await SeedActiveRoundAsync();
        var playerAnswerId = await SeedPlayerAsync("Thierry Henry");
        SetNextResult(_gameModule, isCorrect: true, playerAnswerId);
        var service = BuildService();

        var result = await service.SubmitGuessAsync(round.Id, Guid.NewGuid(), Guid.NewGuid(), "thierry henry");

        Assert.That(result.ResolvedPlayerName, Is.EqualTo("Thierry Henry"));
    }

    [Test]
    public async Task SubmitGuess_Incorrect_ResolvedPlayerNameIsNull()
    {
        var round = await SeedActiveRoundAsync();
        SetNextResult(_gameModule, isCorrect: false);
        var service = BuildService();

        var result = await service.SubmitGuessAsync(round.Id, Guid.NewGuid(), Guid.NewGuid(), "Wrong Guess");

        Assert.That(result.ResolvedPlayerName, Is.Null);
    }

    // ---- REQ-202: guess locking (allow_guess_change) -----------------------

    [Test]
    public async Task REQ202_SubmitGuess_AllowGuessChangeFalse_SecondAttempt_RejectedWithGuessChangeNotAllowed()
    {
        var round = await SeedActiveRoundAsync(allowGuessChange: false);
        var userId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        SetNextResult(_gameModule, isCorrect: false);
        var service = BuildService();
        await service.SubmitGuessAsync(round.Id, userId, cellId, "First Guess");

        var result = await service.SubmitGuessAsync(round.Id, userId, cellId, "Second Guess");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.GuessChangeNotAllowed));
        Assert.That(_gameModule.ScoreSubmissionAsyncCallCount, Is.EqualTo(1), "the rejected second attempt must never reach name resolution");
    }

    [Test]
    public async Task REQ202_SubmitGuess_AllowGuessChangeTrue_SecondAttempt_Accepted()
    {
        var round = await SeedActiveRoundAsync(allowGuessChange: true);
        var userId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        SetNextResult(_gameModule, isCorrect: false);
        var service = BuildService();
        await service.SubmitGuessAsync(round.Id, userId, cellId, "First Guess");
        SetNextResult(_gameModule, isCorrect: true, Guid.NewGuid());

        var result = await service.SubmitGuessAsync(round.Id, userId, cellId, "Second Guess");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.Accepted));
        Assert.That(result.AttemptCount, Is.EqualTo(2));
        Assert.That(result.IsCorrect, Is.True);
    }

    [Test]
    public async Task REQ202_SubmitGuess_AllowGuessChangeFalse_AlreadyCorrectlyLockedCell_RejectedWithCellAlreadySolved_NotGuessChangeNotAllowed()
    {
        // REQ-210's lock takes precedence over REQ-202's setting regardless
        // of its value — a distinct, specific reason, never folded into the
        // guess-change-disabled message.
        var round = await SeedActiveRoundAsync(allowGuessChange: false);
        var userId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        SetNextResult(_gameModule, isCorrect: true, Guid.NewGuid());
        var service = BuildService();
        await service.SubmitGuessAsync(round.Id, userId, cellId, "Correct Guess");

        var result = await service.SubmitGuessAsync(round.Id, userId, cellId, "Another Guess");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.CellAlreadySolved));
    }

    [Test]
    public async Task REQ202_SubmitGuess_AllowGuessChangeTrue_AttemptsExhausted_RejectedWithNoAttemptsRemaining_NotGuessChangeNotAllowed()
    {
        // REQ-210's attempt cap takes precedence over REQ-202's setting
        // regardless of its value — even with changes allowed, a 3rd attempt
        // is still a distinct "no attempts remaining" rejection.
        var round = await SeedActiveRoundAsync(allowGuessChange: true);
        var userId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        SetNextResult(_gameModule, isCorrect: false);
        var service = BuildService();
        await service.SubmitGuessAsync(round.Id, userId, cellId, "First Guess");
        await service.SubmitGuessAsync(round.Id, userId, cellId, "Second Guess");

        var result = await service.SubmitGuessAsync(round.Id, userId, cellId, "Third Guess");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.NoAttemptsRemaining));
    }

    // ---- REQ-210: two guesses per cell, locked immediately on correct -----

    [Test]
    public async Task REQ210_SubmitGuess_CorrectOnAttempt1_LocksImmediately_EvenThoughOnlyOneOfTwoAttemptsUsed()
    {
        var round = await SeedActiveRoundAsync(allowGuessChange: true);
        SetNextResult(_gameModule, isCorrect: true, Guid.NewGuid());
        var service = BuildService();

        var result = await service.SubmitGuessAsync(round.Id, Guid.NewGuid(), Guid.NewGuid(), "Correct Guess");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.Accepted));
        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.AttemptCount, Is.EqualTo(1));
        Assert.That(result.Locked, Is.True);
    }

    [Test]
    public async Task REQ210_SubmitGuess_ThirdAttemptAfterCorrectFirst_RejectedWithCellAlreadySolved_EvenWithGuessChangeAllowed()
    {
        var round = await SeedActiveRoundAsync(allowGuessChange: true);
        var userId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        SetNextResult(_gameModule, isCorrect: true, Guid.NewGuid());
        var service = BuildService();
        await service.SubmitGuessAsync(round.Id, userId, cellId, "Correct Guess");

        var result = await service.SubmitGuessAsync(round.Id, userId, cellId, "Another Guess");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.CellAlreadySolved));
    }

    [Test]
    public async Task REQ210_SubmitGuess_CorrectOnAttempt2_LocksWithAttemptCountTwo()
    {
        var round = await SeedActiveRoundAsync(allowGuessChange: true);
        var userId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        SetNextResult(_gameModule, isCorrect: false);
        var service = BuildService();
        await service.SubmitGuessAsync(round.Id, userId, cellId, "Wrong Guess");
        SetNextResult(_gameModule, isCorrect: true, Guid.NewGuid());

        var result = await service.SubmitGuessAsync(round.Id, userId, cellId, "Correct Guess");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.Accepted));
        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.AttemptCount, Is.EqualTo(2));
        Assert.That(result.Locked, Is.True);
    }

    [Test]
    public async Task REQ210_SubmitGuess_BothAttemptsWrong_LocksAsIncorrect()
    {
        var round = await SeedActiveRoundAsync(allowGuessChange: true);
        var userId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        SetNextResult(_gameModule, isCorrect: false);
        var service = BuildService();
        await service.SubmitGuessAsync(round.Id, userId, cellId, "Wrong Guess 1");

        var result = await service.SubmitGuessAsync(round.Id, userId, cellId, "Wrong Guess 2");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.Accepted));
        Assert.That(result.IsCorrect, Is.False);
        Assert.That(result.AttemptCount, Is.EqualTo(2));
        Assert.That(result.Locked, Is.True, "both attempts used without a correct answer must still lock the cell");
        var stored = await _guessRepository.GetAsync(round.Id, userId, cellId);
        Assert.That(stored!.IsCorrect, Is.False);
        Assert.That(stored.PlayerAnswerId, Is.Null);
    }

    // AllowGuessChange=false is deliberately not parameterized here: REQ-210's
    // preamble is explicitly scoped to "a cell where allow_guess_change is
    // true" — with it false, a genuine 2nd attempt is never reachable at all
    // (the 2nd submission itself is already rejected with GuessChangeNotAllowed
    // per REQ-202, see REQ202_SubmitGuess_AllowGuessChangeFalse_SecondAttempt_
    // RejectedWithGuessChangeNotAllowed above), so "a 3rd attempt after 2 used
    // attempts" cannot occur under that config in the first place.
    [Test]
    public async Task REQ210_SubmitGuess_ThirdAttemptAfterTwoWrongAttemptsUsed_RejectedWithNoAttemptsRemaining()
    {
        var round = await SeedActiveRoundAsync(allowGuessChange: true);
        var userId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        SetNextResult(_gameModule, isCorrect: false);
        var service = BuildService();
        await service.SubmitGuessAsync(round.Id, userId, cellId, "Wrong Guess 1");
        await service.SubmitGuessAsync(round.Id, userId, cellId, "Wrong Guess 2");

        var result = await service.SubmitGuessAsync(round.Id, userId, cellId, "Third Guess");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.NoAttemptsRemaining));
    }

    [Test]
    public async Task REQ210_SubmitGuess_AlreadyCorrectlyLockedCell_RejectedWithoutEverCallingGameModule()
    {
        // The literal acceptance criterion: REQ-210's lock/cap are "checked
        // before any name resolution work, not after" — the rejected
        // submission below must never increment the game module's call
        // count, proving IGameModule.ScoreSubmissionAsync was never invoked
        // for it.
        var round = await SeedActiveRoundAsync(allowGuessChange: true);
        var userId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        SetNextResult(_gameModule, isCorrect: true, Guid.NewGuid());
        var service = BuildService();
        await service.SubmitGuessAsync(round.Id, userId, cellId, "Correct Guess");
        Assert.That(_gameModule.ScoreSubmissionAsyncCallCount, Is.EqualTo(1));

        var result = await service.SubmitGuessAsync(round.Id, userId, cellId, "Second Guess");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.CellAlreadySolved));
        Assert.That(_gameModule.ScoreSubmissionAsyncCallCount, Is.EqualTo(1),
            "a rejected-by-REQ-210 submission must never reach the game module a second time");
    }

    [Test]
    public async Task REQ210_SubmitGuess_AfterAttemptsExhausted_RejectedWithoutEverCallingGameModuleAgain()
    {
        var round = await SeedActiveRoundAsync(allowGuessChange: true);
        var userId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        SetNextResult(_gameModule, isCorrect: false);
        var service = BuildService();
        await service.SubmitGuessAsync(round.Id, userId, cellId, "Wrong Guess 1");
        await service.SubmitGuessAsync(round.Id, userId, cellId, "Wrong Guess 2");
        Assert.That(_gameModule.ScoreSubmissionAsyncCallCount, Is.EqualTo(2));

        var result = await service.SubmitGuessAsync(round.Id, userId, cellId, "Third Guess");

        Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.NoAttemptsRemaining));
        Assert.That(_gameModule.ScoreSubmissionAsyncCallCount, Is.EqualTo(2),
            "a rejected-by-REQ-210 submission must never reach the game module a third time");
    }
}

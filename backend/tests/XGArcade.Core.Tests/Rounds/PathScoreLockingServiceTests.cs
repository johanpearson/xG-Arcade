using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Core.Rounds;
using XGArcade.Core.Scoring;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Tests.Rounds;

// REQ-1206/S-083/ADR-0040 (docs/requirements-document.md, docs/backlog.md
// S-083): end-to-end round-close coverage for xG Path's clue-efficiency
// scoring, the sibling of RoundCloseServiceScoringTests (xG Grid's
// uniqueness formula). Wires BOTH UniquenessScoringStrategy and
// ClueEfficiencyScoringStrategy into one ScoringStrategyResolver so every
// test here also proves the resolver picks the "xg-path"-keyed strategy,
// not the "xg-grid" one, for an xg-path round — never just "the only
// strategy registered comes back" (see also
// ScoringStrategyResolverTests.REQ1206_Resolve_ReturnsTheRegisteredClueEfficiencyScoringStrategy_ForXgPath
// for the narrower resolver-only version of the same proof).
public class PathScoreLockingServiceTests
{
    private const string GameKey = "xg-path";

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
        // GetCellIdsResult explicitly, same as RoundCloseServiceScoringTests.
        _fakeGameModule = new FakeGameModule(GameKey);
        var gameModuleResolver = new GameModuleResolver([_fakeGameModule]);
        // Both strategies registered together (not just ClueEfficiencyScoringStrategy
        // alone) so a wrong-strategy resolution for "xg-path" would be
        // caught here, not masked by there being nothing else to pick.
        var scoringStrategyResolver = new ScoringStrategyResolver(
        [
            new UniquenessScoringStrategy { GameKey = "xg-grid" },
            new ClueEfficiencyScoringStrategy { GameKey = GameKey },
        ]);
        _service = new RoundCloseService(
            _roundRepository,
            new ScoreLockingService(_guessRepository, _roundRepository, gameModuleResolver, scoringStrategyResolver));
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

    // Same shape as RoundCloseServiceScoringTests.SeedGuessAsync, but takes
    // an explicit attemptCount — REQ-1206's cluesUsed is
    // Guess.AttemptCount, and the existing helper hardcodes AttemptCount = 1,
    // which can't exercise a range of cluesUsed values.
    private async Task<Guess> SeedGuessAsync(
        Guid roundId, Guid cellId, bool isCorrect, int attemptCount, Guid? playerAnswerId = null, Guid? userId = null)
    {
        var guess = new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = roundId,
            UserId = userId ?? Guid.NewGuid(),
            CellId = cellId,
            SubmittedName = "Someone",
            PlayerAnswerId = playerAnswerId,
            IsCorrect = isCorrect,
            AttemptCount = attemptCount,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Guesses.Add(guess);
        await _dbContext.SaveChangesAsync();
        return guess;
    }

    [Test]
    public async Task REQ1206_CloseRoundAsync_CorrectGuess_LocksPointsFromClueEfficiencyFormulaAndNullUniquenessScore()
    {
        // maxAttemptsForCell = 5 here (not XGPathGameModule's real fixed 7)
        // to prove ScoreLockingService/ClueEfficiencyScoringStrategy read
        // it generically through IGameModule.GetMaxAttemptsForCellAsync,
        // never a hardcoded 7.
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var cellId = Guid.NewGuid();
        _fakeGameModule.MaxAttemptsForCellResult = (_, _) => 5;
        var guess = await SeedGuessAsync(round.Id, cellId, isCorrect: true, attemptCount: 2, playerAnswerId: Guid.NewGuid());

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var persisted = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == guess.Id);
        // round(2/5*100) = 40. If this had wrongly resolved
        // UniquenessScoringStrategy instead, a lone correct guesser would
        // score 0 (100% unique, ADR-0020), not 40 — so this value alone
        // also pins down "the right strategy was actually invoked".
        Assert.That(persisted.FinalPoints, Is.EqualTo(40));
        Assert.That(persisted.FinalUniquenessScore, Is.Null,
            "xG Path has no uniqueness concept — must never be populated by ScoreLockingService for an xg-path round");
    }

    [Test]
    public async Task REQ1206_CloseRoundAsync_TwoCorrectGuessesWithDifferentClueCounts_EachScoresIndependentlyOfTheOther()
    {
        // Unlike xG Grid, two correct guessers on the same xG Path puzzle
        // (by construction, they always name the same target player) must
        // never affect each other's score — each is scored purely off its
        // own AttemptCount/maxAttemptsForCell.
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var cellId = Guid.NewGuid();
        var targetPlayerAnswerId = Guid.NewGuid();
        _fakeGameModule.MaxAttemptsForCellResult = (_, _) => 7;
        var fastGuess = await SeedGuessAsync(round.Id, cellId, isCorrect: true, attemptCount: 1, playerAnswerId: targetPlayerAnswerId);
        var slowGuess = await SeedGuessAsync(round.Id, cellId, isCorrect: true, attemptCount: 7, playerAnswerId: targetPlayerAnswerId);

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var persistedFast = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == fastGuess.Id);
        var persistedSlow = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == slowGuess.Id);
        Assert.That(persistedFast.FinalPoints, Is.EqualTo(14), "round(1/7*100) = 14");
        Assert.That(persistedSlow.FinalPoints, Is.EqualTo(100), "round(7/7*100) = 100 — solved on the very last clue");
        Assert.That(persistedFast.FinalUniquenessScore, Is.Null);
        Assert.That(persistedSlow.FinalUniquenessScore, Is.Null);
    }

    [Test]
    public async Task REQ1206_CloseRoundAsync_IncorrectGuess_LocksNullUniquenessScoreAndMaxPoints()
    {
        // REQ-1206's "never solved before the attempt cap is exhausted
        // scores the worst case, MaxPointsPerCell" criterion — the same
        // ADR-0021 branch RoundCloseServiceScoringTests.REQ205_..._IncorrectGuess_...
        // already covers for xg-grid, exercised here explicitly for an
        // xg-path round: this branch never calls IScoringStrategy at all
        // (ScoreLockingService only invokes it for guess.IsCorrect == true),
        // so nothing about xG Path's own formula could otherwise leak in.
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var cellId = Guid.NewGuid();
        _fakeGameModule.MaxAttemptsForCellResult = (_, _) => 7;
        var incorrectGuess = await SeedGuessAsync(round.Id, cellId, isCorrect: false, attemptCount: 7);
        // A correct guesser on the same cell must have zero bearing on the
        // incorrect guess's locked values (mirrors REQ205's own regression
        // case above, but confirms it also holds for xg-path).
        await SeedGuessAsync(round.Id, cellId, isCorrect: true, attemptCount: 3, playerAnswerId: Guid.NewGuid());

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var persisted = await _dbContext.Guesses.AsNoTracking().SingleAsync(g => g.Id == incorrectGuess.Id);
        Assert.That(persisted.FinalUniquenessScore, Is.Null);
        Assert.That(persisted.FinalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell));
    }

    [Test]
    public async Task REQ1206_CloseRoundAsync_ParticipantNeverSolvedAPuzzleBeforeAttemptCapExhausted_MaterializesItAsMaxPoints()
    {
        // ADR-0021's unanswered-cell materialization (MaterializeUnansweredCellsAsync)
        // applies identically to xg-path rounds — a participant who opened
        // this round but never solved this specific puzzle before its
        // attempt cap ran out scores the same worst case as an explicit
        // incorrect guess, not 0.
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var solvedCellId = Guid.NewGuid();
        var neverSolvedCellId = Guid.NewGuid();
        _fakeGameModule.GetCellIdsResult = _ => [solvedCellId, neverSolvedCellId];
        _fakeGameModule.MaxAttemptsForCellResult = (_, _) => 7;
        var participantId = Guid.NewGuid();
        await SeedGuessAsync(round.Id, solvedCellId, isCorrect: true, attemptCount: 4, playerAnswerId: Guid.NewGuid(), userId: participantId);

        await _service.CloseRoundAsync(round.Id, DateTime.UtcNow);

        var materialized = await _dbContext.Guesses.AsNoTracking()
            .SingleAsync(g => g.RoundId == round.Id && g.UserId == participantId && g.CellId == neverSolvedCellId);
        Assert.That(materialized.IsCorrect, Is.False);
        Assert.That(materialized.PlayerAnswerId, Is.Null);
        Assert.That(materialized.FinalUniquenessScore, Is.Null);
        Assert.That(materialized.FinalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell));
    }
}

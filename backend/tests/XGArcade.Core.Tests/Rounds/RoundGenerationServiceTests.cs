using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Core.Rounds;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Tests.Rounds;

// REQ-301 (docs/requirements-document.md §4.3): generation always runs one
// round ahead. Follows this repo's no-mocking-framework pattern
// (docs/coding-guidelines.md "don't over-mock"): a real, InMemory-backed
// IRoundRepository plus a hand-rolled FakeGameModule, same setup
// XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs uses for its own
// dependencies.
public class RoundGenerationServiceTests
{
    private const string GameKey = "xg-grid";

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IRoundRepository _roundRepository = null!;
    private FakeGameModule _gameModule = null!;
    private FakeRoundCloseService _roundCloseService = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _roundRepository = new RoundRepository(_dbContext);
        _gameModule = new FakeGameModule(GameKey);
        _roundCloseService = new FakeRoundCloseService();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private RoundGenerationService BuildService(DateTimeOffset now, TimeSpan roundDuration, bool allowGuessChange = true) =>
        new(_roundRepository,
            new GameModuleResolver([_gameModule]),
            _roundCloseService,
            new RoundSchedulingOptions { GameKey = GameKey, RoundDuration = roundDuration, AllowGuessChange = allowGuessChange },
            new FixedTimeProvider(now));

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

    [Test]
    public async Task REQ301_GenerateNextRoundIfNeeded_NoExistingRound_CreatesFirstRoundStartingNow()
    {
        var now = new DateTimeOffset(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
        var service = BuildService(now, TimeSpan.FromDays(3));

        var round = await service.GenerateNextRoundIfNeededAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(round.StartTime, Is.EqualTo(now.UtcDateTime));
        Assert.That(round.EndTime, Is.EqualTo(now.UtcDateTime + TimeSpan.FromDays(3)));
        Assert.That(round.GameKey, Is.EqualTo(GameKey));
        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ301_GenerateNextRoundIfNeeded_PersistsGameInstanceIdReturnedByGameModule()
    {
        var instanceId = Guid.NewGuid();
        _gameModule.GenerateInstanceResult = _ => new GameInstance { Id = instanceId };
        var service = BuildService(DateTimeOffset.UtcNow, TimeSpan.FromDays(3));

        var round = await service.GenerateNextRoundIfNeededAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(round.GameInstanceId, Is.EqualTo(instanceId));
    }

    [Test]
    public async Task REQ301_GenerateNextRoundIfNeeded_ActiveRoundExists_CreatesNextRoundStartingAtItsEndTime()
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var activeRound = await SeedRoundAsync(
            startTime: now.UtcDateTime.AddDays(-1),
            endTime: now.UtcDateTime.AddDays(2));
        var service = BuildService(now, TimeSpan.FromDays(3));

        var nextRound = await service.GenerateNextRoundIfNeededAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(nextRound.Id, Is.Not.EqualTo(activeRound.Id));
        Assert.That(nextRound.StartTime, Is.EqualTo(activeRound.EndTime));
        Assert.That(nextRound.EndTime, Is.EqualTo(activeRound.EndTime + TimeSpan.FromDays(3)));
        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ301_GenerateNextRoundIfNeeded_UpcomingRoundAlreadyExists_ReturnsItWithoutGeneratingAgain()
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        // Round N (active) plus Round N+1 (upcoming, starts in the future) —
        // generation is already one round ahead, so a third round must not
        // be created no matter how many times the scheduler job fires.
        await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-2), endTime: now.UtcDateTime.AddDays(1));
        var upcomingRound = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(1), endTime: now.UtcDateTime.AddDays(4));
        var service = BuildService(now, TimeSpan.FromDays(3));

        var result = await service.GenerateNextRoundIfNeededAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(result.Id, Is.EqualTo(upcomingRound.Id));
        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.Zero, "already one round ahead — generation must not run again");
        Assert.That(await _dbContext.Rounds.CountAsync(), Is.EqualTo(2), "no extra round should have been persisted");
    }

    [Test]
    public async Task REQ301_GenerateNextRoundIfNeeded_RoundBecomesActiveExactlyAtItsStartTime()
    {
        // Boundary: "now == StartTime" must count as already-active (not
        // still-upcoming), so the very next scheduled invocation generates
        // round N+2 rather than treating N+1 as still one-round-ahead forever.
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        await SeedRoundAsync(startTime: now.UtcDateTime, endTime: now.UtcDateTime.AddDays(3));
        var service = BuildService(now, TimeSpan.FromDays(3));

        await service.GenerateNextRoundIfNeededAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.EqualTo(1));
    }

    [Test]
    public void GenerateNextRoundIfNeeded_UnknownGameKey_ThrowsInvalidOperationException()
    {
        var service = new RoundGenerationService(
            _roundRepository,
            new GameModuleResolver([_gameModule]),
            _roundCloseService,
            new RoundSchedulingOptions { GameKey = "some-other-game", RoundDuration = TimeSpan.FromDays(3) },
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.GenerateNextRoundIfNeededAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));
    }

    // ---- ADR-0022: round closing runs inside this job ----------------------

    [Test]
    public async Task REQ205_GenerateNextRoundIfNeeded_PredecessorOfLatestAlreadyEnded_ClosesItBeforeGeneratingSuccessor()
    {
        // Steady-state shape: round A ended exactly when round B (latest)
        // started; B has itself now started, so B's successor is about to be
        // generated — A is the round this job has never had a chance to
        // close until now.
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var roundA = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-8), endTime: now.UtcDateTime.AddDays(-4));
        var roundB = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-4), endTime: now.UtcDateTime.AddHours(-1));
        var service = BuildService(now, TimeSpan.FromDays(4));

        await service.GenerateNextRoundIfNeededAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(_roundCloseService.Calls, Has.Count.EqualTo(1));
        Assert.That(_roundCloseService.Calls[0].RoundId, Is.EqualTo(roundA.Id));
        Assert.That(roundB.Id, Is.Not.EqualTo(_roundCloseService.Calls[0].RoundId), "the predecessor is closed, never 'latest' itself");
    }

    [Test]
    public async Task REQ205_GenerateNextRoundIfNeeded_LatestHasNotStartedYet_NeverAttemptsToCloseAnything()
    {
        // "One round ahead" early-return path: latest hasn't started, so
        // nothing has been superseded yet either.
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-4), endTime: now.UtcDateTime.AddDays(1));
        await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(1), endTime: now.UtcDateTime.AddDays(5));
        var service = BuildService(now, TimeSpan.FromDays(4));

        await service.GenerateNextRoundIfNeededAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(_roundCloseService.Calls, Is.Empty);
    }

    [Test]
    public async Task REQ205_GenerateNextRoundIfNeeded_NoPredecessorExists_GeneratesFirstSuccessorWithoutAttemptingToClose()
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-1), endTime: now.UtcDateTime.AddHours(-1));
        var service = BuildService(now, TimeSpan.FromDays(3));

        await service.GenerateNextRoundIfNeededAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(_roundCloseService.Calls, Is.Empty, "the very first round ever generated has no predecessor to close");
    }
}

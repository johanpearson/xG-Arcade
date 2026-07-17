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
    public async Task REQ301_GenerateNextRoundIfNeeded_RoundDurationOverrideSupplied_UsesOverrideInsteadOfOptionsRoundDuration()
    {
        var now = new DateTimeOffset(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
        var service = BuildService(now, TimeSpan.FromDays(3));

        var round = await service.GenerateNextRoundIfNeededAsync(
            new RoundConfig { TemplateId = Guid.NewGuid() },
            roundDurationOverride: TimeSpan.FromHours(12));

        Assert.That(round.EndTime, Is.EqualTo(now.UtcDateTime + TimeSpan.FromHours(12)));
    }

    [Test]
    public async Task REQ301_GenerateNextRoundIfNeeded_RoundDurationOverrideSupplied_DoesNotMutateSharedOptionsForSubsequentCall()
    {
        // Round A (overridden to 12h) is seeded as the only existing round;
        // calling again without an override must chain Round B off Round A's
        // EndTime using the *configured* 3-day duration, not the 12h override
        // from the first call — proving RoundSchedulingOptions itself was
        // never mutated.
        //
        // Both calls run against the SAME RoundGenerationService instance
        // (and therefore the SAME RoundSchedulingOptions instance passed into
        // its constructor) — this is deliberate, not incidental: BuildService
        // constructs a *new* RoundSchedulingOptions on every call, which
        // would make this test unable to detect a real mutation bug (each
        // service would just have its own, never-shared, options object).
        // Production shares exactly one RoundSchedulingOptions instance
        // across every request via Program.cs's `AddSingleton` registration,
        // so this test must reproduce that sharing to be meaningful — a
        // future `options.RoundDuration = roundDurationOverride ??
        // options.RoundDuration;` bug inside GenerateNextRoundIfNeededAsync
        // must fail this test.
        var options = new RoundSchedulingOptions { GameKey = GameKey, RoundDuration = TimeSpan.FromDays(3) };
        var now = new DateTimeOffset(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
        var service = new RoundGenerationService(
            _roundRepository,
            new GameModuleResolver([_gameModule]),
            _roundCloseService,
            options,
            new FixedTimeProvider(now));

        var roundA = await service.GenerateNextRoundIfNeededAsync(
            new RoundConfig { TemplateId = Guid.NewGuid() },
            roundDurationOverride: TimeSpan.FromHours(12));
        Assert.That(roundA.EndTime, Is.EqualTo(now.UtcDateTime + TimeSpan.FromHours(12)));

        // Advance the clock so Round A now reads as active, and Round B (no
        // upcoming round exists yet) is genuinely generated rather than the
        // "already one round ahead" no-op path. FixedTimeProvider itself is
        // immutable (it always returns the value fixed at construction), so
        // advancing "now" for the second call means constructing a second
        // RoundGenerationService with a later FixedTimeProvider — but reusing
        // the exact same `options` instance from above, which is the part
        // that actually matters for this test.
        var later = now.AddHours(1);
        var serviceAtLaterTime = new RoundGenerationService(
            _roundRepository,
            new GameModuleResolver([_gameModule]),
            _roundCloseService,
            options,
            new FixedTimeProvider(later));

        var roundB = await serviceAtLaterTime.GenerateNextRoundIfNeededAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(roundB.StartTime, Is.EqualTo(roundA.EndTime));
        Assert.That(roundB.EndTime, Is.EqualTo(roundA.EndTime + TimeSpan.FromDays(3)),
            "the second call's round must use the originally configured RoundDuration, not the first call's override");
        Assert.That(options.RoundDuration, Is.EqualTo(TimeSpan.FromDays(3)),
            "the shared RoundSchedulingOptions instance itself must never be mutated by an override");
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

    [Test]
    public async Task REQ205_GenerateNextRoundIfNeeded_CalledAgainAfterSuccessorAlreadyGenerated_DoesNotCloseOrGenerateAgain()
    {
        // Idempotency at *this* layer (not ScoreLockingService/RoundCloseService's
        // own, already covered by RoundCloseServiceTests): once one job run has
        // both closed a predecessor and generated its successor, a second run
        // against the exact same clock (e.g. a retried cron invocation) must be a
        // total no-op — no duplicate close call, no duplicate round.
        //
        // Round A ended; Round B (latest) started but hasn't ended yet, no
        // upcoming round exists yet.
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var roundA = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-8), endTime: now.UtcDateTime.AddDays(-4));
        await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-4), endTime: now.UtcDateTime.AddDays(1));
        var service = BuildService(now, TimeSpan.FromDays(4));

        // First run: closes A (B's predecessor) and generates C, B's successor,
        // starting at B's future EndTime — so C is itself still upcoming.
        await service.GenerateNextRoundIfNeededAsync(new RoundConfig { TemplateId = Guid.NewGuid() });
        Assert.That(_roundCloseService.Calls, Has.Count.EqualTo(1));
        Assert.That(_roundCloseService.Calls[0].RoundId, Is.EqualTo(roundA.Id));
        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.EqualTo(1));
        Assert.That(await _dbContext.Rounds.CountAsync(), Is.EqualTo(3));

        // Second run, same clock, same repository state: "latest" is now C,
        // which hasn't started yet — the one-round-ahead early return applies,
        // so nothing further should be closed or generated.
        await service.GenerateNextRoundIfNeededAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(_roundCloseService.Calls, Has.Count.EqualTo(1), "a repeated call must not close anything a second time");
        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.EqualTo(1), "a repeated call must not generate a second successor");
        Assert.That(await _dbContext.Rounds.CountAsync(), Is.EqualTo(3));
    }
}

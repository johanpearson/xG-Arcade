using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Core.Rounds;
using XGArcade.Core.Scoring;
using XGArcade.Core.Tests.Scoring;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.TestSupport;

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
    private IGuessRepository _guessRepository = null!;
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
        // Real GuessRepository (InMemory-backed), not a fake — REQ-305's own
        // participation check (routed through GuessRoundScoreSource, ADR-0100)
        // needs genuine persistence/counting behavior, same "real repository
        // over the InMemory provider" precedent _roundRepository above
        // already follows.
        _guessRepository = new GuessRepository(_dbContext);
        _gameModule = new FakeGameModule(GameKey);
        _roundCloseService = new FakeRoundCloseService();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    // ADR-0100: the real, zero-behavior-change GuessRoundScoreSource for a
    // GameKey backed by Guess rows — mirrors LeaderboardServiceTests' own
    // SetUp construction of the same type, rather than inventing a second
    // pattern for the same abstraction.
    private IRoundScoreSource BuildGuessRoundScoreSource(string gameKey) =>
        new GuessRoundScoreSource(
            _guessRepository, new LiveRoundContributionService(_guessRepository, new GameModuleResolver([_gameModule])))
        {
            GameKey = gameKey,
        };

    // Real RoundSchedulingOptionsResolver, not a fake — it's a trivial
    // find-by-GameKey lookup (same reasoning ScoringStrategyResolverTests
    // uses the real ScoringStrategyResolver rather than a hand-rolled fake).
    private RoundGenerationService BuildService(
        DateTimeOffset now, TimeSpan roundDuration, bool allowGuessChange = true, bool usesModuleSuggestedTiming = false) =>
        new(_roundRepository,
            new RoundScoreSourceResolver(new Dictionary<string, IRoundScoreSource> { [GameKey] = BuildGuessRoundScoreSource(GameKey) }),
            new GameModuleResolver([_gameModule]),
            _roundCloseService,
            new RoundSchedulingOptionsResolver(
                [new RoundSchedulingOptions
                {
                    GameKey = GameKey,
                    RoundDuration = roundDuration,
                    AllowGuessChange = allowGuessChange,
                    UsesModuleSuggestedTiming = usesModuleSuggestedTiming,
                }]),
            new FixedTimeProvider(now));

    private async Task<Round> SeedRoundAsync(DateTime startTime, DateTime endTime, string? gameKey = null)
    {
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = gameKey ?? GameKey,
            GameInstanceId = Guid.NewGuid(),
            SequenceNumber = 1,
            StartTime = startTime,
            EndTime = endTime,
            AllowGuessChange = true,
        };
        _dbContext.Rounds.Add(round);
        await _dbContext.SaveChangesAsync();
        return round;
    }

    // REQ-305: seeds a single Guess row against a round, so that round no
    // longer reads as "zero Guess rows" for the renewal check.
    private async Task SeedGuessAsync(Guid roundId)
    {
        _dbContext.Guesses.Add(new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = roundId,
            UserId = Guid.NewGuid(),
            CellId = Guid.NewGuid(),
            SubmittedName = "Someone",
            IsCorrect = true,
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task REQ301_GenerateNextRoundIfNeeded_NoExistingRound_CreatesFirstRoundStartingNow()
    {
        var now = new DateTimeOffset(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
        var service = BuildService(now, TimeSpan.FromDays(3));

        var round = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

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

        var round = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(round.GameInstanceId, Is.EqualTo(instanceId));
    }

    // ADR-0102: a module-supplied SuggestedStartTime/SuggestedEndTime wins
    // over chain-math entirely — proven generically here (FakeGameModule),
    // not just for xg-predict, since the override logic itself lives in
    // RoundGenerationService.
    [Test]
    public async Task ADR0102_GenerateNextRoundIfNeeded_GameModuleSuppliesSuggestedTimes_UsesThemInsteadOfChainMath()
    {
        var suggestedStart = new DateTime(2026, 9, 12, 15, 0, 0, DateTimeKind.Utc);
        var suggestedEnd = new DateTime(2026, 9, 14, 17, 15, 0, DateTimeKind.Utc);
        _gameModule.GenerateInstanceResult = _ => new GameInstance
        {
            Id = Guid.NewGuid(),
            SuggestedStartTime = suggestedStart,
            SuggestedEndTime = suggestedEnd,
        };
        // now/RoundDuration are deliberately far from suggestedStart/End —
        // if chain-math formulas were used instead, this assertion would
        // fail loudly rather than pass by coincidence.
        var now = new DateTimeOffset(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
        var service = BuildService(now, TimeSpan.FromDays(3));

        var round = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(round.StartTime, Is.EqualTo(suggestedStart));
        Assert.That(round.EndTime, Is.EqualTo(suggestedEnd));
    }

    [Test]
    public async Task REQ301_GenerateNextRoundIfNeeded_ActiveRoundExists_CreatesNextRoundStartingAtItsEndTime()
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var activeRound = await SeedRoundAsync(
            startTime: now.UtcDateTime.AddDays(-1),
            endTime: now.UtcDateTime.AddDays(2));
        var service = BuildService(now, TimeSpan.FromDays(3));

        var nextRound = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

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

        var result = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

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

        await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ301_GenerateNextRoundIfNeeded_RoundDurationOverrideSupplied_UsesOverrideInsteadOfOptionsRoundDuration()
    {
        var now = new DateTimeOffset(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
        var service = BuildService(now, TimeSpan.FromDays(3));

        var round = await service.GenerateNextRoundIfNeededAsync(
            GameKey,
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
        var roundScoreSourceResolver = new RoundScoreSourceResolver(new Dictionary<string, IRoundScoreSource> { [GameKey] = BuildGuessRoundScoreSource(GameKey) });
        var service = new RoundGenerationService(
            _roundRepository,
            roundScoreSourceResolver,
            new GameModuleResolver([_gameModule]),
            _roundCloseService,
            new RoundSchedulingOptionsResolver([options]),
            new FixedTimeProvider(now));

        var roundA = await service.GenerateNextRoundIfNeededAsync(
            GameKey,
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
            roundScoreSourceResolver,
            new GameModuleResolver([_gameModule]),
            _roundCloseService,
            new RoundSchedulingOptionsResolver([options]),
            new FixedTimeProvider(later));

        var roundB = await serviceAtLaterTime.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(roundB.StartTime, Is.EqualTo(roundA.EndTime));
        Assert.That(roundB.EndTime, Is.EqualTo(roundA.EndTime + TimeSpan.FromDays(3)),
            "the second call's round must use the originally configured RoundDuration, not the first call's override");
        Assert.That(options.RoundDuration, Is.EqualTo(TimeSpan.FromDays(3)),
            "the shared RoundSchedulingOptions instance itself must never be mutated by an override");
    }

    // ---- S-084/REQ-1202: two GameKeys, each with a genuinely distinct -------
    // configured RoundDuration, resolved through the SAME
    // RoundSchedulingOptionsResolver/RoundGenerationService instance — this is
    // the test that actually proves "independent of xG Grid's own round
    // timing/duration" end-to-end at this layer, not just an assumption from
    // the code shape. Uses a second GameKey ("xg-path" — the real second
    // GameKey this story wires up) rather than an arbitrary placeholder, so a
    // reader doesn't have to squint to see this is REQ-1202's own scenario.

    private const string OtherGameKey = "xg-path";

    private (RoundGenerationService Service, FakeGameModule OtherGameModule) BuildServiceWithTwoGameKeys(
        DateTimeOffset now, TimeSpan gameKeyRoundDuration, TimeSpan otherGameKeyRoundDuration)
    {
        var otherGameModule = new FakeGameModule(OtherGameKey);
        var gameModuleResolver = new GameModuleResolver([_gameModule, otherGameModule]);
        // ADR-0100: GuessRoundScoreSource registered twice, once per
        // GameKey it serves — mirrors ServiceRegistration.cs's own
        // composition-root registration (both "xg-grid" and "xg-path" share
        // this one Guess-backed implementation).
        var roundScoreSourceResolver = new RoundScoreSourceResolver(new Dictionary<string, IRoundScoreSource>
        {
            [GameKey] = new GuessRoundScoreSource(_guessRepository, new LiveRoundContributionService(_guessRepository, gameModuleResolver)) { GameKey = GameKey },
            [OtherGameKey] = new GuessRoundScoreSource(_guessRepository, new LiveRoundContributionService(_guessRepository, gameModuleResolver)) { GameKey = OtherGameKey },
        });
        var service = new RoundGenerationService(
            _roundRepository,
            roundScoreSourceResolver,
            gameModuleResolver,
            _roundCloseService,
            new RoundSchedulingOptionsResolver(
            [
                new RoundSchedulingOptions { GameKey = GameKey, RoundDuration = gameKeyRoundDuration },
                new RoundSchedulingOptions { GameKey = OtherGameKey, RoundDuration = otherGameKeyRoundDuration },
            ]),
            new FixedTimeProvider(now));
        return (service, otherGameModule);
    }

    [Test]
    public async Task REQ1202_GenerateNextRoundIfNeeded_TwoGameKeysRegistered_EachGeneratedRoundUsesItsOwnConfiguredRoundDuration()
    {
        var now = new DateTimeOffset(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
        var (service, _) = BuildServiceWithTwoGameKeys(
            now, gameKeyRoundDuration: TimeSpan.FromDays(3), otherGameKeyRoundDuration: TimeSpan.FromHours(30));

        var gridRound = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });
        var pathRound = await service.GenerateNextRoundIfNeededAsync(OtherGameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(gridRound.EndTime - gridRound.StartTime, Is.EqualTo(TimeSpan.FromDays(3)),
            "xg-grid's own configured RoundDuration must land on its generated Round, never xg-path's");
        Assert.That(pathRound.EndTime - pathRound.StartTime, Is.EqualTo(TimeSpan.FromHours(30)),
            "xg-path's own configured RoundDuration must land on its generated Round, never xg-grid's");
    }

    [Test]
    public async Task REQ1202_GenerateNextRoundIfNeeded_TwoGameKeysRegistered_GeneratingForOneGameKeyNeverTouchesTheOthersRounds()
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        // xg-grid already has an active round with nothing scheduled after it
        // — if GameKey scoping were broken, generating xg-path's round could
        // wrongly chain off this, close it, or otherwise disturb it.
        var gridActive = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-1), endTime: now.UtcDateTime.AddDays(2));
        var (service, otherGameModule) = BuildServiceWithTwoGameKeys(
            now, gameKeyRoundDuration: TimeSpan.FromDays(3), otherGameKeyRoundDuration: TimeSpan.FromHours(30));

        var pathRound = await service.GenerateNextRoundIfNeededAsync(OtherGameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(pathRound.GameKey, Is.EqualTo(OtherGameKey));
        Assert.That(pathRound.StartTime, Is.EqualTo(now.UtcDateTime),
            "xg-path has no round of its own yet — it must start now, never chained off xg-grid's EndTime");
        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.Zero,
            "generating xg-path's round must never invoke xg-grid's own game module");
        Assert.That(otherGameModule.GenerateInstanceAsyncCallCount, Is.EqualTo(1));
        Assert.That(_roundCloseService.Calls, Is.Empty,
            "xg-grid's active round has no predecessor to close and must not be touched by an xg-path generation call");

        var gridRoundsAfter = await _dbContext.Rounds.Where(r => r.GameKey == GameKey).ToListAsync();
        Assert.That(gridRoundsAfter, Has.Count.EqualTo(1), "xg-grid's own round set must be unaffected by generating xg-path's round");
        Assert.That(gridRoundsAfter[0].Id, Is.EqualTo(gridActive.Id));
        Assert.That(gridRoundsAfter[0].EndTime, Is.EqualTo(gridActive.EndTime), "xg-grid's round must not be closed/modified by generating xg-path's round");
    }

    [Test]
    public void GenerateNextRoundIfNeeded_UnknownGameKey_ThrowsInvalidOperationException()
    {
        // "some-other-game" resolves fine against IRoundSchedulingOptionsResolver
        // (registered below) but has no matching IGameModule (only _gameModule,
        // keyed "xg-grid", is registered) — this proves
        // IGameModuleResolver.Resolve's own not-found failure still surfaces
        // through RoundGenerationService, distinct from
        // RoundSchedulingOptionsResolverTests' own coverage of the
        // "no RoundSchedulingOptions registered for this GameKey" failure mode.
        var service = new RoundGenerationService(
            _roundRepository,
            // Empty — never reached: no round exists yet for "some-other-game",
            // so the participation-check branch (which resolves an
            // IRoundScoreSource) is unreachable before IGameModuleResolver's
            // own not-found failure fires.
            new RoundScoreSourceResolver(new Dictionary<string, IRoundScoreSource>()),
            new GameModuleResolver([_gameModule]),
            _roundCloseService,
            new RoundSchedulingOptionsResolver(
                [new RoundSchedulingOptions { GameKey = "some-other-game", RoundDuration = TimeSpan.FromDays(3) }]),
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.GenerateNextRoundIfNeededAsync("some-other-game", new RoundConfig { TemplateId = Guid.NewGuid() }));
    }

    // ---- ADR-0102: IGameModule.GenerateInstanceAsync returning null -------
    // (S-204) proves this generic contract at the Core layer, not just
    // xg-predict-specific — see XGPredictGameModuleTests for the real
    // fixture-set-dedup behavior that actually returns null in production.

    [Test]
    public async Task ADR0102_GenerateNextRoundIfNeeded_GameModuleReturnsNull_ReturnsLatestRoundUnchanged_NoNewRoundPersisted()
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        // "latest" is already active with nothing scheduled after it, so
        // generation would normally attempt a real successor here.
        var activeRound = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-1), endTime: now.UtcDateTime.AddDays(2));
        _gameModule.GenerateInstanceResult = config =>
        {
            Assert.That(config.LatestGameInstanceId, Is.EqualTo(activeRound.GameInstanceId),
                "RoundGenerationService must populate LatestGameInstanceId from the existing latest Round before calling the module");
            return null;
        };
        var service = BuildService(now, TimeSpan.FromDays(3));

        var result = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(result.Id, Is.EqualTo(activeRound.Id), "null means 'no new round due' — the existing latest Round must be returned unchanged");
        Assert.That(await _dbContext.Rounds.CountAsync(), Is.EqualTo(1), "no new Round may be persisted when the module returns null");
    }

    [Test]
    public void ADR0102_GenerateNextRoundIfNeeded_GameModuleReturnsNullWithNoExistingRound_ThrowsInvalidOperationException()
    {
        // A module returning null for a GameKey's first-ever round (no
        // `latest` to fall back to) violates its own contract (ADR-0102) —
        // this must fail loudly rather than silently produce a
        // NullReferenceException or a confusing missing round.
        _gameModule.GenerateInstanceResult = _ => null;
        var service = BuildService(DateTimeOffset.UtcNow, TimeSpan.FromDays(3));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() }));
        Assert.That(ex!.Message, Does.Contain(GameKey));
    }

    // ---- REQ-304: per-GameKey SequenceNumber assignment ---------------------
    //
    // The (GameKey, SequenceNumber) unique index added alongside this
    // requirement is an EF Core relational-provider feature — the InMemory
    // provider used throughout this file does not enforce unique indexes at
    // all (same documented limitation UserRepositoryTests.cs relies on for
    // IX_Users_NormalizedDisplayName), so these tests deliberately target
    // only RoundGenerationService's own MAX+1-per-GameKey assignment logic,
    // not database-level constraint enforcement, which is trusted to be
    // provider-enforced in production per that index/migration.

    [Test]
    public async Task REQ304_GenerateNextRoundIfNeeded_FirstRoundForGameKey_AssignsSequenceNumberOne()
    {
        var now = new DateTimeOffset(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
        var service = BuildService(now, TimeSpan.FromDays(3));

        var round = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(round.SequenceNumber, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ304_GenerateNextRoundIfNeeded_SecondRoundForSameGameKey_AssignsNextSequenceNumberWithoutCollision()
    {
        // Round A (SequenceNumber 1) is active with nothing scheduled after
        // it, so generating again chains Round B off Round A's EndTime and
        // must compute MAX(SequenceNumber)+1 = 2 for this GameKey.
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var service = BuildService(now, TimeSpan.FromDays(3));
        var roundA = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });
        Assert.That(roundA.SequenceNumber, Is.EqualTo(1));

        // Advance the clock so Round A now reads as active (its EndTime has
        // passed StartTime relative to "now"), so generating again produces
        // a genuine second round rather than the "already one round ahead"
        // no-op path — same technique REQ301's override-mutation test above
        // uses to force a second real generation.
        var later = now.AddDays(3).AddHours(1);
        var serviceAtLaterTime = BuildService(later, TimeSpan.FromDays(3));

        var roundB = await serviceAtLaterTime.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(roundB.SequenceNumber, Is.EqualTo(2));
        Assert.That(roundB.SequenceNumber, Is.Not.EqualTo(roundA.SequenceNumber),
            "two rounds for the same GameKey must never collide on SequenceNumber");
    }

    [Test]
    public async Task REQ304_GenerateNextRoundIfNeeded_TwoDifferentGameKeys_EachIndependentlyAssignsSequenceNumberOne()
    {
        // Independence per GameKey, matching IRoundSchedulingOptionsResolver's
        // existing per-GameKey independence (REQ-301/REQ-1202): neither
        // GameKey has any existing rounds yet, so both must start their own
        // counter at 1 rather than sharing a single global counter.
        var now = new DateTimeOffset(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
        var (service, _) = BuildServiceWithTwoGameKeys(
            now, gameKeyRoundDuration: TimeSpan.FromDays(3), otherGameKeyRoundDuration: TimeSpan.FromHours(30));

        var gridRound = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });
        var pathRound = await service.GenerateNextRoundIfNeededAsync(OtherGameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(gridRound.SequenceNumber, Is.EqualTo(1));
        Assert.That(pathRound.SequenceNumber, Is.EqualTo(1),
            "SequenceNumber is an independent counter per GameKey — a second GameKey's first round may share the same value as another GameKey's first round");
    }

    [Test]
    public async Task REQ304_GenerateNextRoundIfNeeded_TwoDifferentGameKeysWithExistingRounds_SequenceNumbersDoNotCrossContaminate()
    {
        // xg-grid already has two rounds (SequenceNumber 1 and 2 via
        // SeedRoundAsync's default) seeded directly; generating xg-path's
        // very first round must compute MAX+1 scoped to xg-path alone (i.e.
        // 1, since xg-path has no rows yet) rather than picking up xg-grid's
        // higher counter.
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var gridRoundOne = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-6), endTime: now.UtcDateTime.AddDays(-3));
        gridRoundOne.SequenceNumber = 1;
        var gridRoundTwo = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-3), endTime: now.UtcDateTime.AddDays(3));
        gridRoundTwo.SequenceNumber = 2;
        await _dbContext.SaveChangesAsync();

        var (service, _) = BuildServiceWithTwoGameKeys(
            now, gameKeyRoundDuration: TimeSpan.FromDays(3), otherGameKeyRoundDuration: TimeSpan.FromHours(30));

        var pathRound = await service.GenerateNextRoundIfNeededAsync(OtherGameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(pathRound.SequenceNumber, Is.EqualTo(1),
            "xg-path's first round must not inherit xg-grid's higher SequenceNumber counter");
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

        await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

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

        await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(_roundCloseService.Calls, Is.Empty);
    }

    [Test]
    public async Task REQ205_GenerateNextRoundIfNeeded_NoPredecessorExists_GeneratesFirstSuccessorWithoutAttemptingToClose()
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-1), endTime: now.UtcDateTime.AddHours(-1));
        var service = BuildService(now, TimeSpan.FromDays(3));

        await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

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
        await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });
        Assert.That(_roundCloseService.Calls, Has.Count.EqualTo(1));
        Assert.That(_roundCloseService.Calls[0].RoundId, Is.EqualTo(roundA.Id));
        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.EqualTo(1));
        Assert.That(await _dbContext.Rounds.CountAsync(), Is.EqualTo(3));

        // Second run, same clock, same repository state: "latest" is now C,
        // which hasn't started yet — the one-round-ahead early return applies,
        // so nothing further should be closed or generated.
        await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(_roundCloseService.Calls, Has.Count.EqualTo(1), "a repeated call must not close anything a second time");
        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.EqualTo(1), "a repeated call must not generate a second successor");
        Assert.That(await _dbContext.Rounds.CountAsync(), Is.EqualTo(3));
    }

    // ---- REQ-305: extend an unplayed round's schedule instead of ------------
    // generating a new one (docs/requirements-document.md §4.3). Shares the
    // "round A (closing) / round B (active, latest)" shape
    // REQ205_GenerateNextRoundIfNeeded_PredecessorOfLatestAlreadyEnded_ClosesItBeforeGeneratingSuccessor
    // above already establishes — "the closing round" is round A ("previous"
    // in the code/comments), "the active round" is round B ("latest").

    [Test]
    public async Task REQ305_GenerateNextRoundIfNeeded_ClosingRoundHasZeroGuesses_ExtendsActiveRoundEndTimeInsteadOfGeneratingNewRound()
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var roundA = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-8), endTime: now.UtcDateTime.AddDays(-4));
        var roundB = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-4), endTime: now.UtcDateTime.AddHours(-1));
        var service = BuildService(now, TimeSpan.FromDays(4));

        // Detach the seeded rounds before exercising the service: SeedRoundAsync's
        // own Add+SaveChangesAsync leaves them tracked in this test's shared
        // _dbContext, which would otherwise collide with the service's own
        // roundRepository.UpdateAsync(latest, ...) call below (EF Core refuses
        // to track a second, distinct instance for a key already tracked).
        // Production never hits this: a request-scoped DbContext never has
        // "latest" pre-tracked by anything before RoundGenerationService's own
        // AsNoTracking read of it.
        _dbContext.ChangeTracker.Clear();

        var result = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(_roundCloseService.Calls, Has.Count.EqualTo(1), "the closing round must still be closed exactly as REQ-302 requires");
        Assert.That(_roundCloseService.Calls[0].RoundId, Is.EqualTo(roundA.Id));
        Assert.That(result.Id, Is.EqualTo(roundB.Id), "no new Round is created — the active round itself is returned");
        Assert.That(result.SequenceNumber, Is.EqualTo(roundB.SequenceNumber), "no SequenceNumber is consumed this cycle");
        Assert.That(result.StartTime, Is.EqualTo(roundB.StartTime), "StartTime is unchanged by the extension");
        Assert.That(result.GameInstanceId, Is.EqualTo(roundB.GameInstanceId), "no new game-content instance is generated this cycle");
        Assert.That(result.EndTime, Is.EqualTo(roundB.EndTime + TimeSpan.FromDays(4)), "EndTime is extended by exactly one RoundDuration");
        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.Zero, "IGameModule.GenerateInstanceAsync must not be called this cycle");
        Assert.That(await _dbContext.Rounds.CountAsync(), Is.EqualTo(2), "no new Round row is persisted");

        var persisted = await _dbContext.Rounds.AsNoTracking().SingleAsync(r => r.Id == roundB.Id);
        Assert.That(persisted.EndTime, Is.EqualTo(roundB.EndTime + TimeSpan.FromDays(4)), "the extension is actually persisted, not just returned in-memory");
    }

    [Test]
    public async Task REQ305_GenerateNextRoundIfNeeded_ClosingRoundHasGuesses_GeneratesNewRoundAsBefore()
    {
        // Regression check: this REQ must not change REQ-301/304's existing
        // behavior for a closing round that was actually played.
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var roundA = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-8), endTime: now.UtcDateTime.AddDays(-4));
        var roundB = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-4), endTime: now.UtcDateTime.AddHours(-1));
        await SeedGuessAsync(roundA.Id);
        var service = BuildService(now, TimeSpan.FromDays(4));

        var result = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(result.Id, Is.Not.EqualTo(roundB.Id), "a new Round must still be generated when the closing round was played");
        Assert.That(result.StartTime, Is.EqualTo(roundB.EndTime));
        Assert.That(result.EndTime, Is.EqualTo(roundB.EndTime + TimeSpan.FromDays(4)));
        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.EqualTo(1));
        Assert.That(await _dbContext.Rounds.CountAsync(), Is.EqualTo(3));

        var roundBAfter = await _dbContext.Rounds.AsNoTracking().SingleAsync(r => r.Id == roundB.Id);
        Assert.That(roundBAfter.EndTime, Is.EqualTo(roundB.EndTime), "the active round's own EndTime must be untouched when a new round is generated normally");
    }

    [Test]
    public async Task REQ305_GenerateNextRoundIfNeeded_SecondConsecutiveZeroGuessEvaluation_ExtendsEndTimeAgainUncapped()
    {
        // Product decision (2026-09-11, requirements-document.md §4.3):
        // renewal is deliberately uncapped — this must keep extending the
        // same active round's EndTime indefinitely for as long as its
        // predecessor keeps being found with zero Guess rows, with no
        // renewal-count cap.
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var roundA = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-8), endTime: now.UtcDateTime.AddDays(-4));
        var roundB = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-4), endTime: now.UtcDateTime.AddHours(-1));
        var service = BuildService(now, TimeSpan.FromDays(4));

        // See the sibling zero-Guess test's comment for why this is needed
        // before any call that reaches the real roundRepository.UpdateAsync
        // path — required before EACH call here, since the first call's own
        // UpdateAsync leaves a newly-tracked instance behind that would
        // collide with the second call's own fresh AsNoTracking read.
        _dbContext.ChangeTracker.Clear();
        var firstResult = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });
        Assert.That(firstResult.EndTime, Is.EqualTo(roundB.EndTime + TimeSpan.FromDays(4)));

        _dbContext.ChangeTracker.Clear();
        var secondResult = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(secondResult.Id, Is.EqualTo(roundB.Id));
        Assert.That(secondResult.EndTime, Is.EqualTo(roundB.EndTime + TimeSpan.FromDays(4) + TimeSpan.FromDays(4)),
            "a second consecutive zero-Guess evaluation extends EndTime again, not capped at one renewal");
        Assert.That(_roundCloseService.Calls, Has.Count.EqualTo(2), "CloseRoundAsync is idempotent (its own doc comment) — a repeat call on an already-closed predecessor is harmless");
        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.Zero);
        Assert.That(await _dbContext.Rounds.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task REQ305_GenerateNextRoundIfNeeded_RoundDurationOverrideSupplied_ExtensionUsesOverrideNotConfiguredDefault()
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var roundA = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-8), endTime: now.UtcDateTime.AddDays(-4));
        var roundB = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-4), endTime: now.UtcDateTime.AddHours(-1));
        var service = BuildService(now, TimeSpan.FromDays(4));

        // See REQ305_GenerateNextRoundIfNeeded_ClosingRoundHasZeroGuesses_
        // ExtendsActiveRoundEndTimeInsteadOfGeneratingNewRound's comment above.
        _dbContext.ChangeTracker.Clear();

        var result = await service.GenerateNextRoundIfNeededAsync(
            GameKey,
            new RoundConfig { TemplateId = Guid.NewGuid() },
            roundDurationOverride: TimeSpan.FromHours(6));

        Assert.That(_roundCloseService.Calls[0].RoundId, Is.EqualTo(roundA.Id), "the closing round is still round A, regardless of the override");
        Assert.That(result.EndTime, Is.EqualTo(roundB.EndTime + TimeSpan.FromHours(6)),
            "the extension must use the per-call override, not the configured RoundDuration (same override semantics REQ-301 already defines)");
    }

    [Test]
    public async Task REQ305_GenerateNextRoundIfNeeded_GameKeyUsesModuleSuggestedTiming_NeverRenewsEvenWithZeroGuesses()
    {
        // ADR-0102's exclusion: a GameKey whose module supplies
        // SuggestedStartTime/SuggestedEndTime (currently "xg-predict") must
        // behave exactly as it does today, unaffected by this REQ.
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var roundA = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-8), endTime: now.UtcDateTime.AddDays(-4));
        var roundB = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-4), endTime: now.UtcDateTime.AddHours(-1));
        var service = BuildService(now, TimeSpan.FromDays(4), usesModuleSuggestedTiming: true);

        var result = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(_roundCloseService.Calls[0].RoundId, Is.EqualTo(roundA.Id), "the closing round is still closed exactly as REQ-302 requires, even for an excluded GameKey");
        Assert.That(result.Id, Is.Not.EqualTo(roundB.Id), "a new Round must still be generated — renewal never applies for this GameKey");
        Assert.That(result.StartTime, Is.EqualTo(roundB.EndTime));
        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.EqualTo(1));
        Assert.That(await _dbContext.Rounds.CountAsync(), Is.EqualTo(3));

        var roundBAfter = await _dbContext.Rounds.AsNoTracking().SingleAsync(r => r.Id == roundB.Id);
        Assert.That(roundBAfter.EndTime, Is.EqualTo(roundB.EndTime), "the active round's own EndTime must be untouched for an excluded GameKey");
    }

    [Test]
    public async Task REQ305_GenerateNextRoundIfNeeded_TwoGameKeysRegistered_OneGameKeysRenewalDoesNotAffectTheOthersGenerationCall()
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var roundA = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-8), endTime: now.UtcDateTime.AddDays(-4));
        var roundB = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-4), endTime: now.UtcDateTime.AddHours(-1));
        var (service, otherGameModule) = BuildServiceWithTwoGameKeys(
            now, gameKeyRoundDuration: TimeSpan.FromDays(4), otherGameKeyRoundDuration: TimeSpan.FromHours(30));

        // See REQ305_GenerateNextRoundIfNeeded_ClosingRoundHasZeroGuesses_
        // ExtendsActiveRoundEndTimeInsteadOfGeneratingNewRound's comment above.
        _dbContext.ChangeTracker.Clear();

        var gridResult = await service.GenerateNextRoundIfNeededAsync(GameKey, new RoundConfig { TemplateId = Guid.NewGuid() });
        var pathResult = await service.GenerateNextRoundIfNeededAsync(OtherGameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(gridResult.Id, Is.EqualTo(roundB.Id), "xg-grid's closing round had zero Guess rows — it renews rather than generating a new round");
        Assert.That(_gameModule.GenerateInstanceAsyncCallCount, Is.Zero);

        Assert.That(pathResult.GameKey, Is.EqualTo(OtherGameKey));
        Assert.That(pathResult.StartTime, Is.EqualTo(now.UtcDateTime), "xg-path has no round of its own yet — its own first-ever generation is unaffected by xg-grid's renewal");
        Assert.That(otherGameModule.GenerateInstanceAsyncCallCount, Is.EqualTo(1),
            "xg-grid's renewal must not suppress xg-path's own independent generation call");

        var gridRoundsAfter = await _dbContext.Rounds.Where(r => r.GameKey == GameKey).ToListAsync();
        Assert.That(gridRoundsAfter, Has.Count.EqualTo(2), "xg-grid's round set must be unaffected by xg-path's own generation call");
    }

    // ---- Bug fix regression (architecture review, 2026-09-11) --------------
    //
    // REQ-305's renewal check must ask the closing round's own
    // IRoundScoreSource whether anyone participated (ADR-0100) — never
    // assume participation lives in Guess rows directly. A GameKey shaped
    // like "xg-higher-lower" (chain-math timing, i.e.
    // UsesModuleSuggestedTiming = false — the flag itself never implies
    // anything about where participation data lives, see that flag's own
    // doc comment — but structurally never writes a Guess row at all, see
    // IRoundScoreSource.HasAnyParticipantAsync's own doc comment) is exactly
    // the case an earlier version of this check, which called
    // IGuessRepository directly from RoundGenerationService, got wrong: it
    // would always read zero Guess rows for this GameKey regardless of real
    // play, renewing forever and never generating a new round for it again.
    //
    // Uses FakeRoundScoreSource rather than the real
    // HigherLowerRoundScoreSource — XGArcade.Core.Tests must never
    // reference a game project (same boundary FakeRoundScoreSource's own
    // doc comment already documents for LeaderboardServiceTests' own
    // predict-shaped case). HigherLowerRoundScoreSource's own real
    // HasAnyParticipantAsync implementation is covered by its own dedicated
    // tests in XGArcade.Games.XGHigherLower.Tests instead.

    private const string GuessRowFreeGameKey = "xg-higher-lower";

    private (RoundGenerationService Service, FakeRoundScoreSource ScoreSource, FakeGameModule GameModule) BuildServiceForGuessRowFreeGameKey(
        DateTimeOffset now, TimeSpan roundDuration)
    {
        var guessRowFreeGameModule = new FakeGameModule(GuessRowFreeGameKey);
        var fakeScoreSource = new FakeRoundScoreSource();
        var roundScoreSourceResolver = new RoundScoreSourceResolver(new Dictionary<string, IRoundScoreSource>
        {
            [GuessRowFreeGameKey] = fakeScoreSource,
        });
        var service = new RoundGenerationService(
            _roundRepository,
            roundScoreSourceResolver,
            new GameModuleResolver([guessRowFreeGameModule]),
            _roundCloseService,
            new RoundSchedulingOptionsResolver(
                [new RoundSchedulingOptions { GameKey = GuessRowFreeGameKey, RoundDuration = roundDuration }]),
            new FixedTimeProvider(now));
        return (service, fakeScoreSource, guessRowFreeGameModule);
    }

    [Test]
    public async Task REQ305_GenerateNextRoundIfNeeded_GuessRowFreeGameKeyHasRealParticipation_GeneratesNewRoundInsteadOfRenewing()
    {
        // Real participation recorded via this GameKey's own IRoundScoreSource
        // (never a Guess row, which this GameKey structurally never writes) —
        // renewal must NOT fire. This is exactly the scenario the earlier
        // IGuessRepository-direct check got wrong: it would have read zero
        // Guess rows here regardless of this, renewed, and never generated a
        // new round for this GameKey again.
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var roundA = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-8), endTime: now.UtcDateTime.AddDays(-4), gameKey: GuessRowFreeGameKey);
        var roundB = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-4), endTime: now.UtcDateTime.AddHours(-1), gameKey: GuessRowFreeGameKey);
        var (service, scoreSource, gameModule) = BuildServiceForGuessRowFreeGameKey(now, TimeSpan.FromDays(4));
        Round? roundPassedToScoreSource = null;
        scoreSource.HasAnyParticipantResult = round =>
        {
            roundPassedToScoreSource = round;
            return true;
        };

        var result = await service.GenerateNextRoundIfNeededAsync(GuessRowFreeGameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(roundPassedToScoreSource?.Id, Is.EqualTo(roundA.Id), "HasAnyParticipantAsync must be asked about the closing round (previous), never the active one (latest)");
        Assert.That(result.Id, Is.Not.EqualTo(roundB.Id), "genuine participation recorded via this GameKey's own data shape must generate a new round, not renew");
        Assert.That(result.StartTime, Is.EqualTo(roundB.EndTime));
        Assert.That(gameModule.GenerateInstanceAsyncCallCount, Is.EqualTo(1));
        Assert.That(await _dbContext.Rounds.CountAsync(r => r.GameKey == GuessRowFreeGameKey), Is.EqualTo(3));

        var roundBAfter = await _dbContext.Rounds.AsNoTracking().SingleAsync(r => r.Id == roundB.Id);
        Assert.That(roundBAfter.EndTime, Is.EqualTo(roundB.EndTime), "the active round's own EndTime must be untouched when a new round is generated normally");
    }

    [Test]
    public async Task REQ305_GenerateNextRoundIfNeeded_GuessRowFreeGameKeyHasZeroParticipation_RenewsExactlyLikeGuessBasedGameKeys()
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var roundA = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-8), endTime: now.UtcDateTime.AddDays(-4), gameKey: GuessRowFreeGameKey);
        var roundB = await SeedRoundAsync(startTime: now.UtcDateTime.AddDays(-4), endTime: now.UtcDateTime.AddHours(-1), gameKey: GuessRowFreeGameKey);
        var (service, scoreSource, gameModule) = BuildServiceForGuessRowFreeGameKey(now, TimeSpan.FromDays(4));
        scoreSource.HasAnyParticipantResult = _ => false;
        // See REQ305_GenerateNextRoundIfNeeded_ClosingRoundHasZeroGuesses_
        // ExtendsActiveRoundEndTimeInsteadOfGeneratingNewRound's comment
        // above — the renewal path's own roundRepository.UpdateAsync(latest,
        // ...) call collides with SeedRoundAsync's own tracked instance
        // otherwise.
        _dbContext.ChangeTracker.Clear();

        var result = await service.GenerateNextRoundIfNeededAsync(GuessRowFreeGameKey, new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(result.Id, Is.EqualTo(roundB.Id), "no new Round is created — the active round itself is returned");
        Assert.That(result.EndTime, Is.EqualTo(roundB.EndTime + TimeSpan.FromDays(4)), "EndTime is extended by exactly one RoundDuration");
        Assert.That(gameModule.GenerateInstanceAsyncCallCount, Is.Zero, "IGameModule.GenerateInstanceAsync must not be called this cycle");
        Assert.That(await _dbContext.Rounds.CountAsync(r => r.GameKey == GuessRowFreeGameKey), Is.EqualTo(2), "no new Round row is persisted");

        var persisted = await _dbContext.Rounds.AsNoTracking().SingleAsync(r => r.Id == roundB.Id);
        Assert.That(persisted.EndTime, Is.EqualTo(roundB.EndTime + TimeSpan.FromDays(4)), "the extension is actually persisted, not just returned in-memory");
    }
}

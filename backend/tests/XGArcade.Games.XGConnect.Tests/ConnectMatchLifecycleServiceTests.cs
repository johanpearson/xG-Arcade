using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.TestSupport;

namespace XGArcade.Games.XGConnect.Tests;

// REQ-1405 (docs/requirements-document.md §4.15): match start, 6h forfeit
// timer, and resolution scaffolding. Same real-InMemory-backed-repository,
// no-mocking-framework pattern as ConnectTargetPickServiceTests —
// IConnectMatchRepository is exercised through the real
// ConnectMatchRepository against an InMemory-backed XGArcadeDbContext.
public class ConnectMatchLifecycleServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private XGArcadeDbContext _dbContext = null!;
    private IConnectMatchRepository _connectMatchRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _connectMatchRepository = new ConnectMatchRepository(_dbContext);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private ConnectMatchLifecycleService BuildService(DateTimeOffset now) =>
        new(_connectMatchRepository, new FixedTimeProvider(now));

    private async Task<ConnectMatch> CreateMatchAsync(Guid playerAUserId, Guid playerBUserId, DateTime createdAt) =>
        await _connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = playerAUserId,
            PlayerBUserId = playerBUserId,
            CreatedAt = createdAt,
        });

    // ---- StartMatchIfBothPicksLockedAsync -----------------------------------

    [Test]
    public async Task REQ1405_StartMatchIfBothPicksLockedAsync_FewerThanTwoPicks_NoOps()
    {
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, match.PlayerAUserId, Guid.NewGuid(), FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        await service.StartMatchIfBothPicksLockedAsync(match.Id);

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.AwaitingTargetPicks));
        Assert.That(stored.StartedAt, Is.Null);
        Assert.That(stored.DeadlineUtc, Is.Null);
    }

    [Test]
    public async Task REQ1405_StartMatchIfBothPicksLockedAsync_TwoPicksButNotBothLocked_NoOps()
    {
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, match.PlayerAUserId, Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, match.PlayerBUserId, Guid.NewGuid(), FixedNow.UtcDateTime);
        // Deliberately NOT calling LockTargetPicksForMatchAsync — both picks
        // exist but neither is locked.
        var service = BuildService(FixedNow);

        await service.StartMatchIfBothPicksLockedAsync(match.Id);

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.AwaitingTargetPicks));
        Assert.That(stored.StartedAt, Is.Null);
    }

    [Test]
    public async Task REQ1405_StartMatchIfBothPicksLockedAsync_BothPicksLocked_StartsMatchWithSixHourDeadline()
    {
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, match.PlayerAUserId, Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, match.PlayerBUserId, Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.LockTargetPicksForMatchAsync(match.Id);
        var service = BuildService(FixedNow);

        await service.StartMatchIfBothPicksLockedAsync(match.Id);

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.Active));
        Assert.That(stored.StartedAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(stored.DeadlineUtc, Is.EqualTo(FixedNow.UtcDateTime.AddHours(6)));
    }

    // ---- RunForfeitSweepAsync ------------------------------------------------

    [Test]
    public async Task REQ1405_RunForfeitSweepAsync_MatchPastDeadlineNeitherPlayerTerminal_ForfeitsBothAndResolvesInOneCall()
    {
        var startedAt = FixedNow.UtcDateTime.AddHours(-7);
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), startedAt);
        await _connectMatchRepository.StartMatchAsync(match.Id, startedAt, startedAt.AddHours(6));
        var service = BuildService(FixedNow);

        var result = await service.RunForfeitSweepAsync();

        Assert.That(result.PlayersForfeited, Is.EqualTo(2));
        Assert.That(result.MatchesResolved, Is.EqualTo(1), "both slots reached terminal in this same sweep call — resolution must not wait for a second pass");

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.PlayerATimedOutAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(stored.PlayerBTimedOutAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(stored.Status, Is.EqualTo(ConnectMatchStatus.Resolved));
        Assert.That(stored.Outcome, Is.EqualTo(ConnectMatchOutcome.Draw));
        Assert.That(stored.ResolvedAt, Is.EqualTo(FixedNow.UtcDateTime));
    }

    [Test]
    public async Task REQ1405_RunForfeitSweepAsync_MatchNotYetPastDeadline_LeavesItUntouched()
    {
        var startedAt = FixedNow.UtcDateTime.AddHours(-1);
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), startedAt);
        await _connectMatchRepository.StartMatchAsync(match.Id, startedAt, startedAt.AddHours(6));
        var service = BuildService(FixedNow);

        var result = await service.RunForfeitSweepAsync();

        Assert.That(result.PlayersForfeited, Is.EqualTo(0));
        Assert.That(result.MatchesResolved, Is.EqualTo(0));

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.Active));
        Assert.That(stored.PlayerATimedOutAt, Is.Null);
        Assert.That(stored.PlayerBTimedOutAt, Is.Null);
        Assert.That(stored.Outcome, Is.EqualTo(ConnectMatchOutcome.Pending));
    }

    // REQ-1405 GWT#2/#3: independent per-player enforcement — a slot already
    // terminal (seeded directly through the repository, simulating an
    // earlier-reached terminal state) is left alone, and the still-active
    // slot is swept and the match resolved immediately in the SAME sweep
    // call, never deferred to a later pass.
    [Test]
    public async Task REQ1405_RunForfeitSweepAsync_PlayerAAlreadyTerminal_MarksPlayerBAndResolvesImmediatelyInSameCall()
    {
        var startedAt = FixedNow.UtcDateTime.AddHours(-7);
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), startedAt);
        await _connectMatchRepository.StartMatchAsync(match.Id, startedAt, startedAt.AddHours(6));
        var earlierTerminalAt = FixedNow.UtcDateTime.AddHours(-2);
        await _connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: true, earlierTerminalAt);
        var service = BuildService(FixedNow);

        var result = await service.RunForfeitSweepAsync();

        Assert.That(result.PlayersForfeited, Is.EqualTo(1), "player A was already terminal — only player B's slot is newly forfeited this call");
        Assert.That(result.MatchesResolved, Is.EqualTo(1));

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        // Player A's original timestamp is untouched — idempotent, never
        // overwritten by this later sweep pass.
        Assert.That(stored!.PlayerATimedOutAt, Is.EqualTo(earlierTerminalAt));
        Assert.That(stored.PlayerBTimedOutAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(stored.Status, Is.EqualTo(ConnectMatchStatus.Resolved));
        Assert.That(stored.Outcome, Is.EqualTo(ConnectMatchOutcome.Draw));
        Assert.That(stored.ResolvedAt, Is.EqualTo(FixedNow.UtcDateTime));
    }

    // REQ-1405 GWT#3: resolution never happens with only one side terminal —
    // this constructs that intermediate state directly through the
    // repository (the sweep itself always evaluates both slots together for
    // a match once it's past deadline, so this state can't be observed
    // through the sweep alone) and asserts the match is untouched by a
    // sweep whose OWN match set doesn't include it (not yet past deadline).
    [Test]
    public async Task REQ1405_RunForfeitSweepAsync_OnlyOneSideTerminalAndNotPastDeadline_StatusStaysActiveOutcomeStaysPending()
    {
        var startedAt = FixedNow.UtcDateTime.AddHours(-1);
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), startedAt);
        await _connectMatchRepository.StartMatchAsync(match.Id, startedAt, startedAt.AddHours(6));
        await _connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: true, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.RunForfeitSweepAsync();

        Assert.That(result.MatchesResolved, Is.EqualTo(0), "only one side is terminal and the deadline hasn't passed — never resolved");

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.Active));
        Assert.That(stored.Outcome, Is.EqualTo(ConnectMatchOutcome.Pending));
        Assert.That(stored.PlayerATimedOutAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(stored.PlayerBTimedOutAt, Is.Null);
    }
}

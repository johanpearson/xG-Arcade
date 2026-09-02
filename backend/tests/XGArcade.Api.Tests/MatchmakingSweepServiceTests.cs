using Microsoft.EntityFrameworkCore;
using XGArcade.Api.Social;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Tests;

// REQ-1403 (docs/requirements-document.md §4.15): the periodic
// pairing/expiry sweep. Lives here (XGArcade.Api.Tests), not
// XGArcade.Core.Tests, because MatchmakingSweepService itself lives in
// XGArcade.Api.Social (ADR-0103 — it orchestrates Core.Social's
// IMatchmakingOptInRepository together with Games.XGConnect's
// IConnectMatchRepository, which Core.Social must never depend on). Same
// no-mocking-framework, real-InMemory-backed-repository pattern as
// ChallengeServiceTests/FriendServiceTests — only TimeProvider is faked
// (this project's own FixedTimeProvider, mirroring
// XGArcade.Core.Tests.Rounds.FixedTimeProvider's shape).
public class MatchmakingSweepServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private XGArcadeDbContext _dbContext = null!;
    private IMatchmakingOptInRepository _optInRepository = null!;
    private IConnectMatchRepository _connectMatchRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _optInRepository = new MatchmakingOptInRepository(_dbContext);
        _connectMatchRepository = new ConnectMatchRepository(_dbContext);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private MatchmakingSweepService CreateSweepService(DateTimeOffset now) =>
        new(_optInRepository, _connectMatchRepository, new FixedTimeProvider(now));

    private async Task<Guid> CreateUserAsync(string displayName)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = $"{displayName.ToLowerInvariant()}-{Guid.NewGuid()}@example.com",
            DisplayName = $"{displayName}-{Guid.NewGuid():N}",
            EmailConfirmed = true,
            CreatedAt = FixedNow.UtcDateTime,
            LastActiveAt = FixedNow.UtcDateTime,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user.Id;
    }

    private async Task<MatchmakingOptIn> CreateOptInAsync(Guid userId, DateTime optedInAt, DateTime expiresAt)
    {
        var optIn = new MatchmakingOptIn
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OptedInAt = optedInAt,
            ExpiresAt = expiresAt,
            Status = MatchmakingOptInStatus.Waiting,
        };
        _dbContext.MatchmakingOptIns.Add(optIn);
        await _dbContext.SaveChangesAsync();
        return optIn;
    }

    // ---- REQ-1403 GWT#1: opting in with no one else pairs nothing yet ------

    [Test]
    public async Task REQ1403_RunSweepAsync_SingleWaitingOptInWithNoOtherOptIns_PairsNothingAndLeavesItWaiting()
    {
        var userA = await CreateUserAsync("Alex");
        var optIn = await CreateOptInAsync(userA, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(12));
        var sweepService = CreateSweepService(FixedNow.AddMinutes(1));

        var result = await sweepService.RunSweepAsync();

        Assert.That(result.Paired, Is.EqualTo(0));
        Assert.That(result.Expired, Is.EqualTo(0));
        Assert.That(result.StillWaiting, Is.EqualTo(1));

        var persisted = await _optInRepository.GetOptInByIdAsync(optIn.Id);
        Assert.That(persisted!.Status, Is.EqualTo(MatchmakingOptInStatus.Waiting));
        Assert.That(persisted.ResultingMatchId, Is.Null);
    }

    // ---- REQ-1403 GWT#2: a second opt-in within the first's window pairs ---
    // ---- them and removes both from the pool --------------------------------

    [Test]
    public async Task REQ1403_RunSweepAsync_SecondOptInWithinFirstOptInsWindow_PairsBothIntoNewConnectMatchAndRemovesBothFromThePool()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        var optInA = await CreateOptInAsync(userA, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(12));
        // B opts in an hour later, well inside A's still-open 12h window.
        var laterNow = FixedNow.AddHours(1);
        var optInB = await CreateOptInAsync(userB, laterNow.UtcDateTime, laterNow.UtcDateTime.AddHours(12));
        var sweepService = CreateSweepService(laterNow.AddMinutes(1));

        var result = await sweepService.RunSweepAsync();

        Assert.That(result.Paired, Is.EqualTo(2));
        Assert.That(result.Expired, Is.EqualTo(0));
        Assert.That(result.StillWaiting, Is.EqualTo(0));

        var persistedA = await _optInRepository.GetOptInByIdAsync(optInA.Id);
        var persistedB = await _optInRepository.GetOptInByIdAsync(optInB.Id);
        Assert.That(persistedA!.Status, Is.EqualTo(MatchmakingOptInStatus.Paired));
        Assert.That(persistedB!.Status, Is.EqualTo(MatchmakingOptInStatus.Paired));
        Assert.That(persistedA.ResultingMatchId, Is.Not.Null);
        Assert.That(persistedA.ResultingMatchId, Is.EqualTo(persistedB.ResultingMatchId));

        var match = await _connectMatchRepository.GetMatchByIdAsync(persistedA.ResultingMatchId!.Value);
        Assert.That(match, Is.Not.Null);
        Assert.That(new[] { match!.PlayerAUserId, match.PlayerBUserId }, Is.EquivalentTo(new Guid?[] { userA, userB }));
        Assert.That(match.Status, Is.EqualTo(ConnectMatchStatus.AwaitingTargetPicks), "target-pick selection is a later story (S-211+), not started by this sweep");
    }

    // ---- REQ-1403 GWT#3: 12h expiry with no pairing --------------------------

    [Test]
    public async Task REQ1403_RunSweepAsync_OptInsWindowFullyElapsedWithNoPartner_ExpiresAtTheSweepAndCreatesNoMatch()
    {
        var userA = await CreateUserAsync("Alex");
        var optIn = await CreateOptInAsync(userA, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(12));

        // First sweep, shortly after opting in: still well within the 12h
        // window, no partner yet — must remain Waiting.
        var earlySweep = CreateSweepService(FixedNow.AddMinutes(5));
        var earlyResult = await earlySweep.RunSweepAsync();
        Assert.That(earlyResult.Expired, Is.EqualTo(0));
        Assert.That(earlyResult.StillWaiting, Is.EqualTo(1));

        // Second sweep, simulated well past the 12h mark, still with no
        // second player — must expire now, with no ConnectMatch created.
        var lateSweep = CreateSweepService(FixedNow.AddHours(13));
        var lateResult = await lateSweep.RunSweepAsync();

        Assert.That(lateResult.Paired, Is.EqualTo(0));
        Assert.That(lateResult.Expired, Is.EqualTo(1));
        Assert.That(lateResult.StillWaiting, Is.EqualTo(0));

        var persisted = await _optInRepository.GetOptInByIdAsync(optIn.Id);
        Assert.That(persisted!.Status, Is.EqualTo(MatchmakingOptInStatus.Expired));
        Assert.That(persisted.ResultingMatchId, Is.Null);
    }

    // ---- REQ-1403 GWT#4: no player double-booked from one sweep run --------

    [Test]
    public async Task REQ1403_RunSweepAsync_ThreeOrMoreOverlappingWaitingOptInsIncludingTheSameUserTwice_NeverDoubleBooksAUserAndLeavesLeftoversWaiting()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        var userC = await CreateUserAsync("Casey");

        // Opted-in order: A's first row, A's second row (degenerate double
        // opt-in), B, C — all well within a shared overlapping window.
        var t0 = FixedNow.UtcDateTime;
        var optInA1 = await CreateOptInAsync(userA, t0, t0.AddHours(12));
        var optInA2 = await CreateOptInAsync(userA, t0.AddMinutes(1), t0.AddMinutes(1).AddHours(12));
        var optInB = await CreateOptInAsync(userB, t0.AddMinutes(2), t0.AddMinutes(2).AddHours(12));
        var optInC = await CreateOptInAsync(userC, t0.AddMinutes(3), t0.AddMinutes(3).AddHours(12));

        var sweepService = CreateSweepService(FixedNow.AddMinutes(10));
        var result = await sweepService.RunSweepAsync();

        // 4 waiting rows, oldest-opted-in-first pairing: A1 pairs with B
        // (the earliest compatible different-user candidate). A2 can never
        // pair with A1 (same user) and, once A is already paired via A1,
        // can never pair with anyone else this run either — otherwise user
        // A would end up a participant in two separate matches from one
        // sweep. C then has no remaining compatible candidate (A2 is
        // excluded because its user is already paired) and stays Waiting
        // too. Exact greedy shape asserted below via persisted state, not
        // just the aggregate count.
        var persistedA1 = await _optInRepository.GetOptInByIdAsync(optInA1.Id);
        var persistedA2 = await _optInRepository.GetOptInByIdAsync(optInA2.Id);
        var persistedB = await _optInRepository.GetOptInByIdAsync(optInB.Id);
        var persistedC = await _optInRepository.GetOptInByIdAsync(optInC.Id);

        // The core guarantee under test: user A never appears as a
        // participant in more than one resulting ConnectMatch from this one
        // sweep run, regardless of exactly which of their two rows paired.
        var allPaired = new[] { persistedA1!, persistedA2!, persistedB!, persistedC! }
            .Where(o => o!.Status == MatchmakingOptInStatus.Paired)
            .ToList();
        var participantUserIdsPerMatch = allPaired
            .GroupBy(o => o.ResultingMatchId)
            .Select(g => g.Select(o => o.UserId).ToList())
            .ToList();
        foreach (var participants in participantUserIdsPerMatch)
        {
            Assert.That(participants, Has.Count.EqualTo(2));
            Assert.That(participants[0], Is.Not.EqualTo(participants[1]), "a user must never be paired with their own second opt-in row");
        }

        var pairedUserIds = allPaired.Select(o => o.UserId).ToList();
        Assert.That(pairedUserIds.Distinct().Count(), Is.EqualTo(pairedUserIds.Count),
            "no single UserId may appear as a participant in more than one resulting ConnectMatch from this sweep run");

        // Whatever didn't get paired stays Waiting — never dropped. Exact
        // expected shape per the greedy trace above: A1+B paired, A2 and C
        // still Waiting.
        Assert.That(persistedA1!.Status, Is.EqualTo(MatchmakingOptInStatus.Paired));
        Assert.That(persistedB!.Status, Is.EqualTo(MatchmakingOptInStatus.Paired));
        Assert.That(persistedA1.ResultingMatchId, Is.EqualTo(persistedB.ResultingMatchId));
        Assert.That(persistedA2!.Status, Is.EqualTo(MatchmakingOptInStatus.Waiting));
        Assert.That(persistedA2.ResultingMatchId, Is.Null);
        Assert.That(persistedC!.Status, Is.EqualTo(MatchmakingOptInStatus.Waiting));
        Assert.That(persistedC.ResultingMatchId, Is.Null);

        var stillWaitingRows = new[] { persistedA1, persistedA2, persistedB, persistedC }
            .Where(o => o!.Status == MatchmakingOptInStatus.Waiting)
            .ToList();
        Assert.That(result.StillWaiting, Is.EqualTo(stillWaitingRows.Count));
        Assert.That(result.Paired + result.StillWaiting, Is.EqualTo(4), "every one of the 4 waiting rows must be accounted for (paired or still waiting), never silently dropped");
    }
}

using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Social;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.TestSupport;

namespace XGArcade.Core.Tests.Social;

// REQ-1402 (docs/requirements-document.md §4.15): direct-challenge
// send/accept/decline business logic. Same no-mocking-framework,
// real-InMemory-backed-repository pattern as FriendServiceTests —
// IChallengeRepository/IFriendRepository/IUserRepository are exercised
// through the real Challenge/Friend/UserRepository against an
// InMemory-backed XGArcadeDbContext; only TimeProvider is faked
// (FixedTimeProvider, XGArcade.TestSupport).
//
// ChallengeService.AcceptChallengeAsync deliberately does NOT create a
// ConnectMatch row (ADR-0103 — that's XGArcade.Api's orchestration job, not
// Core.Social's) — these tests assert the Challenge-side contract only
// (Accepted status + the resultingMatchId this test supplies being
// persisted verbatim), the same boundary ChallengeService itself observes.
public class ChallengeServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private XGArcadeDbContext _dbContext = null!;
    private IChallengeRepository _challengeRepository = null!;
    private IFriendRepository _friendRepository = null!;
    private IUserRepository _userRepository = null!;
    private ChallengeService _service = null!;
    private FriendService _friendService = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _challengeRepository = new ChallengeRepository(_dbContext);
        _friendRepository = new FriendRepository(_dbContext);
        _userRepository = new UserRepository(_dbContext);
        _service = new ChallengeService(_challengeRepository, _friendRepository, new FixedTimeProvider(FixedNow));
        _friendService = new FriendService(_friendRepository, _userRepository, new FixedTimeProvider(FixedNow));
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private async Task<Guid> CreateUserAsync(string displayName)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = $"{displayName.ToLowerInvariant()}@example.com",
            DisplayName = displayName,
            EmailConfirmed = true,
            CreatedAt = FixedNow.UtcDateTime,
            LastActiveAt = FixedNow.UtcDateTime,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user.Id;
    }

    // Both users must be friends before every test that expects Send to
    // succeed — mirrors REQ-1402's own "Given User A and User B are
    // friends" precondition via the real REQ-1401 flow rather than
    // fabricating a Friendship row directly.
    private async Task MakeFriendsAsync(Guid userA, Guid userB)
    {
        var sendResult = await _friendService.SendFriendRequestAsync(userA, userB);
        await _friendService.AcceptFriendRequestAsync(sendResult.FriendRequest!.Id, userB);
    }

    // ---- REQ-1402 GWT#1: send between friends ------------------------------

    [Test]
    public async Task REQ1402_SendChallengeAsync_UsersAreFriends_CreatesPendingChallengeVisibleToChallengedUser()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        await MakeFriendsAsync(userA, userB);

        var result = await _service.SendChallengeAsync(userA, userB);

        Assert.That(result.Outcome, Is.EqualTo(SendChallengeOutcome.Sent));
        Assert.That(result.Challenge, Is.Not.Null);
        Assert.That(result.Challenge!.ChallengerUserId, Is.EqualTo(userA));
        Assert.That(result.Challenge.ChallengedUserId, Is.EqualTo(userB));
        Assert.That(result.Challenge.Status, Is.EqualTo(ChallengeStatus.Pending));

        var pendingForB = await _service.GetPendingChallengesAsync(userB);
        Assert.That(pendingForB.Select(c => c.Id), Is.EquivalentTo(new[] { result.Challenge.Id }));
    }

    // ---- REQ-1402 GWT#1 continued: duplicate-pending rejection, both -------
    // ---- directions ---------------------------------------------------------

    [Test]
    public async Task REQ1402_SendChallengeAsync_SecondAttemptFromSameChallengerWhileFirstPending_ReturnsDuplicatePendingWithoutCreatingSecondChallenge()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        await MakeFriendsAsync(userA, userB);
        await _service.SendChallengeAsync(userA, userB);

        var secondAttempt = await _service.SendChallengeAsync(userA, userB);

        Assert.That(secondAttempt.Outcome, Is.EqualTo(SendChallengeOutcome.DuplicatePending));
        Assert.That(secondAttempt.Challenge, Is.Null);
        var pendingForB = await _service.GetPendingChallengesAsync(userB);
        Assert.That(pendingForB, Has.Count.EqualTo(1), "no second, duplicate pending challenge may exist from A to B");
    }

    [Test]
    public async Task REQ1402_SendChallengeAsync_PendingChallengeExistsFromChallengerToChallenged_BlocksTheReverseDirectionAttempt()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        await MakeFriendsAsync(userA, userB);
        await _service.SendChallengeAsync(userA, userB); // A -> B pending

        var reverseAttempt = await _service.SendChallengeAsync(userB, userA); // B -> A

        Assert.That(reverseAttempt.Outcome, Is.EqualTo(SendChallengeOutcome.DuplicatePending));
        Assert.That(reverseAttempt.Challenge, Is.Null);
    }

    // ---- REQ-1402 GWT#4: non-friend rejection -------------------------------

    [Test]
    public async Task REQ1402_SendChallengeAsync_UsersAreNotFriends_ReturnsNotFriendsWithoutCreatingAChallenge()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");

        var result = await _service.SendChallengeAsync(userA, userB);

        Assert.That(result.Outcome, Is.EqualTo(SendChallengeOutcome.NotFriends));
        Assert.That(result.Challenge, Is.Null);
        Assert.That(await _service.GetPendingChallengesAsync(userB), Is.Empty);
    }

    // ---- REQ-1402 GWT#2: accept ----------------------------------------------

    [Test]
    public async Task REQ1402_AcceptChallengeAsync_PendingChallenge_ResolvesAsAcceptedWithTheSuppliedResultingMatchIdAndClearsThePendingList()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        await MakeFriendsAsync(userA, userB);
        var sendResult = await _service.SendChallengeAsync(userA, userB);
        var matchId = Guid.NewGuid();

        var acceptResult = await _service.AcceptChallengeAsync(sendResult.Challenge!.Id, userB, matchId);

        Assert.That(acceptResult.Outcome, Is.EqualTo(ResolveChallengeOutcome.Resolved));
        Assert.That(acceptResult.Challenge!.Status, Is.EqualTo(ChallengeStatus.Accepted));
        Assert.That(acceptResult.Challenge.ResolvedAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(acceptResult.Challenge.ResultingMatchId, Is.EqualTo(matchId));

        // Persisted, not just the in-memory return value — reload via the
        // repository directly to prove UpdateChallengeStatusAsync's write
        // actually happened.
        var persisted = await _challengeRepository.GetChallengeByIdAsync(sendResult.Challenge.Id);
        Assert.That(persisted!.Status, Is.EqualTo(ChallengeStatus.Accepted));
        Assert.That(persisted.ResultingMatchId, Is.EqualTo(matchId));

        Assert.That(await _service.GetPendingChallengesAsync(userB), Is.Empty);
    }

    // ---- REQ-1402 GWT#3: decline, then resend later --------------------------

    [Test]
    public async Task REQ1402_DeclineChallengeAsync_PendingChallenge_ResolvesAsDeclinedWithNoResultingMatchAndClearsThePendingList()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        await MakeFriendsAsync(userA, userB);
        var sendResult = await _service.SendChallengeAsync(userA, userB);

        var declineResult = await _service.DeclineChallengeAsync(sendResult.Challenge!.Id, userB);

        Assert.That(declineResult.Outcome, Is.EqualTo(ResolveChallengeOutcome.Resolved));
        Assert.That(declineResult.Challenge!.Status, Is.EqualTo(ChallengeStatus.Declined));
        Assert.That(declineResult.Challenge.ResultingMatchId, Is.Null);
        Assert.That(await _service.GetPendingChallengesAsync(userB), Is.Empty);
    }

    [Test]
    public async Task REQ1402_SendChallengeAsync_AfterAnEarlierChallengeWasDeclined_ChallengerMaySendANewChallengeLater()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        await MakeFriendsAsync(userA, userB);
        var firstSend = await _service.SendChallengeAsync(userA, userB);
        await _service.DeclineChallengeAsync(firstSend.Challenge!.Id, userB);

        var secondSend = await _service.SendChallengeAsync(userA, userB);

        Assert.That(secondSend.Outcome, Is.EqualTo(SendChallengeOutcome.Sent));
        Assert.That(secondSend.Challenge, Is.Not.Null);
        Assert.That(secondSend.Challenge!.Id, Is.Not.EqualTo(firstSend.Challenge!.Id));
        var pendingForB = await _service.GetPendingChallengesAsync(userB);
        Assert.That(pendingForB.Select(c => c.Id), Is.EquivalentTo(new[] { secondSend.Challenge.Id }));
    }

    // ---- Additional branch coverage (not a distinct GWT clause, but the ----
    // ---- same outcome enum REQ-1402's resolve path is required to surface) --

    [Test]
    public async Task REQ1402_AcceptChallengeAsync_UnknownChallengeId_ReturnsNotFound()
    {
        var result = await _service.AcceptChallengeAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.That(result.Outcome, Is.EqualTo(ResolveChallengeOutcome.NotFound));
        Assert.That(result.Challenge, Is.Null);
    }

    // Only the challenged user may resolve the challenge sent to them —
    // otherwise the challenger could auto-resolve their own outgoing
    // challenge.
    [Test]
    public async Task REQ1402_AcceptChallengeAsync_CalledByTheChallengerRatherThanTheChallengedUser_ReturnsNotYourChallengeAndCreatesNoResolution()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        await MakeFriendsAsync(userA, userB);
        var sendResult = await _service.SendChallengeAsync(userA, userB);

        var result = await _service.AcceptChallengeAsync(sendResult.Challenge!.Id, userA, Guid.NewGuid());

        Assert.That(result.Outcome, Is.EqualTo(ResolveChallengeOutcome.NotYourChallenge));
        var persisted = await _challengeRepository.GetChallengeByIdAsync(sendResult.Challenge.Id);
        Assert.That(persisted!.Status, Is.EqualTo(ChallengeStatus.Pending));
    }

    [Test]
    public async Task REQ1402_AcceptChallengeAsync_ChallengeAlreadyResolved_ReturnsAlreadyResolvedAndDoesNotOverwriteTheFirstResolution()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        await MakeFriendsAsync(userA, userB);
        var sendResult = await _service.SendChallengeAsync(userA, userB);
        var firstMatchId = Guid.NewGuid();
        await _service.AcceptChallengeAsync(sendResult.Challenge!.Id, userB, firstMatchId);

        var secondAccept = await _service.AcceptChallengeAsync(sendResult.Challenge.Id, userB, Guid.NewGuid());

        Assert.That(secondAccept.Outcome, Is.EqualTo(ResolveChallengeOutcome.AlreadyResolved));
        var persisted = await _challengeRepository.GetChallengeByIdAsync(sendResult.Challenge.Id);
        Assert.That(persisted!.ResultingMatchId, Is.EqualTo(firstMatchId));
    }

    [Test]
    public async Task REQ1402_DeclineChallengeAsync_CalledByTheChallengerRatherThanTheChallengedUser_ReturnsNotYourChallenge()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        await MakeFriendsAsync(userA, userB);
        var sendResult = await _service.SendChallengeAsync(userA, userB);

        var result = await _service.DeclineChallengeAsync(sendResult.Challenge!.Id, userA);

        Assert.That(result.Outcome, Is.EqualTo(ResolveChallengeOutcome.NotYourChallenge));
        var persisted = await _challengeRepository.GetChallengeByIdAsync(sendResult.Challenge.Id);
        Assert.That(persisted!.Status, Is.EqualTo(ChallengeStatus.Pending));
    }
}

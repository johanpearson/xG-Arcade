using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Social;
using XGArcade.Core.Tests.Rounds;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Tests.Social;

// REQ-1401 (docs/requirements-document.md §4.15): friend request
// send/accept/decline business logic. Same no-mocking-framework,
// real-InMemory-backed-repository pattern as LeagueServiceTests —
// IFriendRepository/IUserRepository are exercised through the real
// FriendRepository/UserRepository against an InMemory-backed
// XGArcadeDbContext; only TimeProvider is faked (FixedTimeProvider,
// XGArcade.Core.Tests.Rounds), matching IncidentReportServiceTests's own
// reuse of that same fixture.
public class FriendServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private XGArcadeDbContext _dbContext = null!;
    private IFriendRepository _friendRepository = null!;
    private IUserRepository _userRepository = null!;
    private FriendService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _friendRepository = new FriendRepository(_dbContext);
        _userRepository = new UserRepository(_dbContext);
        _service = new FriendService(_friendRepository, _userRepository, new FixedTimeProvider(FixedNow));
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

    // ---- REQ-1401 GWT#1: send with no existing relationship ---------------

    [Test]
    public async Task REQ1401_SendFriendRequestAsync_NoExistingRelationship_CreatesPendingRequestVisibleToRecipient()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");

        var result = await _service.SendFriendRequestAsync(userA, userB);

        Assert.That(result.Outcome, Is.EqualTo(SendFriendRequestOutcome.Sent));
        Assert.That(result.FriendRequest, Is.Not.Null);
        Assert.That(result.FriendRequest!.RequesterUserId, Is.EqualTo(userA));
        Assert.That(result.FriendRequest.RecipientUserId, Is.EqualTo(userB));
        Assert.That(result.FriendRequest.Status, Is.EqualTo(FriendRequestStatus.Pending));

        var pendingForB = await _service.GetPendingFriendRequestsAsync(userB);
        Assert.That(pendingForB.Select(r => r.Id), Is.EquivalentTo(new[] { result.FriendRequest.Id }));
    }

    [Test]
    public async Task REQ1401_SendFriendRequestAsync_SecondAttemptFromSameRequesterWhileFirstPending_ReturnsDuplicatePendingWithoutCreatingSecondRequest()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        await _service.SendFriendRequestAsync(userA, userB);

        var secondAttempt = await _service.SendFriendRequestAsync(userA, userB);

        Assert.That(secondAttempt.Outcome, Is.EqualTo(SendFriendRequestOutcome.DuplicatePending));
        Assert.That(secondAttempt.FriendRequest, Is.Null);
        var pendingForB = await _service.GetPendingFriendRequestsAsync(userB);
        Assert.That(pendingForB, Has.Count.EqualTo(1), "no second, duplicate pending request may exist from A to B");
    }

    // ---- REQ-1401 GWT#4: duplicate-pending rejection, both directions -----

    [Test]
    public async Task REQ1401_SendFriendRequestAsync_PendingRequestExistsFromRequesterToRecipient_BlocksRecipientAttemptingTheReverseDirection()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        await _service.SendFriendRequestAsync(userA, userB); // A -> B pending

        var reverseAttempt = await _service.SendFriendRequestAsync(userB, userA); // B -> A

        Assert.That(reverseAttempt.Outcome, Is.EqualTo(SendFriendRequestOutcome.DuplicatePending));
        Assert.That(reverseAttempt.FriendRequest, Is.Null);
    }

    [Test]
    public async Task REQ1401_SendFriendRequestAsync_PendingRequestExistsFromRecipientToRequester_BlocksTheOriginalDirectionAttempt()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        await _service.SendFriendRequestAsync(userB, userA); // B -> A pending

        var forwardAttempt = await _service.SendFriendRequestAsync(userA, userB); // A -> B

        Assert.That(forwardAttempt.Outcome, Is.EqualTo(SendFriendRequestOutcome.DuplicatePending));
        Assert.That(forwardAttempt.FriendRequest, Is.Null);
    }

    // ---- REQ-1401 GWT#2: accept -------------------------------------------

    [Test]
    public async Task REQ1401_AcceptFriendRequestAsync_PendingRequest_CreatesSymmetricFriendshipAndResolvesTheRequestOutOfBothPendingLists()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        var sendResult = await _service.SendFriendRequestAsync(userA, userB);

        var acceptResult = await _service.AcceptFriendRequestAsync(sendResult.FriendRequest!.Id, userB);

        Assert.That(acceptResult.Outcome, Is.EqualTo(ResolveFriendRequestOutcome.Resolved));
        Assert.That(acceptResult.FriendRequest!.Status, Is.EqualTo(FriendRequestStatus.Accepted));
        Assert.That(acceptResult.FriendRequest.ResolvedAt, Is.EqualTo(FixedNow.UtcDateTime));

        // Symmetric — visible as a friendship from either side.
        var friendshipsForA = await _service.GetFriendshipsAsync(userA);
        var friendshipsForB = await _service.GetFriendshipsAsync(userB);
        Assert.That(friendshipsForA, Has.Count.EqualTo(1));
        Assert.That(friendshipsForB, Has.Count.EqualTo(1));
        Assert.That(friendshipsForA[0].Id, Is.EqualTo(friendshipsForB[0].Id));

        // No longer appears in the recipient's pending list.
        // GetPendingFriendRequestsAsync is recipient-scoped
        // (IFriendRepository.GetPendingFriendRequestsForUserAsync filters on
        // RecipientUserId) — userA is the requester here, never the
        // recipient, so asserting GetPendingFriendRequestsAsync(userA) would
        // be vacuously Is.Empty regardless of whether accept worked and
        // wouldn't exercise the requester's side at all. The symmetric
        // friendship assertions above (visible identically via
        // GetFriendshipsAsync from either userA's or userB's side) are what
        // actually prove the request resolved out of "either player's"
        // relevant view.
        Assert.That(await _service.GetPendingFriendRequestsAsync(userB), Is.Empty);
    }

    // ---- REQ-1401 GWT#3: decline, then resend later ------------------------

    [Test]
    public async Task REQ1401_DeclineFriendRequestAsync_PendingRequest_ResolvesAsDeclinedWithoutCreatingAFriendshipAndClearsThePendingList()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        var sendResult = await _service.SendFriendRequestAsync(userA, userB);

        var declineResult = await _service.DeclineFriendRequestAsync(sendResult.FriendRequest!.Id, userB);

        Assert.That(declineResult.Outcome, Is.EqualTo(ResolveFriendRequestOutcome.Resolved));
        Assert.That(declineResult.FriendRequest!.Status, Is.EqualTo(FriendRequestStatus.Declined));
        Assert.That(await _service.GetFriendshipsAsync(userA), Is.Empty);
        Assert.That(await _service.GetFriendshipsAsync(userB), Is.Empty);
        Assert.That(await _service.GetPendingFriendRequestsAsync(userB), Is.Empty);
    }

    [Test]
    public async Task REQ1401_SendFriendRequestAsync_AfterAnEarlierRequestWasDeclined_RequesterMaySendANewRequestLater()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        var firstSend = await _service.SendFriendRequestAsync(userA, userB);
        await _service.DeclineFriendRequestAsync(firstSend.FriendRequest!.Id, userB);

        var secondSend = await _service.SendFriendRequestAsync(userA, userB);

        Assert.That(secondSend.Outcome, Is.EqualTo(SendFriendRequestOutcome.Sent));
        Assert.That(secondSend.FriendRequest, Is.Not.Null);
        Assert.That(secondSend.FriendRequest!.Id, Is.Not.EqualTo(firstSend.FriendRequest!.Id));
        var pendingForB = await _service.GetPendingFriendRequestsAsync(userB);
        Assert.That(pendingForB.Select(r => r.Id), Is.EquivalentTo(new[] { secondSend.FriendRequest.Id }));
    }

    // ---- REQ-1401 GWT#4: already-friends rejection -------------------------

    [Test]
    public async Task REQ1401_SendFriendRequestAsync_UsersAreAlreadyFriends_ReturnsAlreadyFriendsWithoutCreatingARequest()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        var firstSend = await _service.SendFriendRequestAsync(userA, userB);
        await _service.AcceptFriendRequestAsync(firstSend.FriendRequest!.Id, userB);

        var result = await _service.SendFriendRequestAsync(userA, userB);

        Assert.That(result.Outcome, Is.EqualTo(SendFriendRequestOutcome.AlreadyFriends));
        Assert.That(result.FriendRequest, Is.Null);
        Assert.That(await _service.GetPendingFriendRequestsAsync(userB), Is.Empty);
    }

    // ---- REQ-1401 GWT#5: self-request rejection -----------------------------

    [Test]
    public async Task REQ1401_SendFriendRequestAsync_RequesterAndRecipientAreTheSameUser_ReturnsSelfRequestWithoutCreatingARequest()
    {
        var userA = await CreateUserAsync("Alex");

        var result = await _service.SendFriendRequestAsync(userA, userA);

        Assert.That(result.Outcome, Is.EqualTo(SendFriendRequestOutcome.SelfRequest));
        Assert.That(result.FriendRequest, Is.Null);
        Assert.That(await _service.GetPendingFriendRequestsAsync(userA), Is.Empty);
    }

    // ---- Additional branch coverage (not a distinct GWT clause, but the ----
    // ---- same outcome enum REQ-1401's send path is required to surface) ----

    [Test]
    public async Task REQ1401_SendFriendRequestAsync_RecipientDoesNotExist_ReturnsRecipientNotFoundWithoutCreatingARequest()
    {
        var userA = await CreateUserAsync("Alex");

        var result = await _service.SendFriendRequestAsync(userA, Guid.NewGuid());

        Assert.That(result.Outcome, Is.EqualTo(SendFriendRequestOutcome.RecipientNotFound));
        Assert.That(result.FriendRequest, Is.Null);
    }

    [Test]
    public async Task REQ1401_AcceptFriendRequestAsync_UnknownRequestId_ReturnsNotFound()
    {
        var result = await _service.AcceptFriendRequestAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.That(result.Outcome, Is.EqualTo(ResolveFriendRequestOutcome.NotFound));
        Assert.That(result.FriendRequest, Is.Null);
    }

    // Only User B (the recipient) may resolve the request that was sent to
    // them — otherwise the requester could auto-resolve their own outgoing
    // request, which REQ-1401's "User B accepts it" framing never allows.
    [Test]
    public async Task REQ1401_AcceptFriendRequestAsync_CalledByTheRequesterRatherThanTheRecipient_ReturnsNotYourRequestAndCreatesNoFriendship()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        var sendResult = await _service.SendFriendRequestAsync(userA, userB);

        var result = await _service.AcceptFriendRequestAsync(sendResult.FriendRequest!.Id, userA);

        Assert.That(result.Outcome, Is.EqualTo(ResolveFriendRequestOutcome.NotYourRequest));
        Assert.That(await _service.GetFriendshipsAsync(userA), Is.Empty);
        Assert.That(await _service.GetFriendshipsAsync(userB), Is.Empty);
    }

    [Test]
    public async Task REQ1401_AcceptFriendRequestAsync_RequestAlreadyResolved_ReturnsAlreadyResolvedAndDoesNotCreateASecondFriendship()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        var sendResult = await _service.SendFriendRequestAsync(userA, userB);
        await _service.AcceptFriendRequestAsync(sendResult.FriendRequest!.Id, userB);

        var secondAccept = await _service.AcceptFriendRequestAsync(sendResult.FriendRequest.Id, userB);

        Assert.That(secondAccept.Outcome, Is.EqualTo(ResolveFriendRequestOutcome.AlreadyResolved));
        Assert.That(await _service.GetFriendshipsAsync(userA), Has.Count.EqualTo(1));
    }

    // Same NotYourRequest/AlreadyResolved branches as the Accept-side tests
    // above, exercised via DeclineFriendRequestAsync instead — both public
    // methods delegate to the same private ResolveFriendRequestAsync, but
    // each caller path is worth its own explicit coverage.
    [Test]
    public async Task REQ1401_DeclineFriendRequestAsync_CalledByTheRequesterRatherThanTheRecipient_ReturnsNotYourRequestAndCreatesNoFriendship()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        var sendResult = await _service.SendFriendRequestAsync(userA, userB);

        var result = await _service.DeclineFriendRequestAsync(sendResult.FriendRequest!.Id, userA);

        Assert.That(result.Outcome, Is.EqualTo(ResolveFriendRequestOutcome.NotYourRequest));
        Assert.That(await _service.GetFriendshipsAsync(userA), Is.Empty);
        Assert.That(await _service.GetFriendshipsAsync(userB), Is.Empty);
    }

    [Test]
    public async Task REQ1401_DeclineFriendRequestAsync_RequestAlreadyResolved_ReturnsAlreadyResolved()
    {
        var userA = await CreateUserAsync("Alex");
        var userB = await CreateUserAsync("Blair");
        var sendResult = await _service.SendFriendRequestAsync(userA, userB);
        await _service.DeclineFriendRequestAsync(sendResult.FriendRequest!.Id, userB);

        var secondDecline = await _service.DeclineFriendRequestAsync(sendResult.FriendRequest.Id, userB);

        Assert.That(secondDecline.Outcome, Is.EqualTo(ResolveFriendRequestOutcome.AlreadyResolved));
        Assert.That(await _service.GetFriendshipsAsync(userA), Is.Empty);
    }
}

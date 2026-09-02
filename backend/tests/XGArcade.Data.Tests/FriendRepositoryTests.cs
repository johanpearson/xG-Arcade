using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// Core.Social (COMP-16)/ADR-0103, S-208: FriendRepository's basic
// persistence round-trips for FriendRequest/Friendship. This story scaffolds
// schema + repository CRUD only — no accept/decline/duplicate-request
// business logic (that's S-209), so these tests cover Add/Get/list/update
// primitives, not REQ-1401's full Given/When/Then acceptance criteria.
public class FriendRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IFriendRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new FriendRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    // ---- FriendRequest CRUD --------------------------------------------

    [Test]
    public async Task AddFriendRequestAsync_ThenGetFriendRequestByIdAsync_PersistsAndRetrievesTheRow()
    {
        var requesterId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var friendRequest = new FriendRequest
        {
            Id = Guid.NewGuid(),
            RequesterUserId = requesterId,
            RecipientUserId = recipientId,
            CreatedAt = createdAt,
        };

        var added = await _repository.AddFriendRequestAsync(friendRequest);

        Assert.That(added, Is.SameAs(friendRequest));
        var result = await _repository.GetFriendRequestByIdAsync(friendRequest.Id);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.RequesterUserId, Is.EqualTo(requesterId));
        Assert.That(result.RecipientUserId, Is.EqualTo(recipientId));
        Assert.That(result.Status, Is.EqualTo(FriendRequestStatus.Pending), "Status defaults to Pending");
        Assert.That(result.CreatedAt, Is.EqualTo(createdAt));
        Assert.That(result.ResolvedAt, Is.Null);
    }

    [Test]
    public async Task GetFriendRequestByIdAsync_UnknownId_ReturnsNull()
    {
        var result = await _repository.GetFriendRequestByIdAsync(Guid.NewGuid());

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetPendingFriendRequestsForUserAsync_ReturnsOnlyPendingRequestsForThatRecipient()
    {
        var recipientId = Guid.NewGuid();
        var otherRecipientId = Guid.NewGuid();
        var pendingRequest = new FriendRequest
        {
            Id = Guid.NewGuid(), RequesterUserId = Guid.NewGuid(), RecipientUserId = recipientId,
            CreatedAt = DateTime.UtcNow,
        };
        var resolvedRequest = new FriendRequest
        {
            Id = Guid.NewGuid(), RequesterUserId = Guid.NewGuid(), RecipientUserId = recipientId,
            Status = FriendRequestStatus.Accepted, CreatedAt = DateTime.UtcNow, ResolvedAt = DateTime.UtcNow,
        };
        var otherUsersRequest = new FriendRequest
        {
            Id = Guid.NewGuid(), RequesterUserId = Guid.NewGuid(), RecipientUserId = otherRecipientId,
            CreatedAt = DateTime.UtcNow,
        };
        await _repository.AddFriendRequestAsync(pendingRequest);
        await _repository.AddFriendRequestAsync(resolvedRequest);
        await _repository.AddFriendRequestAsync(otherUsersRequest);

        var result = await _repository.GetPendingFriendRequestsForUserAsync(recipientId);

        Assert.That(result.Select(r => r.Id), Is.EquivalentTo(new[] { pendingRequest.Id }));
    }

    [Test]
    public async Task UpdateFriendRequestStatusAsync_SetsStatusAndResolvedAt()
    {
        var friendRequest = new FriendRequest
        {
            Id = Guid.NewGuid(), RequesterUserId = Guid.NewGuid(), RecipientUserId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
        };
        await _repository.AddFriendRequestAsync(friendRequest);
        var resolvedAt = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);

        await _repository.UpdateFriendRequestStatusAsync(friendRequest.Id, FriendRequestStatus.Accepted, resolvedAt);

        var result = await _repository.GetFriendRequestByIdAsync(friendRequest.Id);
        Assert.That(result!.Status, Is.EqualTo(FriendRequestStatus.Accepted));
        Assert.That(result.ResolvedAt, Is.EqualTo(resolvedAt));
    }

    [Test]
    public void UpdateFriendRequestStatusAsync_UnknownId_Throws()
    {
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.UpdateFriendRequestStatusAsync(Guid.NewGuid(), FriendRequestStatus.Declined, DateTime.UtcNow));
    }

    // ---- Friendship CRUD -------------------------------------------------

    [Test]
    public async Task AddFriendshipAsync_PersistsRowRetrievableViaGetFriendshipsForUserAsync()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var createdAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        var friendship = await _repository.AddFriendshipAsync(userA, userB, createdAt);

        Assert.That(friendship.CreatedAt, Is.EqualTo(createdAt));
        var forUserA = await _repository.GetFriendshipsForUserAsync(userA);
        var forUserB = await _repository.GetFriendshipsForUserAsync(userB);
        Assert.That(forUserA.Select(f => f.Id), Is.EquivalentTo(new[] { friendship.Id }));
        Assert.That(forUserB.Select(f => f.Id), Is.EquivalentTo(new[] { friendship.Id }));
    }

    // Friendship's own doc comment: the lower Guid value is always stored
    // as UserAId, so the (UserAId, UserBId) unique index actually prevents
    // a duplicate pair inserted in the opposite order.
    [Test]
    public async Task AddFriendshipAsync_NormalizesOrder_LowerGuidIsAlwaysStoredAsUserAId()
    {
        var lower = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higher = Guid.Parse("00000000-0000-0000-0000-000000000002");

        var friendshipCallingWithHigherFirst = await _repository.AddFriendshipAsync(higher, lower, DateTime.UtcNow);

        Assert.That(friendshipCallingWithHigherFirst.UserAId, Is.EqualTo(lower));
        Assert.That(friendshipCallingWithHigherFirst.UserBId, Is.EqualTo(higher));
    }

    [Test]
    public async Task AreFriendsAsync_ReturnsTrue_RegardlessOfArgumentOrder()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await _repository.AddFriendshipAsync(userA, userB, DateTime.UtcNow);

        Assert.That(await _repository.AreFriendsAsync(userA, userB), Is.True);
        Assert.That(await _repository.AreFriendsAsync(userB, userA), Is.True, "order of arguments must not matter");
    }

    [Test]
    public async Task AreFriendsAsync_NoFriendshipRow_ReturnsFalse()
    {
        var result = await _repository.AreFriendsAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task GetFriendshipsForUserAsync_NoFriendships_ReturnsEmpty()
    {
        var result = await _repository.GetFriendshipsForUserAsync(Guid.NewGuid());

        Assert.That(result, Is.Empty);
    }
}

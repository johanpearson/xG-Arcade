using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class FriendRepository(XGArcadeDbContext dbContext) : IFriendRepository
{
    public async Task<FriendRequest> AddFriendRequestAsync(FriendRequest friendRequest, CancellationToken cancellationToken = default)
    {
        dbContext.FriendRequests.Add(friendRequest);
        await dbContext.SaveChangesAsync(cancellationToken);
        return friendRequest;
    }

    public async Task<FriendRequest?> GetFriendRequestByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.FriendRequests.AsNoTracking().FirstOrDefaultAsync(fr => fr.Id == id, cancellationToken);

    public async Task<IReadOnlyList<FriendRequest>> GetPendingFriendRequestsForUserAsync(
        Guid recipientUserId, CancellationToken cancellationToken = default) =>
        await dbContext.FriendRequests
            .AsNoTracking()
            .Where(fr => fr.RecipientUserId == recipientUserId && fr.Status == FriendRequestStatus.Pending)
            .ToListAsync(cancellationToken);

    public async Task UpdateFriendRequestStatusAsync(
        Guid friendRequestId, FriendRequestStatus status, DateTime resolvedAt, CancellationToken cancellationToken = default)
    {
        // Load-then-save (coding-guidelines.md — never ExecuteUpdateAsync,
        // the InMemory test provider can't translate it).
        var friendRequest = await dbContext.FriendRequests
            .FirstOrDefaultAsync(fr => fr.Id == friendRequestId, cancellationToken)
            ?? throw new InvalidOperationException($"FriendRequest '{friendRequestId}' not found.");

        friendRequest.Status = status;
        friendRequest.ResolvedAt = resolvedAt;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<Friendship> AddFriendshipAsync(
        Guid userId1, Guid userId2, DateTime createdAt, CancellationToken cancellationToken = default)
    {
        // Order-normalization invariant (Friendship's own doc comment): the
        // lower Guid value is always stored as UserAId, so the
        // (UserAId, UserBId) unique index actually prevents a duplicate
        // pair inserted in the opposite order.
        var (userAId, userBId) = userId1.CompareTo(userId2) <= 0
            ? (userId1, userId2)
            : (userId2, userId1);

        var friendship = new Friendship
        {
            Id = Guid.NewGuid(),
            UserAId = userAId,
            UserBId = userBId,
            CreatedAt = createdAt,
        };

        dbContext.Friendships.Add(friendship);
        await dbContext.SaveChangesAsync(cancellationToken);
        return friendship;
    }

    public async Task<bool> AreFriendsAsync(Guid userId1, Guid userId2, CancellationToken cancellationToken = default)
    {
        var (userAId, userBId) = userId1.CompareTo(userId2) <= 0
            ? (userId1, userId2)
            : (userId2, userId1);

        return await dbContext.Friendships
            .AsNoTracking()
            .AnyAsync(f => f.UserAId == userAId && f.UserBId == userBId, cancellationToken);
    }

    public async Task<IReadOnlyList<Friendship>> GetFriendshipsForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await dbContext.Friendships
            .AsNoTracking()
            .Where(f => f.UserAId == userId || f.UserBId == userId)
            .ToListAsync(cancellationToken);
}

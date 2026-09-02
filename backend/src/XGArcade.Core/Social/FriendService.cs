using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Social;

// COMP-16 (Core.Social)/ADR-0103, S-209: REQ-1401's send/accept/decline
// implementation. Depends on IUserRepository (COMP-01) the same way
// LeaderboardService (Core.Leagues) already does — both are Core-level
// components resolving User rows for their own read/validation needs, not
// a game module reaching into another game module (ADR-0003 doesn't apply
// between two Core components).
public class FriendService(
    IFriendRepository friendRepository,
    IUserRepository userRepository,
    TimeProvider timeProvider) : IFriendService
{
    public async Task<SendFriendRequestResult> SendFriendRequestAsync(
        Guid requesterUserId, Guid recipientUserId, CancellationToken cancellationToken = default)
    {
        // REQ-1401: "a user attempts to send a friend request to
        // themselves" — rejected before any repository lookup.
        if (requesterUserId == recipientUserId)
            return new SendFriendRequestResult(SendFriendRequestOutcome.SelfRequest, null);

        var recipient = await userRepository.GetByIdAsync(recipientUserId, cancellationToken);
        if (recipient is null)
            return new SendFriendRequestResult(SendFriendRequestOutcome.RecipientNotFound, null);

        if (await friendRepository.AreFriendsAsync(requesterUserId, recipientUserId, cancellationToken))
            return new SendFriendRequestResult(SendFriendRequestOutcome.AlreadyFriends, null);

        // REQ-1401: "at most one pending request may exist between any two
        // users at a time" — checked in both directions. IFriendRepository
        // has no dedicated "pending request between these two users"
        // query; two GetPendingFriendRequestsForUserAsync reads (one per
        // direction) reuse what S-208 already exposed rather than widening
        // the repository for this one caller.
        var pendingForRecipient = await friendRepository.GetPendingFriendRequestsForUserAsync(recipientUserId, cancellationToken);
        if (pendingForRecipient.Any(fr => fr.RequesterUserId == requesterUserId))
            return new SendFriendRequestResult(SendFriendRequestOutcome.DuplicatePending, null);

        var pendingForRequester = await friendRepository.GetPendingFriendRequestsForUserAsync(requesterUserId, cancellationToken);
        if (pendingForRequester.Any(fr => fr.RequesterUserId == recipientUserId))
            return new SendFriendRequestResult(SendFriendRequestOutcome.DuplicatePending, null);

        var friendRequest = new FriendRequest
        {
            Id = Guid.NewGuid(),
            RequesterUserId = requesterUserId,
            RecipientUserId = recipientUserId,
            Status = FriendRequestStatus.Pending,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        };

        var created = await friendRepository.AddFriendRequestAsync(friendRequest, cancellationToken);
        return new SendFriendRequestResult(SendFriendRequestOutcome.Sent, created);
    }

    public Task<ResolveFriendRequestResult> AcceptFriendRequestAsync(
        Guid friendRequestId, Guid respondingUserId, CancellationToken cancellationToken = default) =>
        ResolveFriendRequestAsync(friendRequestId, respondingUserId, accept: true, cancellationToken);

    public Task<ResolveFriendRequestResult> DeclineFriendRequestAsync(
        Guid friendRequestId, Guid respondingUserId, CancellationToken cancellationToken = default) =>
        ResolveFriendRequestAsync(friendRequestId, respondingUserId, accept: false, cancellationToken);

    public Task<IReadOnlyList<FriendRequest>> GetPendingFriendRequestsAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        friendRepository.GetPendingFriendRequestsForUserAsync(userId, cancellationToken);

    public Task<IReadOnlyList<Friendship>> GetFriendshipsAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        friendRepository.GetFriendshipsForUserAsync(userId, cancellationToken);

    // REQ-1401's accept/decline share every check except which final
    // Status is written and whether a Friendship row is also created —
    // factored into one method (docs/coding-guidelines.md's rule-of-three
    // budget) rather than two near-identical copies.
    private async Task<ResolveFriendRequestResult> ResolveFriendRequestAsync(
        Guid friendRequestId, Guid respondingUserId, bool accept, CancellationToken cancellationToken)
    {
        var friendRequest = await friendRepository.GetFriendRequestByIdAsync(friendRequestId, cancellationToken);
        if (friendRequest is null)
            return new ResolveFriendRequestResult(ResolveFriendRequestOutcome.NotFound, null);

        // Only the recipient may accept/decline — never the requester, who
        // could otherwise auto-resolve their own outgoing request.
        if (friendRequest.RecipientUserId != respondingUserId)
            return new ResolveFriendRequestResult(ResolveFriendRequestOutcome.NotYourRequest, null);

        if (friendRequest.Status != FriendRequestStatus.Pending)
            return new ResolveFriendRequestResult(ResolveFriendRequestOutcome.AlreadyResolved, null);

        var resolvedAt = timeProvider.GetUtcNow().UtcDateTime;
        var status = accept ? FriendRequestStatus.Accepted : FriendRequestStatus.Declined;

        await friendRepository.UpdateFriendRequestStatusAsync(friendRequestId, status, resolvedAt, cancellationToken);

        if (accept)
        {
            // REQ-1401: "User A and User B become friends — a symmetric
            // relationship" — AddFriendshipAsync normalizes UserAId/UserBId
            // order itself (Friendship's own doc comment), so it's never
            // built directly here.
            await friendRepository.AddFriendshipAsync(
                friendRequest.RequesterUserId, friendRequest.RecipientUserId, resolvedAt, cancellationToken);
        }

        // friendRequest was loaded AsNoTracking (FriendRepository.GetFriendRequestByIdAsync)
        // — updating it in place here only shapes the return value for the
        // caller; the actual persisted write already happened above via
        // UpdateFriendRequestStatusAsync.
        friendRequest.Status = status;
        friendRequest.ResolvedAt = resolvedAt;

        return new ResolveFriendRequestResult(ResolveFriendRequestOutcome.Resolved, friendRequest);
    }
}

using XGArcade.Data.Entities;

namespace XGArcade.Core.Social;

// COMP-16 (Core.Social)/ADR-0103, S-209: REQ-1401's send/accept/decline
// business logic, layered on top of IFriendRepository's pure persistence
// primitives (S-208) — duplicate-pending rejection (checked in both
// directions), self-request rejection, and already-friends rejection all
// live here, never in the repository. Same "outcome enum + result record
// for expected branches, exceptions reserved for the truly unexpected"
// shape as ILeagueService's JoinLeagueOutcome/JoinLeagueResult.
public interface IFriendService
{
    // REQ-1401: creates a Pending FriendRequest from requesterUserId to
    // recipientUserId. Rejects (without ever calling
    // IFriendRepository.AddFriendRequestAsync) when: the two ids are the
    // same (SelfRequest), recipientUserId doesn't resolve to a real User
    // (RecipientNotFound — also what a broken FriendRequest.RecipientUserId
    // FK would otherwise surface as an unhandled DB exception), the two
    // users are already friends (AlreadyFriends), or a Pending request
    // already exists between them in either direction (DuplicatePending).
    Task<SendFriendRequestResult> SendFriendRequestAsync(
        Guid requesterUserId, Guid recipientUserId, CancellationToken cancellationToken = default);

    // REQ-1401: resolves a Pending FriendRequest as Accepted and creates the
    // symmetric Friendship row in the same call — "the pending request is
    // resolved, and it no longer appears in either player's pending list"
    // is never a separate step a caller could skip. Only the request's
    // RecipientUserId may accept it (respondingUserId is checked against
    // that, not RequesterUserId — REQ-1401 doesn't say a requester can
    // accept their own outgoing request, and allowing that would let a
    // request auto-resolve itself).
    Task<ResolveFriendRequestResult> AcceptFriendRequestAsync(
        Guid friendRequestId, Guid respondingUserId, CancellationToken cancellationToken = default);

    // REQ-1401: resolves a Pending FriendRequest as Declined — no Friendship
    // row is ever created, and (since only the request row's Status
    // changes, not any block/cooldown state) the same requester is free to
    // send a new request later, satisfying "declining is not a permanent
    // block" without any extra bookkeeping.
    Task<ResolveFriendRequestResult> DeclineFriendRequestAsync(
        Guid friendRequestId, Guid respondingUserId, CancellationToken cancellationToken = default);

    // REQ-1401: every Pending request where userId is the recipient — lets
    // a player see who's asked to friend them, and is what "no longer
    // appears in either player's pending list" (post accept/decline) is
    // verified against.
    Task<IReadOnlyList<FriendRequest>> GetPendingFriendRequestsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    // REQ-1401's accepted outcome: every Friendship row involving userId,
    // regardless of which side (UserAId/UserBId) they ended up normalized
    // into.
    Task<IReadOnlyList<Friendship>> GetFriendshipsAsync(
        Guid userId, CancellationToken cancellationToken = default);
}

public enum SendFriendRequestOutcome
{
    Sent,
    SelfRequest,
    RecipientNotFound,
    AlreadyFriends,
    DuplicatePending,
}

// FriendRequest is non-null only when Outcome is Sent.
public record SendFriendRequestResult(SendFriendRequestOutcome Outcome, FriendRequest? FriendRequest);

public enum ResolveFriendRequestOutcome
{
    Resolved,
    NotFound,
    NotYourRequest,
    AlreadyResolved,
}

// FriendRequest is non-null only when Outcome is Resolved.
public record ResolveFriendRequestResult(ResolveFriendRequestOutcome Outcome, FriendRequest? FriendRequest);

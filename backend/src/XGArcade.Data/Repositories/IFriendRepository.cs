using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// Core.Social's (COMP-16) own persistence for REQ-1401 — the only path
// Core.Social reaches FriendRequest/Friendship through, same repository-
// per-component pattern as IPredictInstanceRepository (COMP-15). Owns both
// entities together (one workflow's two states, same "one repository owns
// a template+instance+child family" precedent IPredictInstanceRepository
// already sets). See ADR-0103.
//
// S-208 (this story) scaffolds pure persistence primitives only — no
// duplicate-pending-request validation, no self-request rejection, no
// already-friends check. Those are S-209's business logic, layered on top
// of these methods by a future service class, not this repository.
public interface IFriendRepository
{
    Task<FriendRequest> AddFriendRequestAsync(FriendRequest friendRequest, CancellationToken cancellationToken = default);

    Task<FriendRequest?> GetFriendRequestByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // Plain status-filtered list — every FriendRequest currently Pending
    // for this recipient. Not a duplicate-check; S-209's own service layer
    // is responsible for deciding what, if anything, to do with the result.
    Task<IReadOnlyList<FriendRequest>> GetPendingFriendRequestsForUserAsync(
        Guid recipientUserId, CancellationToken cancellationToken = default);

    // Load-then-save (coding-guidelines.md — never ExecuteUpdateAsync, the
    // InMemory test provider can't translate it). resolvedAt is supplied by
    // the caller, mirroring PredictInstanceRepository.LockPlayerPredictionsAsync's
    // own "caller computes `now`, repository just persists it" convention.
    Task UpdateFriendRequestStatusAsync(
        Guid friendRequestId, FriendRequestStatus status, DateTime resolvedAt, CancellationToken cancellationToken = default);

    // REQ-1401's accepted outcome. Normalizes UserAId/UserBId order (lower
    // Guid value as UserAId) before inserting — see Friendship's own doc
    // comment for why this repository-level invariant is what makes the
    // (UserAId, UserBId) unique index actually prevent a duplicate pair.
    Task<Friendship> AddFriendshipAsync(
        Guid userId1, Guid userId2, DateTime createdAt, CancellationToken cancellationToken = default);

    Task<bool> AreFriendsAsync(Guid userId1, Guid userId2, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Friendship>> GetFriendshipsForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}

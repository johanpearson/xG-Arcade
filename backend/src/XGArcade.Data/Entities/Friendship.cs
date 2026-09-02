namespace XGArcade.Data.Entities;

// Core.Social (COMP-16) entity — REQ-1401's accepted outcome: a symmetric
// relationship between two users, one row per pair (never two rows for the
// same pair in either order). See ADR-0103 for the component-boundary
// reasoning (Core.Social is arcade-level, reusable by a future game, not
// folded into Games.XGConnect).
//
// UserAId/UserBId both get a real FK to User, cascade — same "pure
// relationship row, nothing else depends on it surviving" precedent as
// FriendRequest above/LeagueMembership.UserId. Postgres allows two separate
// cascade FKs to the same table on one row (unlike SQL Server's
// multi-cascade-path restriction), so no special handling is needed here.
//
// Order-normalization invariant (repository-level, not a business rule):
// IFriendRepository.AddFriendshipAsync must always store the pair with the
// lower Guid value as UserAId (Guid implements IComparable<Guid>) so the
// (UserAId, UserBId) unique index below actually prevents a duplicate pair
// being inserted in the opposite order — e.g. (A, B) and (B, A) would
// otherwise both satisfy the index and silently duplicate the relationship.
// This is deliberately a repository-level normalization only; S-209's own
// accept-request workflow (not built by this story) is what decides *when*
// to call AddFriendshipAsync at all.
public class Friendship
{
    public Guid Id { get; set; }
    public required Guid UserAId { get; set; }
    public required Guid UserBId { get; set; }
    public required DateTime CreatedAt { get; set; }
}

namespace XGArcade.Data.Entities;

// Core.Social (COMP-16) entity — REQ-1401: one player asking another to
// become friends. See ADR-0103 for why Core.Social is a separate,
// arcade-level component (alongside Core.Users/Core.Leagues) rather than
// folded into Games.XGConnect (COMP-17) — a friends list is a platform
// concept a future game could reuse, not xG-Connect-specific.
//
// RequesterUserId/RecipientUserId both get a real FK to User, cascade —
// this is a pure relationship/flag row with no other user's derived data
// depending on it surviving, same precedent as LeagueMembership.UserId
// (XGArcadeDbContext.OnModelCreating), not Guess.UserId's anonymize-in-place
// shape.
//
// Status starts and stays Pending until S-209's accept/decline workflow
// (not built by this story — S-208 is schema + CRUD only) moves it to
// Accepted/Declined; ResolvedAt is set at the same moment, mirroring
// PlayerSuggestion's own "who and when, on the row itself" resolved-state
// shape.
//
// This story only scaffolds the table/repository CRUD — duplicate-pending
// rejection, self-request rejection, and the accept -> Friendship-row write
// are S-209's business logic, not this one's.
public class FriendRequest
{
    public Guid Id { get; set; }
    public required Guid RequesterUserId { get; set; }
    public required Guid RecipientUserId { get; set; }
    public FriendRequestStatus Status { get; set; } = FriendRequestStatus.Pending;
    public required DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

// Modeled as a plain enum (no HasConversion) — same convention
// PlayerSuggestionStatus/AvatarSubmissionStatus already establish.
public enum FriendRequestStatus
{
    Pending,
    Accepted,
    Declined,
}

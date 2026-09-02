namespace XGArcade.Data.Entities;

// Core.Social (COMP-16) entity — REQ-1402: one player directly challenging
// an existing friend to an xG Connect match. Requires an existing
// Friendship (REQ-1401) — enforced by S-210's service logic, not this
// story's schema/CRUD scaffolding.
//
// ChallengerUserId/ChallengedUserId both get a real FK to User, cascade —
// same "pure relationship/flag row" precedent as FriendRequest above.
//
// Status starts Pending; S-210's accept/decline workflow
// (Core.Social.ChallengeService, driven by XGArcade.Api's
// ChallengeEndpoints) moves it to Accepted/Declined; ResolvedAt mirrors
// FriendRequest's own shape.
//
// ResultingMatchId is a plain, opaque Guid? column with NO EF
// HasForeignKey/navigation to ConnectMatch (Games.XGConnect, COMP-17) — this
// mirrors Round.GameInstanceId's own deliberate FK omission (ADR-0003)
// exactly: Core.Social referencing a Games.XGConnect-owned row's id is the
// identical cross-component shape, and ADR-0103 explicitly requires "never
// a direct project reference" from Core.Social into Games.XGConnect
// internals. Set by ChallengeService once a challenge acceptance resolves
// into a real ConnectMatch (S-210).
public class Challenge
{
    public Guid Id { get; set; }
    public required Guid ChallengerUserId { get; set; }
    public required Guid ChallengedUserId { get; set; }
    public ChallengeStatus Status { get; set; } = ChallengeStatus.Pending;
    public required DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    // See this entity's own doc comment above — deliberately opaque,
    // no FK into Games.XGConnect's ConnectMatch table (ADR-0003/ADR-0103).
    public Guid? ResultingMatchId { get; set; }
}

public enum ChallengeStatus
{
    Pending,
    Accepted,
    Declined,
}

namespace XGArcade.Data.Entities;

// REQ-722/ADR-0087 (S-180): a player's profile-avatar upload — mirrors
// PlayerSuggestion's submit/review/decide shape (see that entity's own doc
// comment for the general pattern this follows, including "no FK to Users"
// below).
public class AvatarSubmission
{
    public Guid Id { get; set; }

    public required Guid SubmittingUserId { get; set; }

    // Deliberately no FK constraint to Users — same reasoning as
    // PlayerSuggestion.SubmittingUserId/Guess.UserId: User rows are
    // hard-deleted on account deletion (UserRepository.DeleteAsync/
    // REQ-710), and this story doesn't define anonymize-on-delete semantics
    // for AvatarSubmission the way Guess.UserId already has one (REQ-710's
    // null-out-don't-delete rule) — leaving this unconstrained avoids
    // silently blocking account deletion behind a submission row, same "no
    // FK" choice PlayerSuggestion.SubmittingUserId already makes for the
    // identical reason.

    // The Supabase Storage object path/key the uploaded image was stored
    // under (IAvatarStorage.UploadAsync's return value) — never the raw
    // image bytes themselves, and never a full URL (ADR-0087). Resolving
    // this into something servable is IAvatarStorage's job, not a concern
    // of this entity or this table.
    public required string ImageStorageKey { get; set; }

    // Modeled as a plain enum (no HasConversion — no existing precedent in
    // this codebase for storing an enum as a string, see PlayerData.
    // Confidence's plain-string convention for contrast), same convention
    // PlayerSuggestionStatus already establishes. Only Pending is ever
    // written by this story (S-180) — REQ-517/S-181's admin
    // approve/reject action is the only path that ever moves a row to
    // Approved/Rejected.
    public AvatarSubmissionStatus Status { get; set; } = AvatarSubmissionStatus.Pending;

    public DateTime CreatedAt { get; set; }

    // Set exactly once, by the admin resolve action
    // (REQ-517/S-181 — IAvatarSubmissionRepository.ApproveAsync/
    // RejectAsync), at the same moment Status moves off Pending — mirrors
    // PlayerSuggestion.ResolvedByAdminId/ResolvedAt's own "who and when, on
    // the row itself" shape (see that entity's own doc comment for the
    // fuller rationale). Both null until then.
    public Guid? ResolvedByAdminId { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public enum AvatarSubmissionStatus
{
    Pending,
    Approved,
    Rejected,
}

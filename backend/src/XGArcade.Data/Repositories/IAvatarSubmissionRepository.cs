using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// REQ-722/ADR-0087 (S-180): AvatarSubmission's own repository, deliberately
// separate from IPlayerSuggestionRepository/every COMP-06 repository —
// this table has nothing to do with player/category data, it's a
// COMP-01-adjacent (Core.Users) player-profile concern.
//
// S-180 only ever calls CreateOrReplacePendingAsync — REQ-517/S-181's
// admin approve/reject action (not this story) will need its own
// resolve-style write, following the same "who and when, on the row
// itself" shape PlayerSuggestionRepository.ResolveAsync already
// establishes, added when that story is built rather than pre-built here.
public interface IAvatarSubmissionRepository
{
    // REQ-722: the caller's own current Pending row, if any. Not read by
    // POST /users/me/avatar itself (CreateOrReplacePendingAsync below does
    // its own lookup) — exposed for a future "see your own avatar status"
    // read (REQ-722's "Seeing your own status" criterion) and S-181's
    // admin queue view, neither built by this story.
    Task<AvatarSubmission?> GetPendingAsync(Guid submittingUserId, CancellationToken cancellationToken = default);

    // REQ-722: the caller's own current Approved row, if any — "a prior
    // Approved avatar stays visible to other players until the new
    // submission is itself approved" (REQ-722's "Replacing an approved
    // avatar" criterion). Not read by POST /users/me/avatar itself (this
    // endpoint never touches an Approved row at all), but needed by
    // S-181's future approve action (to know which prior Approved row, if
    // any, a fresh approval supersedes) and by a future "see your own
    // avatar status" read — added here now so neither of those stories
    // needs to add this lookup itself.
    Task<AvatarSubmission?> GetApprovedAsync(Guid submittingUserId, CancellationToken cancellationToken = default);

    // REQ-722: creates a brand-new Pending row for submittingUserId,
    // replacing (deleting) any existing Pending row for that same player
    // first — "never two pending submissions queued for the same player at
    // once." Returns the created row plus the storage key of whichever
    // Pending row was just replaced (null if there wasn't one), so the
    // caller (AvatarEndpoints) can best-effort delete the now-orphaned
    // image from IAvatarStorage. Never reads or writes an Approved row
    // (REQ-722's own "uploading never blanks a player's visible avatar"
    // rule) — this method only ever touches a Pending row.
    // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
    // ExecuteDeleteAsync.
    Task<AvatarSubmissionCreationResult> CreateOrReplacePendingAsync(
        Guid submittingUserId, string imageStorageKey, DateTime createdAt, CancellationToken cancellationToken = default);
}

public record AvatarSubmissionCreationResult(AvatarSubmission Submission, string? ReplacedImageStorageKey);

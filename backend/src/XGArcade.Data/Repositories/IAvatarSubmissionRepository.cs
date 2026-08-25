using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// REQ-722/ADR-0087 (S-180): AvatarSubmission's own repository, deliberately
// separate from IPlayerSuggestionRepository/every COMP-06 repository —
// this table has nothing to do with player/category data, it's a
// COMP-01-adjacent (Core.Users) player-profile concern.
//
// REQ-517/S-181: GetByIdAsync/GetAllPendingAsync/ApproveAsync/RejectAsync
// below are that admin review/resolve half — GetAllPendingAsync mirrors
// IPlayerSuggestionRepository.GetPendingAsync()'s no-id/oldest-first shape
// (deliberately a different name than the existing single-row
// GetPendingAsync(Guid) above, which means something else: "this one
// player's own pending row"), and ApproveAsync/RejectAsync mirror
// PlayerSuggestionRepository.ResolveAsync's race-safe
// re-check-status-inside-the-write shape.
public interface IAvatarSubmissionRepository
{
    // REQ-722: the caller's own current Pending row, if any. Not read by
    // POST /users/me/avatar itself (CreateOrReplacePendingAsync below does
    // its own lookup) — exposed for a future "see your own avatar status"
    // read (REQ-722's "Seeing your own status" criterion) and S-181's
    // admin queue view, neither built by this story.
    Task<AvatarSubmission?> GetPendingAsync(Guid submittingUserId, CancellationToken cancellationToken = default);

    // REQ-722: the current Approved row for the given submittingUserId, if
    // any — "a prior Approved avatar stays visible to other players until
    // the new submission is itself approved" (REQ-722's "Replacing an
    // approved avatar" criterion). Not read by POST /users/me/avatar itself
    // (this endpoint never touches an Approved row at all), but needed by
    // S-181's future approve action (to know which prior Approved row, if
    // any, a fresh approval supersedes), by GET /users/me/avatar's "see
    // your own avatar status" read, and — as of REQ-722/S-184 — by GET
    // /users/{userId}/avatar/image (AvatarEndpoints.cs), which calls this
    // with an arbitrary TARGET userId, not just the caller's own. Always
    // generic on submittingUserId; "caller's own" was never enforced by
    // this method itself, only by which id a given call site happens to
    // pass in.
    Task<AvatarSubmission?> GetApprovedAsync(Guid submittingUserId, CancellationToken cancellationToken = default);

    // REQ-722 (S-182): the caller's own most-recently-created Rejected row,
    // if any. Rejected rows are never deleted (REQ-517/S-181's future
    // reject action, when built, is expected to just flip Status the same
    // way it will for Approved — nothing in this codebase today removes a
    // Rejected row), so a player can accumulate more than one over time;
    // "most recent" is CreatedAt descending, since that's the one relevant
    // to "see your own current status" — an old rejection from months ago
    // shouldn't resurface over a more recent one. Deliberately independent
    // of GetPendingAsync/GetApprovedAsync above: a Rejected row can coexist
    // with a separate, older Approved row (REQ-722's "Seeing your own
    // status" criterion — rejection never hides a still-valid earlier
    // approval), so this never filters by "no Approved row exists" or
    // similar — GET /users/me/avatar (AvatarEndpoints, S-182) calls all
    // three lookups unconditionally and lets the response carry whichever
    // combination is actually true.
    Task<AvatarSubmission?> GetLatestRejectedAsync(Guid submittingUserId, CancellationToken cancellationToken = default);

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

    // REQ-517/REQ-722 (S-181/S-182, same method, two independent callers):
    // a single submission by id, any status. AdminAvatarEndpoints' approve/
    // reject handlers (S-181) use this for their own initial 404 check,
    // same role PlayerSuggestionRepository.GetByIdAsync plays for
    // AdminSuggestionEndpoints. GET /users/me/avatar/{id}/image
    // (AvatarEndpoints, S-182) uses the same lookup and then checks
    // SubmittingUserId == caller itself (never pushed into the query here)
    // — "fetch by id, let the caller enforce whatever authorization is
    // appropriate for it" is deliberately shared, since the two callers'
    // authorization rules differ (admin policy vs. owner-only) and neither
    // belongs baked into this repository method. AsNoTracking (read-only)
    // — ApproveAsync/RejectAsync below do their own separate, tracked load
    // for the actual write.
    Task<AvatarSubmission?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // REQ-517: every Pending submission, oldest first — the admin
    // moderation queue's own listing, matching REQ-509's existing
    // pending-suggestion ordering convention
    // (IPlayerSuggestionRepository.GetPendingAsync()'s .OrderBy(s =>
    // s.CreatedAt)). Deliberately a different name than GetPendingAsync
    // above, which returns at most one row for one specific player — this
    // returns every player's pending row.
    Task<IReadOnlyList<AvatarSubmission>> GetAllPendingAsync(CancellationToken cancellationToken = default);

    // REQ-517: approves a Pending submission — sets Status=Approved and
    // ResolvedByAdminId/ResolvedAt, and supersedes ("a player has at most
    // one visible avatar at a time") any prior Approved row for the same
    // SubmittingUserId by deleting it in the same SaveChangesAsync, same
    // "replace, don't invent a new status" precedent
    // CreateOrReplacePendingAsync already sets for the analogous
    // pending-replacement case. Returns null when id doesn't exist OR the
    // row isn't Pending any more (race-safe re-check inside the same load,
    // same shape as PlayerSuggestionRepository.ResolveAsync's bool return)
    // — the caller (AdminAvatarEndpoints) reports either case as a 409
    // after its own separate 404 pre-check via GetByIdAsync above.
    Task<AvatarSubmissionApprovalResult?> ApproveAsync(
        Guid id, Guid adminId, DateTime resolvedAt, CancellationToken cancellationToken = default);

    // REQ-517: rejects a Pending submission — sets Status=Rejected and
    // ResolvedByAdminId/ResolvedAt. Deliberately never reads or touches any
    // prior Approved row for this player ("the player's previously-approved
    // avatar if any is unchanged") — unlike ApproveAsync above, there is no
    // supersede step here at all. Same race-safe re-check/false-on-race
    // shape as PlayerSuggestionRepository.ResolveAsync.
    Task<bool> RejectAsync(Guid id, Guid adminId, DateTime resolvedAt, CancellationToken cancellationToken = default);
}

public record AvatarSubmissionCreationResult(AvatarSubmission Submission, string? ReplacedImageStorageKey);

// SupersededImageStorageKey: the ImageStorageKey of the prior Approved row
// for this same player that this approval just replaced, if any — null
// when the player had no prior Approved avatar. The caller
// (AdminAvatarEndpoints) uses this to best-effort delete the now-orphaned
// image from IAvatarStorage, same "log a warning on failure, don't fail
// the request" pattern AvatarEndpoints.cs's upload handler already uses
// for its own replaced-pending-image delete.
public record AvatarSubmissionApprovalResult(AvatarSubmission Submission, string? SupersededImageStorageKey);

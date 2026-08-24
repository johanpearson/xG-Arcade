using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Storage;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Admin;

// REQ-517 (S-181): admin review of pending avatar uploads (REQ-722/S-180) —
// a new file, deliberately separate from AdminSuggestionEndpoints.cs, same
// "submission file vs. admin file" split EndpointMapping.cs's own comments
// already document for MapSuggestionEndpoints/MapAdminSuggestionEndpoints
// and MapAdminIncidentReportEndpoints. Closely mirrors
// AdminSuggestionEndpoints.cs's list-pending / act-on-one-by-id /
// terminal-state-409 shape (REQ-509) — see that file's own doc comments
// for the fuller reasoning behind each pattern reused here.
//
// Every write here goes through IAvatarSubmissionRepository's own
// ApproveAsync/RejectAsync (race-safe: both re-check Status==Pending
// inside the same tracked load, mirroring
// IPlayerSuggestionRepository.ResolveAsync) — never a direct DbContext
// query from this file, same repository-encapsulation rule every other
// admin endpoint file in this codebase already follows.
public static class AdminAvatarEndpoints
{
    public static void MapAdminAvatarEndpoints(this WebApplication app)
    {
        // REQ-517: "every pending submission with a preview of the
        // uploaded image, the submitting player's DisplayName, and the
        // submission time — oldest first." Resolves every row's submitting
        // user's display name in one batched query
        // (IUserRepository.GetByIdsAsync), same "no N+1 loop" discipline
        // AdminSuggestionEndpoints.cs's own GET /admin/suggestions already
        // establishes. SubmittingUserId has no FK (AvatarSubmission's own
        // doc comment) — a user hard-deleted since submission (REQ-710)
        // simply resolves to a null display name, not an error.
        //
        // The per-row IAvatarStorage.GetPreviewUrlAsync call below is NOT
        // an N+1 database query — it's a per-image signed-URL HTTP call to
        // the storage provider, inherent to how a single object gets
        // signed (Supabase Storage has no bulk-object-sign endpoint this
        // codebase uses), sequential rather than parallelized since this
        // queue is expected to stay small and nothing here holds the
        // scoped DbContext across the calls.
        app.MapGet("/admin/avatar-submissions", async (
            IAvatarSubmissionRepository avatarSubmissionRepository,
            IUserRepository userRepository,
            IAvatarStorage avatarStorage,
            CancellationToken cancellationToken) =>
        {
            var pending = await avatarSubmissionRepository.GetAllPendingAsync(cancellationToken);

            var userIds = pending.Select(s => s.SubmittingUserId).Distinct().ToList();
            var users = await userRepository.GetByIdsAsync(userIds, cancellationToken);
            var displayNameByUserId = users.ToDictionary(u => u.Id, u => u.DisplayName);

            var responses = new List<PendingAvatarSubmissionResponse>(pending.Count);
            foreach (var submission in pending)
            {
                var previewUrl = await avatarStorage.GetPreviewUrlAsync(submission.ImageStorageKey, cancellationToken);
                responses.Add(new PendingAvatarSubmissionResponse(
                    submission.Id,
                    previewUrl,
                    submission.SubmittingUserId,
                    displayNameByUserId.GetValueOrDefault(submission.SubmittingUserId),
                    submission.CreatedAt));
            }

            return Results.Ok(responses);
        }).RequireAuthorization("Admin");

        // REQ-517: "that submission becomes the player's visible avatar...
        // and if the same player already had a previously-approved avatar,
        // the new one replaces it — a player has at most one visible
        // avatar at a time." No request body, same "no reason field"
        // precedent REQ-509/510's own commit/reject actions already set.
        app.MapPost("/admin/avatar-submissions/{id:guid}/approve", async (
            Guid id,
            ClaimsPrincipal principal,
            IAvatarSubmissionRepository avatarSubmissionRepository,
            IAvatarStorage avatarStorage,
            ILogger<AdminAvatarEndpointsLogCategory> logger,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var existing = await avatarSubmissionRepository.GetByIdAsync(id, cancellationToken);
            if (existing is null)
                return Results.NotFound();

            if (existing.Status != AvatarSubmissionStatus.Pending)
                return AlreadyResolvedProblem();

            // Policy above already required a valid "sub" claim to reach here.
            var adminId = principal.GetAuthProviderUserId()!.Value;
            var resolvedAt = timeProvider.GetUtcNow().UtcDateTime;

            var result = await avatarSubmissionRepository.ApproveAsync(id, adminId, resolvedAt, cancellationToken);
            if (result is null)
            {
                // Race window between the Pending check above and this call
                // (another admin resolved it first) — same "already
                // resolved" outcome as the check above, reported the same way.
                return AlreadyResolvedProblem();
            }

            // REQ-517: best-effort delete of the now-superseded prior
            // Approved image, if any — same "log a warning on failure,
            // don't fail the request" pattern AvatarEndpoints.cs's upload
            // handler already uses for its own replaced-pending-image
            // delete. The new approval already succeeded by this point; a
            // leftover unreferenced object in the bucket is a
            // storage-hygiene concern, not a reason to fail this request.
            if (result.SupersededImageStorageKey is not null)
            {
                try
                {
                    await avatarStorage.DeleteAsync(result.SupersededImageStorageKey, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to delete superseded avatar image {StorageKey} for submission {SubmissionId}.",
                        result.SupersededImageStorageKey,
                        id);
                }
            }

            return Results.NoContent();
        }).RequireAuthorization("Admin");

        // REQ-517: "the submission's status becomes Rejected... no image
        // becomes visible to anyone but the submitting player... and the
        // player's previously-approved avatar if any is unchanged." No
        // reason/comment field — explicitly out of scope for v1. No
        // request body, same shape as AdminSuggestionEndpoints.cs's own
        // /reject.
        app.MapPost("/admin/avatar-submissions/{id:guid}/reject", async (
            Guid id,
            ClaimsPrincipal principal,
            IAvatarSubmissionRepository avatarSubmissionRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var existing = await avatarSubmissionRepository.GetByIdAsync(id, cancellationToken);
            if (existing is null)
                return Results.NotFound();

            if (existing.Status != AvatarSubmissionStatus.Pending)
                return AlreadyResolvedProblem();

            // Policy above already required a valid "sub" claim to reach here.
            var adminId = principal.GetAuthProviderUserId()!.Value;
            var resolvedAt = timeProvider.GetUtcNow().UtcDateTime;

            var resolved = await avatarSubmissionRepository.RejectAsync(id, adminId, resolvedAt, cancellationToken);
            if (!resolved)
            {
                // Same race window as approve above.
                return AlreadyResolvedProblem();
            }

            return Results.NoContent();
        }).RequireAuthorization("Admin");
    }

    // Shared by both approve/reject above — the same "already resolved"
    // 409 shape AdminSuggestionEndpoints.cs's own commit/reject actions use,
    // whether the terminal state was reached by this action's own prior
    // check or by a race with another admin/action resolving it first.
    private static IResult AlreadyResolvedProblem() =>
        Results.Problem(
            title: "Avatar submission already resolved",
            detail: "This avatar submission has already been approved or rejected.",
            statusCode: StatusCodes.Status409Conflict);
}

// Pure log-category marker for ILogger<T> — same pattern as
// AdminSuggestionEndpointsLogCategory/AvatarEndpointsLogCategory.
internal sealed class AdminAvatarEndpointsLogCategory;

// ImagePreviewUrl is the resolved, servable signed URL
// (IAvatarStorage.GetPreviewUrlAsync) — never AvatarSubmission.
// ImageStorageKey itself, which stays entirely internal to this codebase
// (docs/coding-guidelines.md's DTO-at-the-boundary rule; this interface's
// own doc comment on IAvatarStorage.GetPreviewUrlAsync).
public record PendingAvatarSubmissionResponse(
    Guid Id,
    string ImagePreviewUrl,
    Guid SubmittingUserId,
    string? SubmittingUserDisplayName,
    DateTime CreatedAt);

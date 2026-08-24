using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Storage;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Avatars;

// REQ-722/ADR-0087 (S-180): POST /users/me/avatar — a logged-in player
// (guest or claimed account) uploads a profile avatar image, held Pending
// until an admin approves it (REQ-517, S-181 — not this file). Unlike
// REQ-215/REQ-903's non-guest-only rules (SuggestionEndpoints/
// IncidentEndpoints), REQ-722 has no guest exclusion — deliberately no
// `user.IsGuest` check here; re-verified against the REQ text before
// writing this endpoint, not assumed by analogy to those two.
//
// Mirrors XGArcade.Api.Suggestions.SuggestionEndpoints/XGArcade.Api.
// Incidents.IncidentEndpoints's minimal-API shape (ClaimsPrincipal +
// IUserRepository.GetByAuthProviderUserIdAsync to resolve the caller,
// Results.Problem for every rejection) — this file just adds a file-upload
// (IFormFile) parameter instead of a JSON body.
public static class AvatarEndpoints
{
    // REQ-722: "a reasonable size/type limit" — exact numbers left to this
    // document per requirements-document.md's own convention (mirrors
    // IncidentEndpoints.TitleMaxLength/DescriptionMaxLength). 5 MB
    // comfortably covers a phone-camera profile photo at ordinary
    // compression while staying well under Kestrel's default 30 MB request
    // body cap, so this is the limit that actually rejects an oversized
    // upload, not an unreachable one hidden behind a lower framework
    // default.
    public const long MaxImageSizeBytes = 5 * 1024 * 1024;

    // image/jpeg and image/png cover the overwhelming majority of
    // phone/browser photo uploads; image/webp is included since modern
    // mobile camera/share flows increasingly produce it by default. No
    // image/gif or image/svg+xml: an avatar is a single static photo, and
    // SVG in particular can carry executable content — an unnecessary risk
    // for a moderated-but-not-yet-reviewed upload (REQ-722's whole "pending
    // admin approval" premise assumes the file itself is inert until then).
    public static readonly IReadOnlySet<string> AllowedContentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };

    public static void MapAvatarEndpoints(this WebApplication app)
    {
        app.MapPost("/users/me/avatar", async (
            IFormFile file,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IAvatarSubmissionRepository avatarSubmissionRepository,
            IAvatarStorage avatarStorage,
            ILogger<AvatarEndpointsLogCategory> logger,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            // Free, local-only checks (no DB round trip, no storage call)
            // before anything else — same "checked before any call to
            // [the external dependency]" ordering AuthController.Signup
            // already establishes for its own validation checks.
            if (file.Length == 0)
            {
                return Results.Problem(
                    title: "An image file is required",
                    detail: "The uploaded file was empty.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (file.Length > MaxImageSizeBytes)
            {
                return Results.Problem(
                    title: "Image too large",
                    detail: $"Image must be at most {MaxImageSizeBytes / (1024 * 1024)} MB.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!AllowedContentTypes.Contains(file.ContentType))
            {
                return Results.Problem(
                    title: "Unsupported image type",
                    detail: "Image must be one of: " + string.Join(", ", AllowedContentTypes),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var user = await ResolveCurrentUserAsync(principal, userRepository, cancellationToken);
            if (user is null)
                return Results.Unauthorized();

            // REQ-722: no guest exclusion — see this file's own doc comment.

            string storageKey;
            await using (var stream = file.OpenReadStream())
            {
                storageKey = await avatarStorage.UploadAsync(stream, file.ContentType, cancellationToken);
            }

            var result = await avatarSubmissionRepository.CreateOrReplacePendingAsync(
                user.Id, storageKey, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);

            // REQ-722: "never two pending submissions queued... the prior
            // pending submission is replaced" — best-effort delete of the
            // now-orphaned image this replaced. Logged, not surfaced: the
            // new Pending submission was already created successfully by
            // the point this runs, and a leftover unreferenced object in
            // the bucket is a storage-hygiene concern, not a reason to fail
            // an otherwise-successful upload.
            if (result.ReplacedImageStorageKey is not null)
            {
                try
                {
                    await avatarStorage.DeleteAsync(result.ReplacedImageStorageKey, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to delete superseded avatar image {StorageKey} for user {UserId}.",
                        result.ReplacedImageStorageKey,
                        user.Id);
                }
            }

            return Results.Created(
                $"/users/me/avatar/{result.Submission.Id}",
                new SubmitAvatarResponse(result.Submission.Id, result.Submission.Status.ToString(), result.Submission.CreatedAt));
        }).RequireAuthorization().DisableAntiforgery();

        // REQ-722 (S-182): "Seeing your own status" — a logged-in player's
        // own Pending/Rejected/Approved rows, independently looked up (see
        // AvatarStatusResponse's own doc comment for why none of the three
        // filters on the others). ADR-0087's "Follow-up" section assigned
        // "resolve a stored key into a servable URL" to REQ-517/S-181 —
        // that's still true for the admin-queue/public-visible case, but
        // S-182 needs the owning player's own preview first; see
        // IAvatarStorage's own doc comment for the fuller reasoning on why
        // this doesn't actually build what that follow-up deferred.
        app.MapGet("/users/me/avatar", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IAvatarSubmissionRepository avatarSubmissionRepository,
            CancellationToken cancellationToken) =>
        {
            var user = await ResolveCurrentUserAsync(principal, userRepository, cancellationToken);
            if (user is null)
                return Results.Unauthorized();

            // Three independent lookups, not a single "current status"
            // query — REQ-722 is explicit that a Rejected row never hides a
            // separately-existing Approved row from an earlier submission,
            // and (symmetrically) a fresh Pending upload never hides an
            // older Approved row either. All three can be non-null for the
            // same player at once.
            var pending = await avatarSubmissionRepository.GetPendingAsync(user.Id, cancellationToken);
            var rejected = await avatarSubmissionRepository.GetLatestRejectedAsync(user.Id, cancellationToken);
            var approved = await avatarSubmissionRepository.GetApprovedAsync(user.Id, cancellationToken);

            return Results.Ok(new AvatarStatusResponse(
                ToSummary(pending),
                ToSummary(rejected),
                ToSummary(approved)));
        }).RequireAuthorization();

        // REQ-722 (S-182): streams the actual image bytes for one of the
        // caller's own submissions, referenced by AvatarStatusResponse's
        // ImageUrl above. Deliberately owner-only for now, regardless of
        // Status — REQ-722 is explicit that Pending/Rejected must never be
        // shown to anyone but the submitting player, and even an Approved
        // row (eventually visible to other players once REQ-517/S-181's
        // admin approval exists and a "visible to other players" surface is
        // built) has no such surface yet, so restricting this to the owner
        // only is strictly narrower than final behavior, never broader.
        // S-181's future admin-queue view and any future "visible to other
        // players" endpoint each need their own separate authorization
        // path — not this one — when those stories are built.
        app.MapGet("/users/me/avatar/{submissionId:guid}/image", async (
            Guid submissionId,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IAvatarSubmissionRepository avatarSubmissionRepository,
            IAvatarStorage avatarStorage,
            CancellationToken cancellationToken) =>
        {
            var user = await ResolveCurrentUserAsync(principal, userRepository, cancellationToken);
            if (user is null)
                return Results.Unauthorized();

            var submission = await avatarSubmissionRepository.GetByIdAsync(submissionId, cancellationToken);

            // A missing row and a row owned by someone else are the exact
            // same response — 404, never 403 — so this endpoint never
            // confirms to a caller that a given submissionId exists at all
            // for another player (same "don't leak existence" reasoning
            // this file's own doc comment on POST /users/me/avatar's
            // sibling checks apply elsewhere in this codebase).
            if (submission is null || submission.SubmittingUserId != user.Id)
                return Results.NotFound();

            var image = await avatarStorage.DownloadAsync(submission.ImageStorageKey, cancellationToken);
            if (image is null)
                return Results.NotFound();

            return Results.Stream(new MemoryStream(image.Content), image.ContentType);
        }).RequireAuthorization();
    }

    // Shared by all three handlers above — resolves the caller from
    // ClaimsPrincipal via IUserRepository.GetByAuthProviderUserIdAsync,
    // same lookup SuggestionEndpoints.cs/IncidentEndpoints.cs each do
    // inline once. A null return means "missing subject claim or no
    // matching user" — both cases the caller handles identically
    // (Results.Unauthorized()), so this collapses them into one signal
    // rather than distinguishing the two failure modes.
    private static async Task<User?> ResolveCurrentUserAsync(
        ClaimsPrincipal principal, IUserRepository userRepository, CancellationToken cancellationToken)
    {
        var authProviderUserId = principal.GetAuthProviderUserId();
        if (authProviderUserId is null)
            return null;

        return await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
    }

    private static AvatarSubmissionSummary? ToSummary(AvatarSubmission? submission) =>
        submission is null
            ? null
            : new AvatarSubmissionSummary(submission.Id, submission.CreatedAt, $"/users/me/avatar/{submission.Id}/image");
}

// Id/Status/CreatedAt only — never AvatarSubmission itself
// (docs/coding-guidelines.md's DTO-at-the-boundary rule) and never
// ImageStorageKey/SubmittingUserId, neither of which the caller has any
// use for.
public record SubmitAvatarResponse(Guid Id, string Status, DateTime CreatedAt);

// REQ-722 (S-182): GET /users/me/avatar's response — three independent
// slots, not one "current status" field, because REQ-722's "Seeing your
// own status" criterion requires a Rejected submission to never hide a
// separately-existing Approved one from an earlier submission (and,
// symmetrically, a fresh Pending upload never hides an older Approved one
// either — REQ-722's "Replacing an approved avatar" criterion). Any subset
// of the three can be non-null at once for the same player; the frontend
// (S-182, ui-implementer's own task, not built by this file) decides how
// to present that combination, this endpoint just reports it faithfully.
public record AvatarStatusResponse(
    AvatarSubmissionSummary? Pending, AvatarSubmissionSummary? Rejected, AvatarSubmissionSummary? Approved);

// Id/CreatedAt/ImageUrl only — same DTO-at-the-boundary reasoning
// SubmitAvatarResponse's own doc comment gives (never AvatarSubmission
// itself, never ImageStorageKey/SubmittingUserId). ImageUrl is always this
// same API's own relative path (GET /users/me/avatar/{id}/image below),
// never a raw Supabase Storage URL — ADR-0013's "backend mediates, frontend
// never talks to the provider directly" convention, the same reasoning
// SupabaseAvatarStorage.cs's own top-of-file comment gives for why every
// Supabase Storage call is backend-initiated.
public record AvatarSubmissionSummary(Guid Id, DateTime CreatedAt, string ImageUrl);

// Pure log-category marker for ILogger<T> — same pattern as
// SuggestionEndpoints.cs's SuggestionEndpointsLogCategory/IncidentEndpoints
// .cs's IncidentEndpointsLogCategory.
internal sealed class AvatarEndpointsLogCategory;

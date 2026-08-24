using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Storage;
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

            var authProviderUserId = principal.GetAuthProviderUserId();
            if (authProviderUserId is null)
                return Results.Unauthorized();

            var user = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
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
    }
}

// Id/Status/CreatedAt only — never AvatarSubmission itself
// (docs/coding-guidelines.md's DTO-at-the-boundary rule) and never
// ImageStorageKey/SubmittingUserId, neither of which the caller has any
// use for.
public record SubmitAvatarResponse(Guid Id, string Status, DateTime CreatedAt);

// Pure log-category marker for ILogger<T> — same pattern as
// SuggestionEndpoints.cs's SuggestionEndpointsLogCategory/IncidentEndpoints
// .cs's IncidentEndpointsLogCategory.
internal sealed class AvatarEndpointsLogCategory;

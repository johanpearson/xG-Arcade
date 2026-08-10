using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Admin;

// REQ-511: the admin-only create/edit/activate/deactivate half of the
// site-wide announcement banner — GET /announcement-banner (the public,
// unauthenticated read path every visitor uses) lives in
// Announcements/AnnouncementBannerEndpoints.cs, deliberately its own file
// outside XGArcade.Api.Admin, same split as Suggestions/
// SuggestionEndpoints.cs (submission) vs. this file's own
// AdminSuggestionEndpoints.cs sibling (review).
//
// Every action here goes through IAnnouncementBannerRepository, which
// itself enforces the "at most one row, ever" singleton invariant (see
// AnnouncementBanner's own doc comment) — this file never constructs a
// second AnnouncementBanner row directly.
public static class AdminAnnouncementBannerEndpoints
{
    // REQ-511: "a reasonable max length, exact limit left to
    // implementation." No existing free-text column in this codebase
    // enforces a max length at all (PlayerOverride.Reason, PlayerSuggestion
    // .PlayerName, etc. are all unconstrained `text`) — this is the first,
    // chosen because a banner is meant to be a short notice rendered
    // inline on every page, not a long-form message, and picked
    // deliberately generously (well above any realistic maintenance-notice
    // length) so a legitimate message is never truncated as a side effect
    // of this REQ's "reasonable max length" requirement. Flagged as a
    // judgment call, not dictated by REQ-511's own text.
    private const int MaxMessageLength = 500;

    public static void MapAdminAnnouncementBannerEndpoints(this WebApplication app)
    {
        // REQ-511: "the banner's message is created (if none existed) or
        // the existing banner's message is replaced with the new text —
        // there is exactly one banner record at a time." PUT (not POST):
        // idempotent create-or-replace-in-place, same semantics as this
        // codebase's existing PUT /admin/player-overrides/{id} and
        // PUT /admin/rounds/{gameKey}/end-time (AdminEndpoints.cs/
        // AdminManagementEndpoints.cs) — repeating the same call with the
        // same body always leaves the resource in the same state, unlike a
        // POST that creates a new row each time.
        app.MapPut("/admin/announcement-banner", async (
            UpsertAnnouncementBannerRequest request,
            ClaimsPrincipal principal,
            IAnnouncementBannerRepository announcementBannerRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            // REQ-511: "a blank/empty message is rejected with a
            // validation error and does not change the stored banner" —
            // checked before any repository call, so a rejected request
            // never touches the row.
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.Problem(
                    title: "Invalid announcement banner",
                    detail: "message must not be empty.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var trimmedMessage = request.Message.Trim();
            if (trimmedMessage.Length > MaxMessageLength)
            {
                return Results.Problem(
                    title: "Invalid announcement banner",
                    detail: $"message must be {MaxMessageLength} characters or fewer.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Policy below already required a valid "sub" claim to reach here.
            var adminId = principal.GetAuthProviderUserId()!.Value;
            var updatedAt = timeProvider.GetUtcNow().UtcDateTime;

            var banner = await announcementBannerRepository.UpsertMessageAsync(trimmedMessage, adminId, updatedAt, cancellationToken);

            return Results.Ok(ToResponse(banner));
        }).RequireAuthorization("Admin");

        // REQ-511: "it becomes visible to every visitor the next time they
        // fetch it... no push/real-time delivery is required" — flips
        // IsActive only, leaving Message untouched.
        app.MapPost("/admin/announcement-banner/activate", async (
            ClaimsPrincipal principal,
            IAnnouncementBannerRepository announcementBannerRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            // Policy below already required a valid "sub" claim to reach here.
            var adminId = principal.GetAuthProviderUserId()!.Value;
            var updatedAt = timeProvider.GetUtcNow().UtcDateTime;

            var banner = await announcementBannerRepository.SetActiveAsync(true, adminId, updatedAt, cancellationToken);
            // No banner has ever been created yet — REQ-511 only describes
            // activating "a banner with saved text [that] exists," never a
            // still-nonexistent one; PUT /admin/announcement-banner above
            // is the only way to create the row this action needs.
            if (banner is null)
            {
                return Results.Problem(
                    title: "No announcement banner exists",
                    detail: "Create a banner with PUT /admin/announcement-banner before activating it.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Ok(ToResponse(banner));
        }).RequireAuthorization("Admin");

        // REQ-511: "it stops being visible to every visitor the next time
        // they fetch it, and deactivating does not delete the banner's
        // saved message" — same IsActive-only flip as activate above, in
        // the other direction.
        app.MapPost("/admin/announcement-banner/deactivate", async (
            ClaimsPrincipal principal,
            IAnnouncementBannerRepository announcementBannerRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            // Policy below already required a valid "sub" claim to reach here.
            var adminId = principal.GetAuthProviderUserId()!.Value;
            var updatedAt = timeProvider.GetUtcNow().UtcDateTime;

            var banner = await announcementBannerRepository.SetActiveAsync(false, adminId, updatedAt, cancellationToken);
            if (banner is null)
            {
                return Results.Problem(
                    title: "No announcement banner exists",
                    detail: "Create a banner with PUT /admin/announcement-banner before deactivating it.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Ok(ToResponse(banner));
        }).RequireAuthorization("Admin");

        // REQ-511's own admin area needs to see the banner's current full
        // state (including IsActive and audit fields) to render its
        // create/edit/activate/deactivate controls correctly — the public
        // GET /announcement-banner (Announcements/AnnouncementBannerEndpoints.cs)
        // deliberately returns a minimal Active/Message-only shape instead
        // and is the wrong endpoint for an admin screen to poll, since it
        // collapses "no banner ever created" and "banner exists but
        // inactive" into the same response.
        app.MapGet("/admin/announcement-banner", async (
            IAnnouncementBannerRepository announcementBannerRepository,
            CancellationToken cancellationToken) =>
        {
            var banner = await announcementBannerRepository.GetAsync(cancellationToken);
            return banner is null ? Results.NotFound() : Results.Ok(ToResponse(banner));
        }).RequireAuthorization("Admin");
    }

    private static AdminAnnouncementBannerResponse ToResponse(AnnouncementBanner banner) =>
        new(banner.Id, banner.Message, banner.IsActive, banner.CreatedAt, banner.UpdatedAt, banner.LastUpdatedByAdminId);
}

public record UpsertAnnouncementBannerRequest(string Message);

public record AdminAnnouncementBannerResponse(
    Guid Id, string Message, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt, Guid LastUpdatedByAdminId);

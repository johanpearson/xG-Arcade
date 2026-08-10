using XGArcade.Data.Repositories;

namespace XGArcade.Api.Announcements;

// REQ-511: the public, read-only half of the site-wide announcement banner
// — the admin-only create/edit/activate/deactivate actions live in
// Admin/AdminAnnouncementBannerEndpoints.cs, same "submission-only file vs.
// admin-only file" split as Suggestions/SuggestionEndpoints.cs vs.
// Admin/AdminSuggestionEndpoints.cs.
public static class AnnouncementBannerEndpoints
{
    public static void MapAnnouncementBannerEndpoints(this WebApplication app)
    {
        // REQ-511: "fetching it requires no authentication of any kind" —
        // any visitor, logged-in, guest, or fully logged-out with no
        // session at all. Deliberately no .RequireAuthorization() call at
        // all, the same way Program.cs's own GET /health is registered —
        // that's the one other endpoint in this codebase with no auth
        // requirement whatsoever (every other player-facing GET, e.g.
        // GET /rounds/current, GET /players/autocomplete, calls
        // .RequireAuthorization()).
        app.MapGet("/announcement-banner", async (
            IAnnouncementBannerRepository announcementBannerRepository,
            CancellationToken cancellationToken) =>
        {
            var banner = await announcementBannerRepository.GetAsync(cancellationToken);

            // REQ-511: "no banner exists, or the only banner on record is
            // inactive... the response indicates there is no active
            // banner (not an error), and no banner is shown" — both cases
            // collapse to the same Active=false/Message=null response
            // shape, always a 200, never a 404.
            if (banner is null || !banner.IsActive)
                return Results.Ok(new AnnouncementBannerResponse(false, null));

            return Results.Ok(new AnnouncementBannerResponse(true, banner.Message));
        });
    }
}

// Message is null exactly when Active is false — mirrors
// WikidataPlayerLookupResponse's Found/nullable-fields convention
// (AdminSuggestionEndpoints.cs).
public record AnnouncementBannerResponse(bool Active, string? Message);

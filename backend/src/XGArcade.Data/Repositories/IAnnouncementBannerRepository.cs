using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// REQ-511: the only path to AnnouncementBanner — same "repositories
// encapsulate DbContext, endpoint code never queries it directly"
// discipline as every other repository in this project
// (docs/coding-guidelines.md). Deliberately three narrow methods rather
// than a general CRUD surface: this table only ever has at most one row,
// so there is no "list," no "get by id," and no "delete" — see
// AnnouncementBanner's own doc comment for why the singleton shape is
// enforced at this layer rather than left to callers to remember.
public interface IAnnouncementBannerRepository
{
    // The public read path (Announcements/AnnouncementBannerEndpoints.cs)
    // and the admin write paths' own "does a banner already exist" checks
    // both go through this one method. Null means no banner has ever been
    // created — a normal, expected state (not an error) for a fresh
    // environment.
    Task<AnnouncementBanner?> GetAsync(CancellationToken cancellationToken = default);

    // REQ-511's create-or-replace write: creates the singleton row if none
    // exists yet, or replaces the existing row's Message in place if one
    // does — never inserts a second row. IsActive is left untouched on an
    // edit of an existing row (REQ-511: "an edit to an already-active
    // banner does not require a separate deactivate/reactivate step" — the
    // converse also holds, editing an inactive banner does not activate
    // it). A newly-created row starts IsActive=false — REQ-511's
    // activate/deactivate criteria describe a freshly-created banner as
    // "currently inactive (or has never been activated)," never as active
    // by default.
    Task<AnnouncementBanner> UpsertMessageAsync(
        string message, Guid adminId, DateTime updatedAt, CancellationToken cancellationToken = default);

    // Flips IsActive without touching Message — REQ-511's "deactivating
    // does not delete the banner's saved message." Returns null (and
    // writes nothing) if no banner row exists yet — there is nothing to
    // activate/deactivate until an admin has created one via
    // UpsertMessageAsync at least once; the caller (AdminAnnouncementBannerEndpoints)
    // translates that into a 404.
    Task<AnnouncementBanner?> SetActiveAsync(
        bool isActive, Guid adminId, DateTime updatedAt, CancellationToken cancellationToken = default);
}

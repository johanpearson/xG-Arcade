namespace XGArcade.Data.Entities;

// REQ-511: a site-wide, admin-managed notification banner (maintenance
// notices, announcements) visible to every visitor — including a fully
// logged-out one — with no push/real-time delivery, scheduling, per-user
// dismissal, severity/color levels, or rich text (all explicitly out of
// scope per REQ-511's own acceptance criteria).
//
// Deliberately a true singleton table — never a list/queue of concurrent
// banners. There is at most one row, ever: the first admin write creates
// it, and every write after that (edit, activate, deactivate) mutates the
// same row in place (IAnnouncementBannerRepository never inserts a second
// row). Modeled this way — rather than "many banners, query for the most
// recent" — because REQ-511 is explicit that a second create/edit
// "replaces the single existing banner, it does not create an additional
// one," so the data model itself should make a second row structurally
// impossible to reach through the repository's own API, not just
// conventionally avoided.
//
// Deactivating never deletes this row or clears Message — REQ-511's own
// "an admin can reactivate the same text later, or edit it first, without
// retyping it from scratch." IsActive alone gates visibility to the public
// read endpoint (Announcements/AnnouncementBannerEndpoints.cs); nothing
// else about a banner's shape ever changes based on active/inactive state.
public class AnnouncementBanner
{
    public Guid Id { get; set; }

    public required string Message { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    // Stamped on every write to this row (message replaced, activated, or
    // deactivated) — same "who and when, on the row itself" precedent as
    // PlayerOverride.LockedByAdminId/LockedAt and PlayerSuggestion.
    // ResolvedByAdminId/ResolvedAt, rather than a separate audit-log table
    // this codebase has deliberately avoided so far. Not required by
    // REQ-511's own acceptance criteria (no test level asks for it), but
    // added for the same auditability every other admin-write entity in
    // this codebase already carries — flagged as a judgment call, not a
    // REQ-511 requirement.
    public Guid LastUpdatedByAdminId { get; set; }
    public DateTime UpdatedAt { get; set; }
}

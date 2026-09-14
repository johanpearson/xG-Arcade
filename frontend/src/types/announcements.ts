// REQ-511: the public GET /announcement-banner response shape
// (Announcements/AnnouncementBannerEndpoints.AnnouncementBannerResponse) —
// message is non-null exactly when active is true. Both "no banner has
// ever been created" and "a banner exists but is inactive" collapse to the
// same `{ active: false, message: null }` shape here — a visitor never
// needs to tell those two apart (unlike the admin-only shape below, which
// does, via its own 404-as-null convention in fetchAdminAnnouncementBanner).
export interface AnnouncementBanner {
  active: boolean;
  message: string | null;
}

// REQ-511: the admin-only shape shared by GET/PUT/activate/deactivate
// /admin/announcement-banner (Admin/AdminAnnouncementBannerEndpoints
// .AdminAnnouncementBannerResponse) — carries isActive and audit fields the
// public AnnouncementBanner shape above deliberately omits, so the admin
// screen can pre-populate its form and know the current active state on
// load without a second request.
export interface AdminAnnouncementBanner {
  id: string;
  message: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  lastUpdatedByAdminId: string;
}

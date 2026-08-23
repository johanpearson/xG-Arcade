import type { AdminAnnouncementBanner, AnnouncementBanner } from './types';
import { ApiError, apiRequest } from './apiClient';

// REQ-511: the site-wide announcement banner's public, unauthenticated
// read — GET /announcement-banner. Deliberately sends no Authorization
// header at all, unlike every other function in this file (mirrors the
// backend's own "no .RequireAuthorization() call, same as GET /health"
// note) — this must work identically for a logged-in user, a guest, and a
// fully logged-out visitor with no session whatsoever, since App.tsx
// mounts the component calling this above <header>, outside any
// authenticated render path. `accessToken: null` is what tells apiRequest
// to omit the Authorization header entirely (see its own comment). Always
// resolves (never throws): the backend contract guarantees a 200 even when
// no banner has ever been created (`{ active: false, message: null }`), so
// there's no error path to distinguish here the way most other reads in
// this file have.
export async function fetchAnnouncementBanner(): Promise<AnnouncementBanner> {
  return apiRequest<AnnouncementBanner>(null, '/announcement-banner');
}

// REQ-511: the admin screen's own read — GET /admin/announcement-banner —
// returns the fuller shape (isActive/audit fields) the public
// fetchAnnouncementBanner above deliberately omits, so AdminScreen can
// pre-populate its form and know the current active state on load. A 404
// ("no banner has ever been created yet") is a real, expected state, not
// an error — resolves to null rather than throwing, mirroring
// fetchCurrentRound's own 404-as-null idiom, so the caller can render an
// empty create form instead of an error. Catches the ApiError apiRequest
// throws for the 404 rather than letting it surface.
export async function fetchAdminAnnouncementBanner(
  accessToken: string,
): Promise<AdminAnnouncementBanner | null> {
  try {
    return await apiRequest<AdminAnnouncementBanner>(accessToken, '/admin/announcement-banner');
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  }
}

// REQ-511: creates the banner (if none exists yet) or replaces its message
// in place (PUT — idempotent create-or-replace, same semantics as
// updateAdminRoundEndTime in admin.ts) — never touches isActive. A 400
// (blank/whitespace-only message, or over the server's max length) is left
// to throw so the caller shows the server's own detail text inline, same
// convention as createPlayerOverride in admin.ts.
export async function upsertAnnouncementBanner(
  accessToken: string,
  message: string,
): Promise<AdminAnnouncementBanner> {
  return apiRequest<AdminAnnouncementBanner>(accessToken, '/admin/announcement-banner', {
    method: 'PUT',
    body: JSON.stringify({ message }),
  });
}

// REQ-511: flips the banner active — no request body. A 404 (no banner has
// ever been created yet) is left to throw; the caller (AdminScreen's
// AnnouncementBannerSection) only ever renders this action once a banner
// already exists, so this is defense in depth, not the primary guard, same
// convention as lookupPlayerByName's blank-name check in admin.ts.
export async function activateAnnouncementBanner(accessToken: string): Promise<AdminAnnouncementBanner> {
  return apiRequest<AdminAnnouncementBanner>(accessToken, '/admin/announcement-banner/activate', {
    method: 'POST',
  });
}

// REQ-511: sibling to activateAnnouncementBanner above, in the other
// direction — same 404 behavior/reasoning, same no-request-body shape.
export async function deactivateAnnouncementBanner(accessToken: string): Promise<AdminAnnouncementBanner> {
  return apiRequest<AdminAnnouncementBanner>(accessToken, '/admin/announcement-banner/deactivate', {
    method: 'POST',
  });
}

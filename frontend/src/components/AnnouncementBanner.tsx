import { useEffect, useState } from 'react';
import { fetchAnnouncementBanner } from '../lib/api';
import type { AnnouncementBanner as AnnouncementBannerState } from '../lib/types';
import './AnnouncementBanner.css';

// REQ-511: the site-wide, admin-managed announcement banner — must be
// visible to every visitor, logged-in, guest, or fully logged-out with no
// session at all. Mounted at the very top of App.tsx, above <header> and
// outside every auth-gated branch (`accessToken`/`showAuthScreen`), so it
// renders identically on the splash screen, the auth screen, and every
// authenticated screen alike — never nested inside a component that only
// exists after login.
//
// Fetches once on mount, no polling: REQ-511's own acceptance criteria is
// explicit that "no push/real-time delivery is required" and a fresh
// visitor "fetch" (page load) is sufficient. No existing periodic-refresh
// pattern in this codebase was worth reusing here — the closest
// candidates (LeaderboardScreen's 15s poll) exist for genuinely live,
// fast-changing data; a maintenance/announcement notice is expected to
// change on the order of hours or days, and REQ-511's own text frames the
// delivery moment as "the next time they fetch it (e.g. on page load or
// the frontend's next poll)" — page load already satisfies that.
export function AnnouncementBanner() {
  const [banner, setBanner] = useState<AnnouncementBannerState | null>(null);

  useEffect(() => {
    let cancelled = false;

    fetchAnnouncementBanner()
      .then((result) => {
        if (!cancelled) setBanner(result);
      })
      .catch(() => {
        // The backend's own contract guarantees a 200 here (REQ-511: "not
        // an error, even when no banner has ever been created") — a
        // failure this catches is a genuine network/infra problem, not a
        // real "no banner" state. Left as a silent no-banner outcome
        // rather than a page-wide error, since a maintenance notice
        // failing to load is never worth blocking or degrading the rest
        // of the page over.
      });

    return () => {
      cancelled = true;
    };
  }, []);

  if (!banner || !banner.active || !banner.message) return null;

  return (
    <div className="announcement-banner" role="status">
      <p className="announcement-banner__message">{banner.message}</p>
    </div>
  );
}

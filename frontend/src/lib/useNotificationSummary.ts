import { useEffect, useState } from 'react';
import { ApiError } from './apiClient';
import { fetchNotificationSummary } from './notifications';
import type { NotificationSummaryResponse } from './types';

// REQ-1411 (design-document.md SCREEN-07's 2026-09-03 status note): the
// same interval AllTimeLeaderboard.tsx's own 15s poll already uses —
// reused for consistency, not re-derived, since REQ-1411 itself only
// requires "page-load/poll-driven refresh," no specific cadence.
const REFRESH_INTERVAL_MS = 15_000;

const EMPTY_SUMMARY: NotificationSummaryResponse = {
  pendingFriendRequestCount: 0,
  pendingChallengeCount: 0,
  matchesAwaitingActionCount: 0,
  hasPending: false,
};

// Mounted once at the top of App() (accessToken-gated), the same
// "regardless of which screen is showing" placement useThemePreference/
// IncidentReportDialog's own state already uses — HeaderNav's "Friends"
// badge (SCREEN-07) must stay current no matter what's on screen, not only
// while a particular screen is mounted.
//
// Self-rescheduling via setTimeout (not setInterval), mirroring
// AllTimeLeaderboard.tsx's own poll exactly (see that file's own comment
// for why: guarantees only one fetch is ever in flight, so a slow response
// can never overlap with a later one). A transient poll failure never
// blanks/errors the badge — it just leaves the last known counts showing,
// the same "never replace already-good state with an error" discipline
// the leaderboard's own poll already follows; only a real 401 escalates,
// via onAuthError.
export function useNotificationSummary(
  accessToken: string | null,
  onAuthError: () => void,
): NotificationSummaryResponse {
  const [summary, setSummary] = useState<NotificationSummaryResponse>(EMPTY_SUMMARY);

  useEffect(() => {
    if (!accessToken) {
      setSummary(EMPTY_SUMMARY);
      return;
    }

    let cancelled = false;
    let timeoutId: number | undefined;

    function load() {
      fetchNotificationSummary(accessToken as string)
        .then((result) => {
          if (cancelled) return;
          setSummary(result);
        })
        .catch((error: unknown) => {
          if (cancelled) return;
          if (error instanceof ApiError && error.status === 401) {
            onAuthError();
            return;
          }
          console.error('Notification summary poll failed:', error);
        })
        .finally(() => {
          if (!cancelled) timeoutId = window.setTimeout(load, REFRESH_INTERVAL_MS);
        });
    }

    load();

    return () => {
      cancelled = true;
      if (timeoutId != null) window.clearTimeout(timeoutId);
    };
  }, [accessToken, onAuthError]);

  return summary;
}

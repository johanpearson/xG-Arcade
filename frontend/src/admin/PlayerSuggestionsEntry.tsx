import { useCallback } from 'react';
import { fetchPendingSuggestions } from '../lib/admin';
import { useAuthedFetch } from '../lib/useAuthedFetch';

interface PlayerSuggestionsEntryProps {
  accessToken: string;
  onAuthError: () => void;
  onOpenSuggestions: () => void;
}

// REQ-512: the "Player suggestions" entry point's pending-count badge.
// Reuses REQ-509's existing GET /admin/suggestions data (fetchPendingSuggestions)
// — no new endpoint, no second data source. Uses the shared
// useAuthedFetch hook (same resilience pattern as
// AccountMetricsSection/XGPathCycleSection): a 401 escalates via
// onAuthError, a 403 leaves the count absent silently (this section never
// erroring or flipping the whole page to access-denied — the button itself
// still works regardless, since SuggestionsScreen enforces its own access
// checks), and anything else (500, network failure, parse error, etc.) is
// surfaced inline via loadError rather than silently read as "nothing
// pending" — the one failure mode this badge can't afford, since its whole
// purpose is letting an admin trust it without opening the screen.
// Fetch-on-load only, per REQ-512's "no polling/websocket" scope: App.tsx's
// screen ternary unmounts AdminScreen while SuggestionsScreen is open and
// remounts it on the way back, so returning from resolving a suggestion
// there naturally re-triggers this fetch with no extra refresh plumbing.
// Renders the count as plain text next to the button label (e.g. "Player
// suggestions (3)"), the same convention UnverifiedDataSection's own
// "Unverified data (N)" heading already uses in AdminScreen.tsx —
// deliberately not a colored pill/badge, since design-document.md §2 has no
// token for one and this avoids introducing an ad-hoc color per CLAUDE.md's
// token rule.
export function PlayerSuggestionsEntry({ accessToken, onAuthError, onOpenSuggestions }: PlayerSuggestionsEntryProps) {
  const fetchFn = useCallback(() => fetchPendingSuggestions(accessToken), [accessToken]);
  // `hidden` is deliberately unused here — a 403 just leaves `data` null,
  // the same way any other unfetched state does, and that alone already
  // produces REQ-512's "no badge/count shown" behavior. Unlike
  // AccountMetricsSection/XGPathCycleSection, this section never hides
  // itself outright — the button must keep rendering regardless of the
  // fetch's outcome, since SuggestionsScreen enforces its own access checks.
  const { data, loadError } = useAuthedFetch(fetchFn, { onAuthError });
  const pendingCount = data ? data.length : null;

  return (
    <section className="admin-screen__section">
      <button type="button" onClick={onOpenSuggestions}>
        Player suggestions{pendingCount !== null && pendingCount > 0 ? ` (${pendingCount})` : ''}
      </button>
      {loadError && (
        <p className="admin-screen__error" role="alert">
          {loadError}
        </p>
      )}
    </section>
  );
}

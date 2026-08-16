import { useCallback } from 'react';
import { fetchAdminIncidentReports } from '../lib/incidents';
import { useAuthedFetch } from '../lib/useAuthedFetch';

// REQ-904/ADR-0064/ADR-0066: this repo's fixed, server-configured owner/repo/
// label (same values Program.cs's GitHubIncidentReportOptions defaults to,
// and the same ones the backend itself already writes issues to/reads issues
// from) — hard-coded here as a display-only link, never accepted as a prop
// or sourced from anything dynamic, matching REQ-904's "no client-supplied
// repo/label" rule and ADR-0064's "target repo and label are hard-coded
// server-side" boundary. This is not a request parameter to any endpoint, so
// hard-coding a second copy on the frontend doesn't violate that boundary —
// it's just where GitHub's own filtered issue list already lives.
const INCIDENT_REPORTS_GITHUB_URL =
  'https://github.com/johanpearson/xg-arcade/issues?q=is%3Aissue+is%3Aopen+label%3Auser-reported';

interface IncidentReportsEntryProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-904/ADR-0066 (S-098): fetch-on-load only (no polling/websocket —
// REQ-904's own freshness model), using the shared useAuthedFetch hook
// for the transport half (401/403/thrown-error/cancel). Three renderable
// states, not PlayerSuggestionsEntry's two, because a GitHub-poll failure
// (`available: false`) is a real, distinct failure/unknown state — never
// conflated with "you're not an admin" (403, handled identically to
// AccountMetricsSection/XGPathCycleSection's own hide-quietly pattern, since
// this section — unlike PlayerSuggestionsEntry's button — has no
// separately-gated destination screen to fall back on) and never conflated
// with a genuine zero count. A 401 escalates via onAuthError; a 403 hides
// this section only; a GitHub-poll failure (`available: false` in a normal
// 200 body, per ADR-0066 — never a thrown error) renders a distinct inline
// message, branched on locally rather than inside the hook (see
// useAuthedFetch's own doc comment for why); any other failure (500,
// network, parse) also renders inline rather than silently reading as
// "nothing open", the one failure mode this entry point can't afford per
// REQ-904's "never a false zero-count" rule. Renders the count next to the
// heading the same way UnverifiedDataSection's own "Unverified data (N)"
// heading does, except the count itself is omitted entirely at zero
// (REQ-904/REQ-512's shared "absence, not '0'" convention) rather than
// always shown.
export function IncidentReportsEntry({ accessToken, onAuthError }: IncidentReportsEntryProps) {
  const fetchFn = useCallback(() => fetchAdminIncidentReports(accessToken), [accessToken]);
  const { data, hidden, loadError } = useAuthedFetch(fetchFn, { onAuthError });

  if (hidden) return null;

  // ADR-0066: `available: false` is a business-level state carried inside a
  // normal 200 response body, not a thrown error, so it's branched on here
  // rather than inside useAuthedFetch (which only owns transport-level
  // states). Never rendered as openCount: 0.
  const openCount = data && data.available ? data.openCount : null;
  const unavailable = data !== null && !data.available;

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">
        Incident reports{openCount !== null && openCount > 0 ? ` (${openCount})` : ''}
      </h3>
      {openCount !== null && openCount > 0 && (
        <a
          className="admin-screen__link"
          href={INCIDENT_REPORTS_GITHUB_URL}
          target="_blank"
          rel="noreferrer"
        >
          View open reports on GitHub
        </a>
      )}
      {unavailable && (
        <p className="admin-screen__error" role="alert">
          Couldn't check GitHub for open incident reports right now — this doesn't mean there are none, try
          reloading in a minute.
        </p>
      )}
      {loadError && (
        <p className="admin-screen__error" role="alert">
          {loadError}
        </p>
      )}
    </section>
  );
}

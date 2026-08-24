import { useCallback } from 'react';
import { ApiError } from '../lib/apiClient';
import { fetchActiveAdminRound, fetchUnverifiedPlayerData } from '../lib/admin';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import { XG_GRID_GAME_KEY } from '../games/GameSelectScreen';
import { PlayerSuggestionsEntry } from './PlayerSuggestionsEntry';
import { IncidentReportsEntry } from './IncidentReportsEntry';
import { AnnouncementBannerSection } from './AnnouncementBannerSection';
import { UnverifiedDataSection } from './UnverifiedDataSection';
import { AccountMetricsSection } from './AccountMetricsSection';
import { XGPathCycleSection } from './XGPathCycleSection';
import { RoundControlSection } from './RoundControlSection';
import { UserDeletionSection } from './UserDeletionSection';
import './AdminScreen.css';

export interface AdminScreenProps {
  accessToken: string;
  onAuthError: () => void;
  // REQ-509/REQ-510 (S-090)/ADR-0053: the only entry point into
  // SuggestionsScreen — App.tsx wires this to navigateTo('admin-suggestions'),
  // mirroring how SettingsScreen's own onOpenAdmin link is this screen's own
  // entry point. Never a standalone top-level nav entry (ADR-0053).
  onOpenSuggestions: () => void;
}

// SCREEN-04, REQ-504: the admin page S-012 deliberately deferred. Reached
// only via App.tsx's admin-only nav link (REQ-504's "no visible entry
// point" half); this component provides the other half — every underlying
// endpoint 403s a non-admin token directly, and the unverified-data fetch's
// own 403 is what flips this whole page to an access-denied message,
// independent of the nav-hiding.
//
// S-157: migrated onto the shared useAuthedFetch hook (two independent
// instances, one per endpoint, mirroring AccountMetricsSection's own usage)
// rather than the single hand-rolled Promise.allSettled effect this used to
// run. The two fetches must stay independently refetchable — see
// refreshUnverified/refreshActiveRound below — so this deliberately does NOT
// collapse to one shared refetch.
export function AdminScreen({ accessToken, onAuthError, onOpenSuggestions }: AdminScreenProps) {
  const rowsFetchFn = useCallback(() => fetchUnverifiedPlayerData(accessToken), [accessToken]);
  const {
    data: unverifiedRows,
    hidden: rowsHidden,
    loadError: rowsError,
    refetch: refreshUnverified,
  } = useAuthedFetch(rowsFetchFn, { onAuthError });

  // REQ-505/506: fetchActiveAdminRound already resolves a bare 404 (feature
  // absent in Production) to `null` rather than throwing — this wrapper
  // additionally swallows any OTHER non-401/403 failure (e.g. a 500 or a
  // network error) to `null` too, so useAuthedFetch's own loadError/hidden
  // never fire for this probe on anything but a real 401/403. That matches
  // the pre-S-157 behavior exactly: only 401/403 ever escalated to a
  // page-level outcome for this fetch, every other failure just meant "treat
  // as no active round."
  const activeRoundFetchFn = useCallback(async () => {
    try {
      return await fetchActiveAdminRound(accessToken, XG_GRID_GAME_KEY);
    } catch (err) {
      if (err instanceof ApiError && (err.status === 401 || err.status === 403)) throw err;
      return null;
    }
  }, [accessToken]);
  const {
    data: activeRound,
    hidden: activeRoundHidden,
    refetch: refreshActiveRound,
  } = useAuthedFetch(activeRoundFetchFn, { onAuthError });

  // REQ-504/505: a 403 from either endpoint is a page-wide decision, unlike
  // every other admin sub-section's own 403 (which only hides that one
  // section) — this is the one exception, preserved from the pre-S-157
  // single-pageState version.
  if (rowsHidden || activeRoundHidden) {
    // REQ-504: the defense-in-depth half — reachable even if a non-admin
    // somehow lands on this screen directly, independent of App.tsx's
    // nav-hiding.
    return <p className="admin-screen__status">You don't have access to this page.</p>;
  }

  if (rowsError !== null) {
    return <p className="admin-screen__status admin-screen__status--error">{rowsError}</p>;
  }

  // A real, resolved fetch is always a UnverifiedPlayerData[] (possibly
  // empty) and never itself `null` — so `unverifiedRows === null` here is an
  // unambiguous "hasn't loaded yet" signal, same reasoning LeaguesScreen
  // already relies on for its own useAuthedFetch usage.
  if (unverifiedRows === null) {
    return <p className="admin-screen__status">Loading…</p>;
  }

  return (
    <div className="admin-screen">
      <h2 className="admin-screen__title">Admin</h2>

      {/* REQ-509/REQ-510 (S-090)/ADR-0053: the only entry point into
          SuggestionsScreen — a separate screen/file per that ADR, never
          folded into this one's sections below. Mirrors SettingsScreen's own
          "onOpenAdmin" link-out pattern, one level deeper. REQ-512 adds the
          pending-count badge shown alongside it. */}
      <PlayerSuggestionsEntry
        accessToken={accessToken}
        onAuthError={onAuthError}
        onOpenSuggestions={onOpenSuggestions}
      />

      {/* REQ-904/ADR-0066: the sibling "admin notification" entry point from
          the same S-096/S-097/S-098 grouping as PlayerSuggestionsEntry above
          — placed directly after it for that reason. Unlike
          PlayerSuggestionsEntry, there is no in-app screen to navigate to
          (ADR-0064's "no review queue" boundary), so this renders as a
          passive entry (heading + optional count + external link), not a
          button. */}
      <IncidentReportsEntry accessToken={accessToken} onAuthError={onAuthError} />

      {/* REQ-511: own fetch/state, same resilience pattern as
          AccountMetricsSection/XGPathCycleSection below — rendered
          unconditionally (this endpoint, like those, is registered in
          every environment), never gated by the Non-Production-only
          activeRound probe. */}
      <AnnouncementBannerSection accessToken={accessToken} onAuthError={onAuthError} />

      <UnverifiedDataSection
        accessToken={accessToken}
        rows={unverifiedRows}
        onAuthError={onAuthError}
        onRefresh={refreshUnverified}
      />

      {/* REQ-507/508: unlike RoundControlSection/UserDeletionSection below,
          this section is NOT gated by `activeRound !== null` — that gate
          exists only because the round-control/user-deletion probe 404s in
          Production (REQ-505/506's non-Production-only scope). REQ-507's
          metrics view and REQ-508's bulk guest-clear are both explicitly
          visible in every environment, including Production, so this section
          renders (and attempts its own fetch) unconditionally. */}
      <AccountMetricsSection accessToken={accessToken} onAuthError={onAuthError} />

      {/* REQ-1209: same "own fetch, own gating, never blocks or is blocked by
          any other admin section" pattern as AccountMetricsSection above —
          rendered unconditionally (this endpoint is registered in every
          environment, including Production, same as REQ-507/508's), not
          gated by the Non-Production-only `activeRound` probe below. */}
      <XGPathCycleSection accessToken={accessToken} onAuthError={onAuthError} />

      {activeRound !== null && (
        <>
          <RoundControlSection
            accessToken={accessToken}
            activeRound={activeRound}
            onAuthError={onAuthError}
            onRefresh={refreshActiveRound}
          />
          <UserDeletionSection accessToken={accessToken} onAuthError={onAuthError} />
        </>
      )}
    </div>
  );
}

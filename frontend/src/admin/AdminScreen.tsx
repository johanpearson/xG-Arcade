import { useCallback, useState } from 'react';
import { ApiError } from '../lib/apiClient';
import { fetchActiveAdminRound, fetchUnverifiedPlayerData } from '../lib/admin';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import { XG_GRID_GAME_KEY, XG_PREDICT_GAME_KEY } from '../games/GameSelectScreen';
import { PlayerSuggestionsEntry } from './PlayerSuggestionsEntry';
import { IncidentReportsEntry } from './IncidentReportsEntry';
import { AnnouncementBannerSection } from './AnnouncementBannerSection';
import { UnverifiedDataSection } from './UnverifiedDataSection';
import { AccountMetricsSection } from './AccountMetricsSection';
import { XGPathCycleSection } from './XGPathCycleSection';
import { RoundControlSection } from './RoundControlSection';
import { UserDeletionSection } from './UserDeletionSection';
import { AvatarModerationSection } from './AvatarModerationSection';
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

// REQ-516 (S-177): the grouped-nav tabs, in this fixed order — "Users" is
// first/default (no persistence of the last-selected group across a reload
// is in scope; REQ-516 explicitly leaves that out and says "always opens to
// the same default group, left to implementation"). "Predict" added
// 2026-08-31 alongside xG Predict's own RoundControlSection instance below
// (REQ-304/REQ-505's generalization) — same order GameSelectScreen's own
// tiles use (Grid, Path, Predict).
type AdminNavGroup = 'users' | 'grid' | 'path' | 'predict' | 'announcements' | 'issues';

const ADMIN_NAV_GROUPS: Array<{ value: AdminNavGroup; label: string }> = [
  { value: 'users', label: 'Users' },
  { value: 'grid', label: 'Grid' },
  { value: 'path', label: 'Path' },
  { value: 'predict', label: 'Predict' },
  { value: 'announcements', label: 'Announcements' },
  { value: 'issues', label: 'Issues' },
];

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
  // REQ-516: which nav group is currently visible. Deliberately does NOT
  // affect any fetch below — every section keeps fetching/loading exactly
  // as it did before this story, on mount, regardless of which group is
  // selected. See the render below for how switching groups toggles
  // visibility (the `hidden` attribute) without ever unmounting a section.
  const [activeGroup, setActiveGroup] = useState<AdminNavGroup>('users');

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

  // REQ-304/505 (2026-08-31): xG Predict's own active-round probe, same
  // shape/resilience as the Grid one above (404-as-null in Production,
  // every other non-401/403 failure also swallowed to null) — a fully
  // independent fetch/refetch pair, not a second call derived from the
  // Grid one, since the two games' rounds are entirely unrelated.
  const predictActiveRoundFetchFn = useCallback(async () => {
    try {
      return await fetchActiveAdminRound(accessToken, XG_PREDICT_GAME_KEY);
    } catch (err) {
      if (err instanceof ApiError && (err.status === 401 || err.status === 403)) throw err;
      return null;
    }
  }, [accessToken]);
  const {
    data: predictActiveRound,
    hidden: predictActiveRoundHidden,
    refetch: refreshPredictActiveRound,
  } = useAuthedFetch(predictActiveRoundFetchFn, { onAuthError });

  // REQ-504/505: a 403 from any of these probes is a page-wide decision,
  // unlike every other admin sub-section's own 403 (which only hides that
  // one section) — this is the one exception, preserved from the pre-S-157
  // single-pageState version, now extended to the Predict probe too since
  // it is the same underlying "not an admin" failure mode regardless of
  // which GameKey the round-control probe happened to be for.
  if (rowsHidden || activeRoundHidden || predictActiveRoundHidden) {
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

      {/* REQ-516: persistent grouped nav — only one group's sections are
          visible at a time. Every group below is always mounted; switching
          groups only toggles the `hidden` attribute on that group's
          wrapper, never conditional rendering, so no section's fetch is
          ever re-triggered by a group switch (mirrors the
          "always mounted, active-controlled" pattern LeaderboardScreen.tsx's
          scope tabs already established). The one exception is the
          RoundControlSection instances/UserDeletionSection below, which
          stay a real `activeRound !== null`/`predictActiveRound !== null`
          conditional nested inside the "Grid"/"Predict"/"Users" groups —
          that gate must still unmount them in Production, not just hide
          them behind an unselected tab. */}
      <div className="admin-screen__nav" role="tablist" aria-label="Admin section">
        {ADMIN_NAV_GROUPS.map(({ value, label }) => (
          <button
            key={value}
            type="button"
            role="tab"
            aria-selected={activeGroup === value}
            className={`admin-screen__nav-tab ${activeGroup === value ? 'admin-screen__nav-tab--active' : ''}`}
            onClick={() => setActiveGroup(value)}
          >
            {label}
          </button>
        ))}
      </div>

      {/* Users: account metrics (REQ-507) — which composes REQ-508's
          guest-clear section internally, kept as-is rather than split out —
          user deletion (REQ-506, Production-gated below, same as
          RoundControlSection's own gating in the Grid group), and avatar
          moderation (REQ-517, S-183 — rendered unconditionally, see its own
          render-site comment below). */}
      <div className="admin-screen__group" hidden={activeGroup !== 'users'}>
        <AccountMetricsSection accessToken={accessToken} onAuthError={onAuthError} />

        {activeRound !== null && <UserDeletionSection accessToken={accessToken} onAuthError={onAuthError} />}

        {/* REQ-517 (S-183): avatar moderation — always registered, in every
            environment (including Production), unlike UserDeletionSection
            above — so rendered unconditionally, not nested inside the
            `activeRound !== null` (Non-Production-only) gate. */}
        <AvatarModerationSection accessToken={accessToken} onAuthError={onAuthError} />
      </div>

      {/* Grid: unverified data review (REQ-503), player suggestions entry
          (REQ-509/510/512), and round control (REQ-505, Production-gated
          below). */}
      <div className="admin-screen__group" hidden={activeGroup !== 'grid'}>
        <UnverifiedDataSection
          accessToken={accessToken}
          rows={unverifiedRows}
          onAuthError={onAuthError}
          onRefresh={refreshUnverified}
        />

        {/* REQ-509/REQ-510 (S-090)/ADR-0053: the only entry point into
            SuggestionsScreen — a separate screen/file per that ADR, never
            folded into this one's sections below. Mirrors SettingsScreen's
            own "onOpenAdmin" link-out pattern, one level deeper. REQ-512
            adds the pending-count badge shown alongside it. */}
        <PlayerSuggestionsEntry
          accessToken={accessToken}
          onAuthError={onAuthError}
          onOpenSuggestions={onOpenSuggestions}
        />

        {activeRound !== null && (
          <RoundControlSection
            accessToken={accessToken}
            gameKey={XG_GRID_GAME_KEY}
            roundLabel="Grid Round"
            activeRound={activeRound}
            onAuthError={onAuthError}
            onRefresh={refreshActiveRound}
          />
        )}
      </div>

      {/* Path: xG Path target cycle control (REQ-1209) — same "own fetch,
          own gating, never blocks or is blocked by any other admin section"
          pattern as AccountMetricsSection above, rendered unconditionally
          (this endpoint is registered in every environment, including
          Production), not gated by the Non-Production-only `activeRound`
          probe. */}
      <div className="admin-screen__group" hidden={activeGroup !== 'path'}>
        <XGPathCycleSection accessToken={accessToken} onAuthError={onAuthError} />
      </div>

      {/* Predict: round control for xG Predict (REQ-304/505's
          generalization, 2026-08-31) — same Production-gated
          `predictActiveRound !== null` pattern as the Grid group's own
          RoundControlSection instance above, using the independent Predict
          probe/refetch pair. Added specifically so a stale/stuck xg-predict
          round (e.g. one generated against an already-elapsed matchday
          before ADR-0099's lookahead fix) can be cleared from the admin UI
          instead of only via a direct API call. */}
      <div className="admin-screen__group" hidden={activeGroup !== 'predict'}>
        {predictActiveRound !== null && (
          <RoundControlSection
            accessToken={accessToken}
            gameKey={XG_PREDICT_GAME_KEY}
            roundLabel="Predict Round"
            activeRound={predictActiveRound}
            onAuthError={onAuthError}
            onRefresh={refreshPredictActiveRound}
          />
        )}
      </div>

      {/* Announcements: site-wide announcement banner (REQ-511) — own
          fetch/state, same resilience pattern as AccountMetricsSection/
          XGPathCycleSection, rendered unconditionally (this endpoint, like
          those, is registered in every environment), never gated by the
          Non-Production-only activeRound probe. */}
      <div className="admin-screen__group" hidden={activeGroup !== 'announcements'}>
        <AnnouncementBannerSection accessToken={accessToken} onAuthError={onAuthError} />
      </div>

      {/* Issues: incident-reports admin notification (REQ-904/ADR-0066).
          Unlike PlayerSuggestionsEntry, there is no in-app screen to
          navigate to (ADR-0064's "no review queue" boundary), so this
          renders as a passive entry (heading + optional count + external
          link), not a button. */}
      <div className="admin-screen__group" hidden={activeGroup !== 'issues'}>
        <IncidentReportsEntry accessToken={accessToken} onAuthError={onAuthError} />
      </div>
    </div>
  );
}

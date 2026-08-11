import { useCallback, useEffect, useState } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { fetchActiveAdminRound, fetchUnverifiedPlayerData } from '../lib/admin';
import type { AdminActiveRound, UnverifiedPlayerData } from '../lib/types';
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

type PageState =
  | { phase: 'loading' }
  | { phase: 'access-denied' }
  | { phase: 'error'; message: string }
  | { phase: 'ready' };

// SCREEN-04, REQ-504: the admin page S-012 deliberately deferred. Reached
// only via App.tsx's admin-only nav link (REQ-504's "no visible entry
// point" half); this component provides the other half — every underlying
// endpoint 403s a non-admin token directly, and the unverified-data fetch's
// own 403 is what flips this whole page to an access-denied message,
// independent of the nav-hiding.
export function AdminScreen({ accessToken, onAuthError, onOpenSuggestions }: AdminScreenProps) {
  const [pageState, setPageState] = useState<PageState>({ phase: 'loading' });
  const [unverifiedRows, setUnverifiedRows] = useState<UnverifiedPlayerData[]>([]);
  // null both while the round-control/user-deletion feature is genuinely
  // absent (404 probe) and before the first load resolves — pageState.phase
  // gates the "still loading" case, so by the time pageState is 'ready',
  // null here always means "hidden", never "not fetched yet".
  const [activeRound, setActiveRound] = useState<AdminActiveRound | null>(null);

  const refreshUnverified = useCallback(async () => {
    const rows = await fetchUnverifiedPlayerData(accessToken);
    setUnverifiedRows(rows);
  }, [accessToken]);

  const refreshActiveRound = useCallback(async () => {
    const probe = await fetchActiveAdminRound(accessToken, XG_GRID_GAME_KEY);
    setActiveRound(probe);
  }, [accessToken]);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      const [unverifiedResult, activeRoundResult] = await Promise.allSettled([
        fetchUnverifiedPlayerData(accessToken),
        fetchActiveAdminRound(accessToken, XG_GRID_GAME_KEY),
      ]);
      if (cancelled) return;

      if (unverifiedResult.status === 'rejected') {
        const err = unverifiedResult.reason;
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          setPageState({ phase: 'access-denied' });
          return;
        }
        setPageState({ phase: 'error', message: describeError(err) });
        return;
      }
      setUnverifiedRows(unverifiedResult.value);

      if (activeRoundResult.status === 'rejected') {
        const err = activeRoundResult.reason;
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          setPageState({ phase: 'access-denied' });
          return;
        }
        // Non-fatal for the page as a whole — the round-control/user-deletion
        // sections just stay hidden, same as a genuine 404 probe result.
        setActiveRound(null);
      } else {
        setActiveRound(activeRoundResult.value);
      }

      setPageState({ phase: 'ready' });
    }

    load();

    return () => {
      cancelled = true;
    };
  }, [accessToken, onAuthError]);

  if (pageState.phase === 'loading') {
    return <p className="admin-screen__status">Loading…</p>;
  }

  if (pageState.phase === 'access-denied') {
    // REQ-504: the defense-in-depth half — reachable even if a non-admin
    // somehow lands on this screen directly, independent of App.tsx's
    // nav-hiding.
    return <p className="admin-screen__status">You don't have access to this page.</p>;
  }

  if (pageState.phase === 'error') {
    return <p className="admin-screen__status admin-screen__status--error">{pageState.message}</p>;
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

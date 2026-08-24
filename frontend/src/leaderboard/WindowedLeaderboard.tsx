import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { fetchWindowedLeaderboard } from '../lib/leaderboard';
import type { WindowResolution } from '../lib/leaderboard';
import { LeaderboardRowsList, type RowsReadyState } from './LeaderboardRowsList';
import type { GameKey } from './LeaderboardScreen';

// REQ-405 (S-027): SCREEN-03's "Time Windows" scope's own round/week/month/
// year sub-tabs — 'round' (the current round-in-progress-plus-recent
// rolling window, same underlying meaning as "round" everywhere else in
// this file) is the default: the most specific and most recently relevant
// resolution, and the one most analogous to what a player already checks
// most often (REQ-407's "Current Round" scope).
const DEFAULT_WINDOW_RESOLUTION: WindowResolution = 'round';

// REQ-405: sub-tab order/labels for the "Time Windows" scope — shortest
// window to longest, matching the resolution's own natural progression.
const WINDOW_RESOLUTIONS: Array<{ value: WindowResolution; label: string }> = [
  { value: 'round', label: 'Round' },
  { value: 'week', label: 'Week' },
  { value: 'month', label: 'Month' },
  { value: 'year', label: 'Year' },
];

// REQ-405 (S-027): the rolling-window scope's own state — same idle/
// loading/error/ready shape as LiveLeaderboard's own state, minus a "no
// active round" case: unlike REQ-407's active-round scope, a window always
// resolves to *some* ranked list (possibly empty, handled via
// `emptyMessage` below), never a 404.
type WindowState =
  | { phase: 'idle' }
  | { phase: 'loading' }
  | { phase: 'error'; message: string }
  | ({ phase: 'ready' } & RowsReadyState);

export interface WindowedLeaderboardProps {
  accessToken: string;
  gameKey: GameKey;
  onAuthError: () => void;
  // Whether "Time Windows" is the currently selected scope tab. This
  // component is always mounted alongside the other three scopes (see
  // LeaderboardScreen.tsx) so the selected round/week/month/year sub-tab
  // survives a switch away and back — `active` (rather than unmount/
  // remount) is what drives the "fetch on entry, refetch on every
  // re-entry" effect below.
  active: boolean;
  // REQ-411 (S-179): threaded straight through to LeaderboardRowsList — see
  // that component's own doc comment for the optional-prop/backward-compat
  // reasoning and the "why every row, including your own" judgement call.
  onSelectPlayer?: (userId: string, displayName: string) => void;
}

// REQ-406/407/408/405 (S-053/S-054/S-027, split out of LeaderboardScreen.tsx
// in S-121): REQ-405's calendar-aligned (never rolling) round/week/month/
// year leaderboard.
export function WindowedLeaderboard({
  accessToken,
  gameKey,
  onAuthError,
  active,
  onSelectPlayer,
}: WindowedLeaderboardProps) {
  const [windowResolution, setWindowResolution] = useState<WindowResolution>(DEFAULT_WINDOW_RESOLUTION);
  const [windowState, setWindowState] = useState<WindowState>({ phase: 'idle' });

  // Stable across renders (as long as onAuthError itself is) so the effect
  // below can safely list it as a dependency without re-running on every
  // render.
  const handleAuthError = useCallback(
    (error: unknown): boolean => {
      if (error instanceof ApiError && error.status === 401) {
        onAuthError();
        return true;
      }
      return false;
    },
    [onAuthError],
  );

  // Fetched on every transition into this scope, AND on every change of
  // `windowResolution` while already active (switching the round/week/
  // month/year sub-tab) — both are real reasons to issue a fresh request.
  // Same prev-ref-comparison-rather-than-phase-in-deps fix as
  // LiveLeaderboard/PastRoundsLeaderboard: `setWindowState({ phase:
  // 'loading' })` below changes `windowState.phase`, which would otherwise
  // re-trigger this very effect (cleanup racing the in-flight fetch) before
  // the fetch had a chance to resolve — comparing against the *previous*
  // `active`/resolution (both tracked in refs) instead means the effect
  // only fires on a genuine tab or sub-tab change. Same "loading flash on
  // re-entry" reasoning as the live scope: re-entering "window" (or
  // re-picking the same sub-tab after leaving and coming back) deliberately
  // shows the loading state again rather than quietly leaving the previous
  // resolution's rows on screen.
  //
  // REQ-410 (S-087): switching games while already active is a third real
  // reason to re-fetch, same pattern as `isChangingResolution` above.
  const prevActiveRef = useRef(active);
  const prevResolutionForWindowRef = useRef<WindowResolution>(windowResolution);
  const prevGameKeyForWindowRef = useRef<GameKey>(gameKey);
  useEffect(() => {
    const isEnteringWindow = active && !prevActiveRef.current;
    const isChangingResolution = active && prevResolutionForWindowRef.current !== windowResolution;
    const isSwitchingGameWhileWindow = active && prevGameKeyForWindowRef.current !== gameKey;
    prevActiveRef.current = active;
    prevResolutionForWindowRef.current = windowResolution;
    prevGameKeyForWindowRef.current = gameKey;
    if (!isEnteringWindow && !isChangingResolution && !isSwitchingGameWhileWindow) return;
    let cancelled = false;
    setWindowState({ phase: 'loading' });

    fetchWindowedLeaderboard(accessToken, windowResolution, gameKey)
      .then((response) => {
        if (cancelled) return;
        setWindowState({
          phase: 'ready',
          pages: [response.rows],
          requestingUserRow: response.requestingUserRow,
          nextCursor: response.nextCursor,
          hasMore: response.hasMore,
          loadingMore: false,
          loadMoreError: null,
        });
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        if (handleAuthError(error)) return;
        setWindowState({ phase: 'error', message: describeError(error) });
      });

    return () => {
      cancelled = true;
    };
  }, [active, windowResolution, gameKey, accessToken, handleAuthError]);

  async function handleLoadMoreWindow() {
    if (windowState.phase !== 'ready' || windowState.nextCursor == null || windowState.loadingMore) return;
    const cursor = windowState.nextCursor;

    setWindowState((prev) => (prev.phase === 'ready' ? { ...prev, loadingMore: true, loadMoreError: null } : prev));

    try {
      const response = await fetchWindowedLeaderboard(accessToken, windowResolution, gameKey, cursor);
      setWindowState((prev) => {
        if (prev.phase !== 'ready') return prev;
        return {
          ...prev,
          pages: [...prev.pages, response.rows],
          requestingUserRow: response.requestingUserRow,
          nextCursor: response.nextCursor,
          hasMore: response.hasMore,
          loadingMore: false,
          loadMoreError: null,
        };
      });
    } catch (error) {
      if (handleAuthError(error)) return;
      setWindowState((prev) =>
        prev.phase === 'ready' ? { ...prev, loadingMore: false, loadMoreError: describeError(error) } : prev,
      );
    }
  }

  if (!active) return null;

  return (
    <>
      {/* REQ-405: a secondary, nested tab row — same role="tab"/
          aria-selected pattern as the top-level scope tabs in
          LeaderboardScreen.tsx, styled as a nested/secondary row (smaller,
          no bottom border of its own) rather than a second, visually-
          competing tab bar. */}
      <div className="leaderboard-screen__window-tabs" role="tablist" aria-label="Time window">
        {WINDOW_RESOLUTIONS.map(({ value, label }) => (
          <button
            key={value}
            type="button"
            role="tab"
            aria-selected={windowResolution === value}
            className={`leaderboard-screen__window-tab ${windowResolution === value ? 'leaderboard-screen__window-tab--active' : ''}`}
            onClick={() => setWindowResolution(value)}
          >
            {label}
          </button>
        ))}
      </div>
      {windowState.phase === 'idle' || windowState.phase === 'loading' ? (
        <p className="leaderboard-screen__status">Loading this window’s leaderboard…</p>
      ) : windowState.phase === 'error' ? (
        <p className="leaderboard-screen__status leaderboard-screen__status--error">{windowState.message}</p>
      ) : (
        <LeaderboardRowsList
          rows={windowState.pages.flat()}
          requestingUserRow={windowState.requestingUserRow}
          emptyMessage="No one scored in this window yet."
          hasMore={windowState.hasMore}
          loadingMore={windowState.loadingMore}
          loadMoreError={windowState.loadMoreError}
          onLoadMore={handleLoadMoreWindow}
          provisional={false}
          onSelectPlayer={onSelectPlayer}
        />
      )}
    </>
  );
}

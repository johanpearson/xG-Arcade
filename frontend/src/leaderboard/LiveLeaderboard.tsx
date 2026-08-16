import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { fetchActiveRoundLeaderboard } from '../lib/leaderboard';
import { LeaderboardRowsList, type RowsReadyState } from './LeaderboardRowsList';
import type { GameKey } from './LeaderboardScreen';

// REQ-407/ADR-0031 (S-053): the active round's own leaderboard —
// participant-only, recomputed live on every read, no snapshot/cache. 'idle'
// means the scope has never been selected yet (no fetch made); it's fetched
// once on first selection, not eagerly on mount, since ADR-0031 makes this
// read materially more expensive than the all-time one.
type LiveState =
  | { phase: 'idle' }
  | { phase: 'loading' }
  | { phase: 'no-active-round' }
  | { phase: 'error'; message: string }
  | ({ phase: 'ready' } & RowsReadyState);

export interface LiveLeaderboardProps {
  accessToken: string;
  gameKey: GameKey;
  onAuthError: () => void;
  // Whether "Current Round" is the currently selected scope tab. This
  // component is always mounted alongside the other three scopes (see
  // LeaderboardScreen.tsx) so its own state survives a switch away and
  // back — `active` (rather than unmount/remount) is what drives the
  // "fetch on entry, refetch on every re-entry" effect below.
  active: boolean;
}

// REQ-406/407/408/405 (S-053/S-054/S-027, split out of LeaderboardScreen.tsx
// in S-121): REQ-407's standalone active-round scope.
export function LiveLeaderboard({ accessToken, gameKey, onAuthError, active }: LiveLeaderboardProps) {
  const [liveState, setLiveState] = useState<LiveState>({ phase: 'idle' });

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

  // Fetched on every *transition into* this scope — not eagerly on mount,
  // and not on a poll interval, unlike the all-time scope. ADR-0031 makes
  // this read materially more expensive (every participant × every
  // active-round cell, recomputed in full on every call), so this only ever
  // fetches on an explicit action (selecting the tab, or "Load more"), never
  // ambiently in the background.
  //
  // Guarded by comparing against the *previous* `active` value (tracked in a
  // ref), not `liveState.phase` in the dependency array: the
  // `setLiveState({ phase: 'loading' })` call below changes that phase,
  // which would otherwise re-trigger this very effect (cleanup — setting
  // `cancelled = true` — racing the in-flight fetch's own resolution) before
  // the fetch had a chance to complete. Tracking the previous `active` value
  // instead means the effect still only fires on a genuine transition into
  // this scope, but — unlike a permanent "have we ever fetched" latch —
  // fires again every time the player re-enters "live" after visiting a
  // different scope, which is the whole point of REQ-407's "check back once
  // one starts" / "come back to see the update" promise: a stale response
  // with no visual staleness indicator would otherwise sit there for the
  // component's entire mounted lifetime. Re-entry deliberately shows the
  // loading state again (below), rather than leaving the previous, possibly
  // stale rows on screen while fetching silently — for a scope whose whole
  // value proposition is "check back for something more current," a brief
  // loading flash is the more honest signal than quietly leaving stale data
  // up with no cue that a refresh is even happening.
  //
  // REQ-410 (S-087): switching games while already on "live" is a second,
  // equally real reason to re-fetch — same ref-comparison approach, this
  // time against the previous `gameKey`, so a game switch re-triggers the
  // fetch exactly like a fresh tab entry would, without requiring the
  // player to leave and re-enter the "live" scope tab first.
  const prevActiveRef = useRef(active);
  const prevGameKeyForLiveRef = useRef<GameKey>(gameKey);
  useEffect(() => {
    const isEnteringLive = active && !prevActiveRef.current;
    const isSwitchingGameWhileLive = active && prevGameKeyForLiveRef.current !== gameKey;
    prevActiveRef.current = active;
    prevGameKeyForLiveRef.current = gameKey;
    if (!isEnteringLive && !isSwitchingGameWhileLive) return;
    let cancelled = false;
    setLiveState({ phase: 'loading' });

    fetchActiveRoundLeaderboard(accessToken, gameKey)
      .then((response) => {
        if (cancelled) return;
        setLiveState({
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
        // REQ-407: "no active round right now" (404) is a real, expected
        // state — a plain informational empty-state, not a red error banner
        // (mirrors fetchCurrentRound/GridScreen's existing 404-as-empty-state
        // idiom for the exact same underlying situation).
        if (error instanceof ApiError && error.status === 404) {
          setLiveState({ phase: 'no-active-round' });
          return;
        }
        setLiveState({ phase: 'error', message: describeError(error) });
      });

    return () => {
      cancelled = true;
    };
  }, [active, gameKey, accessToken, handleAuthError]);

  async function handleLoadMoreLive() {
    if (liveState.phase !== 'ready' || liveState.nextCursor == null || liveState.loadingMore) return;
    const cursor = liveState.nextCursor;

    setLiveState((prev) => (prev.phase === 'ready' ? { ...prev, loadingMore: true, loadMoreError: null } : prev));

    try {
      const response = await fetchActiveRoundLeaderboard(accessToken, gameKey, cursor);
      setLiveState((prev) => {
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
      setLiveState((prev) =>
        prev.phase === 'ready' ? { ...prev, loadingMore: false, loadMoreError: describeError(error) } : prev,
      );
    }
  }

  if (!active) return null;

  if (liveState.phase === 'idle' || liveState.phase === 'loading') {
    return <p className="leaderboard-screen__status">Loading the current round’s leaderboard…</p>;
  }
  if (liveState.phase === 'error') {
    return <p className="leaderboard-screen__status leaderboard-screen__status--error">{liveState.message}</p>;
  }
  // REQ-407: requesting the active-round scope when no round is active is a
  // real, expected state (mirrors REQ-303's existing "no active round"
  // pattern) — a plain informational empty-state, never a red error.
  if (liveState.phase === 'no-active-round') {
    return <p className="leaderboard-screen__empty">No round is currently active — check back once one starts.</p>;
  }
  return (
    <>
      {/* REQ-407 (ADR-0031): visibly, unmistakably provisional — same
          "estimated … can still change" framing ScoringExplainer.tsx
          already uses for a single cell's live point value, stated once
          here at the scope level in addition to each row's own
          "~N pts estimated" text below. */}
      <p className="leaderboard-screen__scope-note">Live — estimated, can still change until the round closes.</p>
      <LeaderboardRowsList
        rows={liveState.pages.flat()}
        requestingUserRow={liveState.requestingUserRow}
        emptyMessage="No one has played this round yet — be the first."
        hasMore={liveState.hasMore}
        loadingMore={liveState.loadingMore}
        loadMoreError={liveState.loadMoreError}
        onLoadMore={handleLoadMoreLive}
        provisional
      />
    </>
  );
}

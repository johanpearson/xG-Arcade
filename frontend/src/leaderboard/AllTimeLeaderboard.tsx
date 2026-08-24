import { useCallback, useEffect, useState } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { fetchLeaderboard } from '../lib/leaderboard';
import { LeaderboardRowsList, type RowsReadyState } from './LeaderboardRowsList';
import type { GameKey } from './LeaderboardScreen';

// REQ-607 (S-034, refactored per quality-architect's S-034 review): `pages`
// is one entry per page loaded so far, in load order. `pages[0]` is always
// what the 15s poll replaces wholesale (see the effect below); `pages[1+]`
// are appended, one per "Load more" click, and never touched by the poll.
// Rendering flattens `pages` back into a single in-rank-order list.
type ReadyState = { phase: 'ready' } & RowsReadyState;

type LoadState = { phase: 'loading' } | { phase: 'error'; message: string } | ReadyState;

// Rows shown here are already REQ-401/404's ranked totals — REQ-409's
// median of each player's locked per-round points (S-060), never
// in-progress/live points (see REQ-205/S-018's "provisional, never a
// promise" rule) — polling keeps that ranking current as rounds close
// elsewhere, it does not fold live points into it.
const REFRESH_INTERVAL_MS = 15_000;

export interface AllTimeLeaderboardProps {
  accessToken: string;
  gameKey: GameKey;
  onAuthError: () => void;
  // Whether the "All-time" scope tab is the currently selected one — this
  // component is always mounted (see the poll effect's own comment below
  // for why), and only its rendered *output* is gated by this flag; its
  // fetch/poll lifecycle runs regardless.
  active: boolean;
  // REQ-411 (S-179): threaded straight through to LeaderboardRowsList — see
  // that component's own doc comment for the optional-prop/backward-compat
  // reasoning and the "why every row, including your own" judgement call.
  onSelectPlayer?: (userId: string, displayName: string) => void;
}

// REQ-406/407/408/405 (S-053/S-054/S-027, split out of LeaderboardScreen.tsx
// in S-121): the global, all-time leaderboard scope — REQ-401/404's ranked
// totals, REQ-409's median-per-qualifying-round score (>= 5 qualifying
// rounds, S-060), 15s-polled while mounted, with REQ-607's cursor-based
// "Load more" pagination. Deliberately does NOT gate its fetch/poll effect
// on `active`: unlike the live/past/window scopes below, this scope's own
// existing behavior (predating the scope selector entirely) polls
// regardless of which scope tab is currently selected, so a player who
// switches away and back always sees an up-to-date all-time leaderboard
// without waiting for a fresh fetch — see the "REQ406/407/408: the all-time
// scope keeps its own existing 15s poll/'Load more' behavior after
// switching scopes and back" test.
export function AllTimeLeaderboard({ accessToken, gameKey, onAuthError, active, onSelectPlayer }: AllTimeLeaderboardProps) {
  const [state, setState] = useState<LoadState>({ phase: 'loading' });

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

  useEffect(() => {
    let cancelled = false;
    let timeoutId: number | undefined;

    // showLoadingState is only true for the initial mount fetch — a
    // background poll tick must never flash the "Loading…" state over an
    // already-rendered leaderboard, and a transient poll failure must never
    // replace a good, already-displayed leaderboard with an error message.
    //
    // Self-rescheduling via setTimeout (rather than setInterval) rather than
    // a single "sequencing" pass, since it guarantees only one fetch is ever
    // in flight — the next poll is scheduled only after the previous one
    // resolves, so a slow response can never overlap with, or be overtaken
    // by, a later one.
    //
    // REQ-607: the poll always re-fetches only page 1 (cursor omitted) at
    // the default pageSize — it never re-fetches pages loaded via "Load
    // more". When additional pages are already loaded, the poll's page-1
    // response replaces `pages[0]` wholesale; the pagination frontier
    // (`nextCursor`/`hasMore` for the "Load more" button) is left alone in
    // that case, since it reflects the *last* loaded page, not page 1.
    function load(showLoadingState: boolean) {
      if (showLoadingState) setState({ phase: 'loading' });

      fetchLeaderboard(accessToken, gameKey)
        .then((response) => {
          if (cancelled) return;
          setState((prev) => {
            const prevReady = prev.phase === 'ready' ? prev : null;
            const freshPage1 = [...response.rows];
            const trailingPages = prevReady ? prevReady.pages.slice(1) : [];

            // A player's rank can cross the page-1/page-2 boundary between
            // poll ticks (round close shifting FinalPoints totals, or a
            // REQ-710 account deletion). Replacing `pages[0]` wholesale while
            // leaving `pages[1:]` untouched can therefore put the same
            // userId in both the fresh page 1 and a stale trailing page —
            // drop it from the trailing pages so that player appears once,
            // in their fresher page-1 position, instead of duplicated (and
            // instead of colliding on the row's React `key`).
            const freshIds = new Set(freshPage1.map((row) => row.userId));
            const dedupedTrailingPages = trailingPages.map((page) =>
              page.filter((row) => !freshIds.has(row.userId)),
            );

            // Trailing pages beyond page 1 exist only when prevReady already
            // had one loaded, so the frontier (nextCursor/hasMore for "Load
            // more") can only ever be carried over from a ready prev state —
            // checking prevReady directly in the condition (rather than a
            // separate boolean) lets TypeScript narrow it without an
            // assertion.
            const frontier =
              prevReady && trailingPages.length > 0
                ? { nextCursor: prevReady.nextCursor, hasMore: prevReady.hasMore }
                : { nextCursor: response.nextCursor, hasMore: response.hasMore };

            return {
              phase: 'ready',
              pages: [freshPage1, ...dedupedTrailingPages],
              requestingUserRow: response.requestingUserRow,
              ...frontier,
              loadingMore: prevReady ? prevReady.loadingMore : false,
              loadMoreError: prevReady ? prevReady.loadMoreError : null,
            };
          });
        })
        .catch((error: unknown) => {
          if (cancelled) return;
          if (handleAuthError(error)) return;
          if (showLoadingState) {
            setState({ phase: 'error', message: describeError(error) });
          } else {
            // Never replace an already-displayed leaderboard with an error
            // over a transient background hiccup, but a failure with no
            // trace anywhere is hard to debug — at least log it.
            console.error('Leaderboard background refresh failed:', error);
          }
        })
        .finally(() => {
          if (!cancelled) timeoutId = window.setTimeout(() => load(false), REFRESH_INTERVAL_MS);
        });
    }

    load(true);

    return () => {
      cancelled = true;
      if (timeoutId != null) window.clearTimeout(timeoutId);
    };
    // REQ-410 (S-087): `gameKey` is a dependency so switching games restarts
    // this effect exactly like a fresh mount would — the cleanup above
    // cancels the pending poll timeout for the old game, and the effect
    // re-running immediately calls `load(true)`, showing the loading state
    // once and fetching the newly selected game's all-time leaderboard
    // (never blending pages/frontier across games, since `load`'s own
    // `prevReady` read above only sees this fresh 'loading' state, not the
    // old game's trailing pages).
  }, [accessToken, gameKey, onAuthError, handleAuthError]);

  // REQ-607: "Load more" is a separate, explicit, user-triggered action —
  // it appends the next page on top of whatever's already loaded and never
  // touches the 15s poll's page-1 state (beyond the trailing-rows handoff
  // above).
  async function handleLoadMore() {
    if (state.phase !== 'ready' || state.nextCursor == null || state.loadingMore) return;
    const cursor = state.nextCursor;

    setState((prev) => (prev.phase === 'ready' ? { ...prev, loadingMore: true, loadMoreError: null } : prev));

    try {
      const response = await fetchLeaderboard(accessToken, gameKey, cursor);
      setState((prev) => {
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
      setState((prev) =>
        prev.phase === 'ready' ? { ...prev, loadingMore: false, loadMoreError: describeError(error) } : prev,
      );
    }
  }

  if (!active) return null;

  if (state.phase === 'loading') {
    return <p className="leaderboard-screen__status">Loading the leaderboard…</p>;
  }
  if (state.phase === 'error') {
    return <p className="leaderboard-screen__status leaderboard-screen__status--error">{state.message}</p>;
  }
  return (
    <LeaderboardRowsList
      rows={state.pages.flat()}
      requestingUserRow={state.requestingUserRow}
      emptyMessage="No scores yet — be the first to play a round."
      hasMore={state.hasMore}
      loadingMore={state.loadingMore}
      loadMoreError={state.loadMoreError}
      onLoadMore={handleLoadMore}
      provisional={false}
      onSelectPlayer={onSelectPlayer}
    />
  );
}

import { useEffect, useState } from 'react';
import { ApiError, describeError, fetchLeaderboard } from '../lib/api';
import type { LeaderboardRow } from '../lib/types';
import './LeaderboardScreen.css';

export interface LeaderboardScreenProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-607 (S-034): `rows` is the flattened, in-rank-order list across
// however many pages the player has loaded so far — `firstPageCount` marks
// where page 1 ends within it, since the 15s poll only ever re-fetches page
// 1 (see the effect below) and needs to know which prefix of `rows` to
// replace without disturbing any pages loaded via "Load more".
type ReadyState = {
  phase: 'ready';
  rows: LeaderboardRow[];
  firstPageCount: number;
  requestingUserRow: LeaderboardRow | null;
  nextCursor: number | null;
  hasMore: boolean;
  loadingMore: boolean;
  loadMoreError: string | null;
};

type LoadState = { phase: 'loading' } | { phase: 'error'; message: string } | ReadyState;

// Rows shown here are already REQ-401/404's locked totals
// (SUM(FinalPoints), never in-progress/live points — see REQ-205/S-018's
// "provisional, never a promise" rule) — polling keeps that locked total
// current as rounds close elsewhere, it does not add live points into it.
const REFRESH_INTERVAL_MS = 15_000;

// SCREEN-03 (REQ-401/404's Tier 0 slice): the global league is the only one
// that exists yet — custom leagues' "[My League ▾] [+ New]" tabs (REQ-402-
// 404) are deferred per MVP-SCOPE.md, so this shows only the Global list,
// no tab switcher.
export function LeaderboardScreen({ accessToken, onAuthError }: LeaderboardScreenProps) {
  const [state, setState] = useState<LoadState>({ phase: 'loading' });

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
    // response replaces just that leading prefix of `rows`; the pagination
    // frontier (`nextCursor`/`hasMore` for the "Load more" button) is left
    // alone in that case, since it reflects the *last* loaded page, not
    // page 1.
    function load(showLoadingState: boolean) {
      if (showLoadingState) setState({ phase: 'loading' });

      fetchLeaderboard(accessToken)
        .then((response) => {
          if (cancelled) return;
          setState((prev) => {
            const trailingRows = prev.phase === 'ready' ? prev.rows.slice(prev.firstPageCount) : [];
            const isFrontierStillPageOne = trailingRows.length === 0;
            return {
              phase: 'ready',
              rows: [...response.rows, ...trailingRows],
              firstPageCount: response.rows.length,
              requestingUserRow: response.requestingUserRow,
              nextCursor: isFrontierStillPageOne
                ? response.nextCursor
                : prev.phase === 'ready'
                  ? prev.nextCursor
                  : response.nextCursor,
              hasMore: isFrontierStillPageOne
                ? response.hasMore
                : prev.phase === 'ready'
                  ? prev.hasMore
                  : response.hasMore,
              loadingMore: prev.phase === 'ready' ? prev.loadingMore : false,
              loadMoreError: prev.phase === 'ready' ? prev.loadMoreError : null,
            };
          });
        })
        .catch((error: unknown) => {
          if (cancelled) return;
          if (error instanceof ApiError && error.status === 401) {
            onAuthError();
            return;
          }
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
  }, [accessToken, onAuthError]);

  // REQ-607: "Load more" is a separate, explicit, user-triggered action —
  // it appends the next page on top of whatever's already loaded and never
  // touches the 15s poll's page-1 state (beyond the trailing-rows handoff
  // above).
  async function handleLoadMore() {
    if (state.phase !== 'ready' || state.nextCursor == null || state.loadingMore) return;
    const cursor = state.nextCursor;

    setState((prev) => (prev.phase === 'ready' ? { ...prev, loadingMore: true, loadMoreError: null } : prev));

    try {
      const response = await fetchLeaderboard(accessToken, cursor);
      setState((prev) => {
        if (prev.phase !== 'ready') return prev;
        return {
          ...prev,
          rows: [...prev.rows, ...response.rows],
          requestingUserRow: response.requestingUserRow,
          nextCursor: response.nextCursor,
          hasMore: response.hasMore,
          loadingMore: false,
          loadMoreError: null,
        };
      });
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        onAuthError();
        return;
      }
      setState((prev) =>
        prev.phase === 'ready' ? { ...prev, loadingMore: false, loadMoreError: describeError(error) } : prev,
      );
    }
  }

  if (state.phase === 'loading') {
    return <p className="leaderboard-screen__status">Loading the leaderboard…</p>;
  }

  if (state.phase === 'error') {
    return <p className="leaderboard-screen__status leaderboard-screen__status--error">{state.message}</p>;
  }

  // REQ-607: when the requesting user's row isn't among the currently
  // loaded rows (they're off-page), pin a distinct footer row with their
  // real global rank/points so they always know their standing without
  // loading more pages. When it IS already visible in the list, skip the
  // footer — showing both would be a redundant duplicate.
  const showYouFooter =
    state.requestingUserRow !== null && !state.rows.some((row) => row.userId === state.requestingUserRow!.userId);

  return (
    <div className="leaderboard-screen">
      <div className="leaderboard-screen__header">
        <h2>Global leaderboard</h2>
        {/* ADR-0021/design-document.md SCREEN-03: scored like golf — this
            corrects the natural "higher number = better" assumption before
            a player reads any rank. Must never be omitted or left implicit
            in the ranking order alone. */}
        <p className="leaderboard-screen__subtitle">Lowest total wins</p>
      </div>
      {state.rows.length === 0 ? (
        // design-document.md §5: "empty states are invitations."
        <p className="leaderboard-screen__empty">No scores yet — be the first to play a round.</p>
      ) : (
        <>
          <ol className="leaderboard-screen__list">
            {state.rows.map((row) => (
              <li
                key={row.userId}
                className={`leaderboard-screen__row ${row.isRequestingUser ? 'leaderboard-screen__row--you' : ''}`}
              >
                <span className="leaderboard-screen__rank mono-figure">{row.rank}</span>
                <span className="leaderboard-screen__name">{row.displayName}</span>
                <span className="leaderboard-screen__points mono-figure">{row.totalPoints} pts</span>
                {/* Text, not color-only (design-document.md §6). */}
                {row.isRequestingUser && <span className="leaderboard-screen__you-tag">you</span>}
              </li>
            ))}
          </ol>
          {state.hasMore && (
            <button
              type="button"
              className="leaderboard-screen__load-more"
              onClick={handleLoadMore}
              disabled={state.loadingMore}
            >
              {state.loadingMore ? 'Loading more…' : 'Load more'}
            </button>
          )}
          {state.loadMoreError && (
            <p className="leaderboard-screen__load-more-error">{state.loadMoreError}</p>
          )}
        </>
      )}
      {showYouFooter && state.requestingUserRow && (
        <div className="leaderboard-screen__you-footer">
          <span className="leaderboard-screen__rank mono-figure">{state.requestingUserRow.rank}</span>
          <span className="leaderboard-screen__name">{state.requestingUserRow.displayName}</span>
          <span className="leaderboard-screen__points mono-figure">{state.requestingUserRow.totalPoints} pts</span>
          {/* Text, not color-only (design-document.md §6). */}
          <span className="leaderboard-screen__you-tag">you</span>
        </div>
      )}
    </div>
  );
}

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  describeError,
  fetchActiveRoundLeaderboard,
  fetchClosedRoundLeaderboard,
  fetchClosedRounds,
  fetchLeaderboard,
  fetchWindowedLeaderboard,
} from '../lib/api';
import type { WindowResolution } from '../lib/api';
import type { ClosedRoundSummary, LeaderboardRow } from '../lib/types';
// REQ-213 (S-068): the leaderboard's own `(ⓘ)` entry point opens this exact
// same explainer GridScreen.tsx already uses — no new component, no new
// props, no leaderboard-specific content (see ScoringExplainer.tsx's own
// 2026-07-21 doc comment and REQ-213's matching acceptance criteria).
import { ScoringExplainer } from '../grid/ScoringExplainer';
import './LeaderboardScreen.css';

export interface LeaderboardScreenProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-406/407/408/405 (S-053/S-054/S-027): a new, separate scope selector on
// the same SCREEN-03 screen — distinct from custom leagues' own "[Global]
// [My League ▾] [+ New]" tabs design-document.md's current SCREEN-03 mock
// describes (custom leagues exist as of REQ-402/403/S-063, but this screen
// still only reads the global league; this selector exists alongside a
// future league picker, not instead of it). 'all-time' is REQ-401/404's
// global leaderboard, ranked by REQ-409's median-per-qualifying-round score
// (>= 5 qualifying rounds) as of S-060 — no longer a raw locked-points sum.
// 'live' is REQ-407's standalone active-round scope. 'past' is REQ-408's
// browsable closed-round list + drill-in. 'window' is REQ-405's
// calendar-aligned (never rolling) round/week/month/year leaderboard.
type Scope = 'all-time' | 'live' | 'past' | 'window';

// ---- Shared row shape used by all three scopes' "ready" states ----------

type RowsReadyState = {
  pages: LeaderboardRow[][];
  requestingUserRow: LeaderboardRow | null;
  nextCursor: number | null;
  hasMore: boolean;
  loadingMore: boolean;
  loadMoreError: string | null;
};

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

type PastRoundsListReadyState = {
  pages: ClosedRoundSummary[][];
  nextCursor: number | null;
  hasMore: boolean;
  loadingMore: boolean;
  loadMoreError: string | null;
};

// REQ-408 (S-054): the round-selection list, fetched once on first
// selection of the "past rounds" scope (same 'idle' reasoning as LiveState).
type PastListState =
  | { phase: 'idle' }
  | { phase: 'loading' }
  | { phase: 'error'; message: string }
  | ({ phase: 'ready' } & PastRoundsListReadyState);

// REQ-408: one selected closed round's locked, final leaderboard —
// "not-found" and "not-closed" are distinct, real states (a bad round id vs.
// a real but still-active/upcoming one), never squashed into one generic
// error message.
type PastDetailState =
  | { phase: 'loading' }
  | { phase: 'not-found' }
  | { phase: 'not-closed' }
  | { phase: 'error'; message: string }
  | ({ phase: 'ready' } & RowsReadyState);

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
// loading/error/ready shape as `LiveState`, minus a "no active round"
// case: unlike REQ-407's active-round scope, a window always resolves to
// *some* ranked list (possibly empty, handled via `emptyMessage` below),
// never a 404.
type WindowState =
  | { phase: 'idle' }
  | { phase: 'loading' }
  | { phase: 'error'; message: string }
  | ({ phase: 'ready' } & RowsReadyState);

// REQ-406/407/408 (ADR-0031's "never presented as if it were final/locked"
// rule): shared row/footer rendering for all three scopes, so the <li> row
// markup isn't triplicated. `provisional` controls only the points text —
// a live total renders with the same "~N pts estimated" wording
// GridScreen.tsx/CellState.tsx already established for a single cell's live
// point value (S-018/REQ-204), applied per row here; a locked total (all-time
// or a past closed round) renders as plain "N pts", unchanged from before.
function formatPoints(points: number, provisional: boolean): string {
  return provisional ? `~${points} pts estimated` : `${points} pts`;
}

function LeaderboardRowsList({
  rows,
  requestingUserRow,
  emptyMessage,
  hasMore,
  loadingMore,
  loadMoreError,
  onLoadMore,
  provisional,
}: {
  rows: LeaderboardRow[];
  requestingUserRow: LeaderboardRow | null;
  emptyMessage: string;
  hasMore: boolean;
  loadingMore: boolean;
  loadMoreError: string | null;
  onLoadMore: () => void;
  provisional: boolean;
}) {
  // REQ-607: when the requesting user's row isn't among the currently
  // loaded rows (they're off-page, or — for the live scope — simply not a
  // participant), pin a distinct footer row with their real rank/points so
  // they always know their standing without loading more pages. When it IS
  // already visible in the list, skip the footer — showing both would be a
  // redundant duplicate.
  const showYouFooter =
    requestingUserRow !== null && !rows.some((row) => row.userId === requestingUserRow.userId);

  return (
    <>
      {rows.length === 0 ? (
        // design-document.md §5: "empty states are invitations."
        <p className="leaderboard-screen__empty">{emptyMessage}</p>
      ) : (
        <>
          <ol className="leaderboard-screen__list">
            {rows.map((row) => (
              <li
                key={row.userId}
                className={`leaderboard-screen__row ${row.isRequestingUser ? 'leaderboard-screen__row--you' : ''}`}
              >
                <span className="leaderboard-screen__rank mono-figure">{row.rank}</span>
                <span className="leaderboard-screen__name">{row.displayName}</span>
                <span className="leaderboard-screen__points mono-figure">
                  {formatPoints(row.totalPoints, provisional)}
                </span>
                {/* Text, not color-only (design-document.md §6). */}
                {row.isRequestingUser && <span className="leaderboard-screen__you-tag">you</span>}
              </li>
            ))}
          </ol>
          {hasMore && (
            <button
              type="button"
              className="leaderboard-screen__load-more"
              onClick={onLoadMore}
              disabled={loadingMore}
            >
              {loadingMore ? 'Loading more…' : 'Load more'}
            </button>
          )}
          {loadMoreError && <p className="leaderboard-screen__load-more-error">{loadMoreError}</p>}
        </>
      )}
      {showYouFooter && requestingUserRow && (
        <div className="leaderboard-screen__you-footer">
          <span className="leaderboard-screen__rank mono-figure">{requestingUserRow.rank}</span>
          <span className="leaderboard-screen__name">{requestingUserRow.displayName}</span>
          <span className="leaderboard-screen__points mono-figure">
            {formatPoints(requestingUserRow.totalPoints, provisional)}
          </span>
          {/* Text, not color-only (design-document.md §6). */}
          <span className="leaderboard-screen__you-tag">you</span>
        </div>
      )}
    </>
  );
}

// SCREEN-03: this screen still only reads the global league — custom
// leagues (REQ-402/403/S-063) can now be created/joined via LeaguesScreen,
// but REQ-404's own "[My League ▾] [+ New]" tab switcher and per-league
// leaderboard reads here remain unbuilt (LeaguesScreen only lists a
// player's own leagues by name/code, no leaderboard rendering). REQ-406/
// 407/408 (S-053/S-054) add the scope selector above instead.
export function LeaderboardScreen({ accessToken, onAuthError }: LeaderboardScreenProps) {
  const [scope, setScope] = useState<Scope>('all-time');
  // REQ-213 (S-068): independent of `scope`/every scope's own load state on
  // purpose — same reasoning as GridScreen.tsx's `explainerOpen` being
  // independent of `activeCell` (SCREEN-06's "doesn't discard in-progress
  // state" requirement). Opening/closing this never touches which scope tab
  // is selected, a loaded "Load more" page, or any scope's fetch state.
  const [explainerOpen, setExplainerOpen] = useState(false);

  const [state, setState] = useState<LoadState>({ phase: 'loading' });
  const [liveState, setLiveState] = useState<LiveState>({ phase: 'idle' });
  const [pastListState, setPastListState] = useState<PastListState>({ phase: 'idle' });
  const [selectedRound, setSelectedRound] = useState<ClosedRoundSummary | null>(null);
  const [pastDetailState, setPastDetailState] = useState<PastDetailState | null>(null);
  const [windowResolution, setWindowResolution] = useState<WindowResolution>(DEFAULT_WINDOW_RESOLUTION);
  const [windowState, setWindowState] = useState<WindowState>({ phase: 'idle' });

  // Stable across renders (as long as onAuthError itself is) so the effects
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

  // ---- All-time scope (unchanged behavior: 15s poll, "Load more", pinned
  // "you" footer) --------------------------------------------------------

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

      fetchLeaderboard(accessToken)
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
  }, [accessToken, onAuthError, handleAuthError]);

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

  // ---- Live scope (REQ-407/ADR-0031) ------------------------------------

  // Fetched on every *transition into* this scope — not eagerly on mount,
  // and not on a poll interval, unlike the all-time scope above. ADR-0031
  // makes this read materially more expensive (every participant × every
  // active-round cell, recomputed in full on every call), so this only ever
  // fetches on an explicit action (selecting the tab, or "Load more"), never
  // ambiently in the background.
  //
  // Guarded by comparing against the *previous* scope (tracked in a ref),
  // not `liveState.phase` in the dependency array: the
  // `setLiveState({ phase: 'loading' })` call below changes that phase,
  // which would otherwise re-trigger this very effect (cleanup — setting
  // `cancelled = true` — racing the in-flight fetch's own resolution) before
  // the fetch had a chance to complete. Tracking the previous scope instead
  // means the effect still only fires on a genuine tab change, but — unlike
  // a permanent "have we ever fetched" latch — fires again every time the
  // user re-enters "live" after visiting a different scope, which is the
  // whole point of REQ-407's "check back once one starts" / "come back to
  // see the update" promise: a stale response with no visual staleness
  // indicator would otherwise sit there for the component's entire mounted
  // lifetime. Re-entry deliberately shows the loading state again (below),
  // rather than leaving the previous, possibly-stale rows on screen while
  // fetching silently — for a scope whose whole value proposition is "check
  // back for something more current," a brief loading flash is the more
  // honest signal than quietly leaving stale data up with no cue that a
  // refresh is even happening.
  const prevScopeForLiveRef = useRef<Scope>(scope);
  useEffect(() => {
    const isEnteringLive = scope === 'live' && prevScopeForLiveRef.current !== 'live';
    prevScopeForLiveRef.current = scope;
    if (!isEnteringLive) return;
    let cancelled = false;
    setLiveState({ phase: 'loading' });

    fetchActiveRoundLeaderboard(accessToken)
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
  }, [scope, accessToken, handleAuthError]);

  async function handleLoadMoreLive() {
    if (liveState.phase !== 'ready' || liveState.nextCursor == null || liveState.loadingMore) return;
    const cursor = liveState.nextCursor;

    setLiveState((prev) => (prev.phase === 'ready' ? { ...prev, loadingMore: true, loadMoreError: null } : prev));

    try {
      const response = await fetchActiveRoundLeaderboard(accessToken, cursor);
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

  // ---- Previous Rounds scope (REQ-408) -----------------------------------

  // The round-selection list, fetched on every transition into this
  // scope — same "idle until picked" and "re-entry, not a one-time latch"
  // reasoning, and same prev-scope-ref-rather-than-phase-in-deps fix, as the
  // live scope above.
  const prevScopeForPastListRef = useRef<Scope>(scope);
  useEffect(() => {
    const isEnteringPast = scope === 'past' && prevScopeForPastListRef.current !== 'past';
    prevScopeForPastListRef.current = scope;
    if (!isEnteringPast) return;
    let cancelled = false;
    setPastListState({ phase: 'loading' });

    fetchClosedRounds(accessToken)
      .then((response) => {
        if (cancelled) return;
        setPastListState({
          phase: 'ready',
          pages: [response.rounds],
          nextCursor: response.nextCursor,
          hasMore: response.hasMore,
          loadingMore: false,
          loadMoreError: null,
        });
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        if (handleAuthError(error)) return;
        setPastListState({ phase: 'error', message: describeError(error) });
      });

    return () => {
      cancelled = true;
    };
  }, [scope, accessToken, handleAuthError]);

  async function handleLoadMoreRoundList() {
    if (pastListState.phase !== 'ready' || pastListState.nextCursor == null || pastListState.loadingMore) return;
    const cursor = pastListState.nextCursor;

    setPastListState((prev) =>
      prev.phase === 'ready' ? { ...prev, loadingMore: true, loadMoreError: null } : prev,
    );

    try {
      const response = await fetchClosedRounds(accessToken, cursor);
      setPastListState((prev) => {
        if (prev.phase !== 'ready') return prev;
        return {
          ...prev,
          pages: [...prev.pages, response.rounds],
          nextCursor: response.nextCursor,
          hasMore: response.hasMore,
          loadingMore: false,
          loadMoreError: null,
        };
      });
    } catch (error) {
      if (handleAuthError(error)) return;
      setPastListState((prev) =>
        prev.phase === 'ready' ? { ...prev, loadingMore: false, loadMoreError: describeError(error) } : prev,
      );
    }
  }

  function handleSelectRound(round: ClosedRoundSummary) {
    setSelectedRound(round);
    setPastDetailState({ phase: 'loading' });

    fetchClosedRoundLeaderboard(accessToken, round.roundId)
      .then((response) => {
        setPastDetailState({
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
        if (handleAuthError(error)) return;
        // REQ-408: "not found" (404) and "not closed yet" (409) are two
        // distinct, real states — never squashed into one generic error.
        if (error instanceof ApiError && error.status === 404) {
          setPastDetailState({ phase: 'not-found' });
          return;
        }
        if (error instanceof ApiError && error.status === 409) {
          setPastDetailState({ phase: 'not-closed' });
          return;
        }
        setPastDetailState({ phase: 'error', message: describeError(error) });
      });
  }

  function handleBackToRoundList() {
    setSelectedRound(null);
    setPastDetailState(null);
  }

  async function handleLoadMoreRoundDetail() {
    if (
      !selectedRound ||
      !pastDetailState ||
      pastDetailState.phase !== 'ready' ||
      pastDetailState.nextCursor == null ||
      pastDetailState.loadingMore
    ) {
      return;
    }
    const cursor = pastDetailState.nextCursor;

    setPastDetailState((prev) =>
      prev && prev.phase === 'ready' ? { ...prev, loadingMore: true, loadMoreError: null } : prev,
    );

    try {
      const response = await fetchClosedRoundLeaderboard(accessToken, selectedRound.roundId, cursor);
      setPastDetailState((prev) => {
        if (!prev || prev.phase !== 'ready') return prev;
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
      setPastDetailState((prev) =>
        prev && prev.phase === 'ready' ? { ...prev, loadingMore: false, loadMoreError: describeError(error) } : prev,
      );
    }
  }

  // ---- Time Windows scope (REQ-405/S-027) -------------------------------

  // Fetched on every transition into this scope, AND on every change of
  // `windowResolution` while already in it (switching the round/week/month/
  // year sub-tab) — both are real reasons to issue a fresh request. Same
  // prev-ref-comparison-rather-than-phase-in-deps fix as the live/past
  // scopes above: `setWindowState({ phase: 'loading' })` below changes
  // `windowState.phase`, which would otherwise re-trigger this very effect
  // (cleanup racing the in-flight fetch) before the fetch had a chance to
  // resolve — comparing against the *previous* scope and resolution (both
  // tracked in refs) instead means the effect only fires on a genuine tab
  // or sub-tab change. Same "loading flash on re-entry" reasoning as the
  // live scope: re-entering "window" (or re-picking the same sub-tab after
  // leaving and coming back) deliberately shows the loading state again
  // rather than quietly leaving the previous resolution's rows on screen.
  const prevScopeForWindowRef = useRef<Scope>(scope);
  const prevResolutionForWindowRef = useRef<WindowResolution>(windowResolution);
  useEffect(() => {
    const isEnteringWindow = scope === 'window' && prevScopeForWindowRef.current !== 'window';
    const isChangingResolution = scope === 'window' && prevResolutionForWindowRef.current !== windowResolution;
    prevScopeForWindowRef.current = scope;
    prevResolutionForWindowRef.current = windowResolution;
    if (!isEnteringWindow && !isChangingResolution) return;
    let cancelled = false;
    setWindowState({ phase: 'loading' });

    fetchWindowedLeaderboard(accessToken, windowResolution)
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
  }, [scope, windowResolution, accessToken, handleAuthError]);

  async function handleLoadMoreWindow() {
    if (windowState.phase !== 'ready' || windowState.nextCursor == null || windowState.loadingMore) return;
    const cursor = windowState.nextCursor;

    setWindowState((prev) => (prev.phase === 'ready' ? { ...prev, loadingMore: true, loadMoreError: null } : prev));

    try {
      const response = await fetchWindowedLeaderboard(accessToken, windowResolution, cursor);
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

  // ---- Rendering ----------------------------------------------------------

  function renderAllTime() {
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
      />
    );
  }

  function renderLive() {
    if (liveState.phase === 'idle' || liveState.phase === 'loading') {
      return <p className="leaderboard-screen__status">Loading the current round’s leaderboard…</p>;
    }
    if (liveState.phase === 'error') {
      return <p className="leaderboard-screen__status leaderboard-screen__status--error">{liveState.message}</p>;
    }
    // REQ-407: requesting the active-round scope when no round is active is
    // a real, expected state (mirrors REQ-303's existing "no active round"
    // pattern) — a plain informational empty-state, never a red error.
    if (liveState.phase === 'no-active-round') {
      return (
        <p className="leaderboard-screen__empty">
          No round is currently active — check back once one starts.
        </p>
      );
    }
    return (
      <>
        {/* REQ-407 (ADR-0031): visibly, unmistakably provisional — same
            "estimated … can still change" framing ScoringExplainer.tsx
            already uses for a single cell's live point value, stated once
            here at the scope level in addition to each row's own
            "~N pts estimated" text below. */}
        <p className="leaderboard-screen__scope-note">
          Live — estimated, can still change until the round closes.
        </p>
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

  function renderPast() {
    if (selectedRound && pastDetailState) {
      return (
        <>
          <div className="leaderboard-screen__past-detail-header">
            <button
              type="button"
              className="leaderboard-screen__back"
              onClick={handleBackToRoundList}
            >
              Back to previous rounds
            </button>
            <p className="leaderboard-screen__scope-note">Closed {selectedRound.closedAt}</p>
          </div>
          {pastDetailState.phase === 'loading' && (
            <p className="leaderboard-screen__status">Loading this round’s leaderboard…</p>
          )}
          {pastDetailState.phase === 'not-found' && (
            <p className="leaderboard-screen__status leaderboard-screen__status--error">
              This round couldn’t be found.
            </p>
          )}
          {pastDetailState.phase === 'not-closed' && (
            <p className="leaderboard-screen__empty">
              This round hasn’t closed yet — its live leaderboard is under “Current Round.”
            </p>
          )}
          {pastDetailState.phase === 'error' && (
            <p className="leaderboard-screen__status leaderboard-screen__status--error">
              {pastDetailState.message}
            </p>
          )}
          {pastDetailState.phase === 'ready' && (
            <LeaderboardRowsList
              rows={pastDetailState.pages.flat()}
              requestingUserRow={pastDetailState.requestingUserRow}
              emptyMessage="No one scored in this round."
              hasMore={pastDetailState.hasMore}
              loadingMore={pastDetailState.loadingMore}
              loadMoreError={pastDetailState.loadMoreError}
              onLoadMore={handleLoadMoreRoundDetail}
              provisional={false}
            />
          )}
        </>
      );
    }

    if (pastListState.phase === 'idle' || pastListState.phase === 'loading') {
      return <p className="leaderboard-screen__status">Loading previous rounds…</p>;
    }
    if (pastListState.phase === 'error') {
      return (
        <p className="leaderboard-screen__status leaderboard-screen__status--error">{pastListState.message}</p>
      );
    }

    const rounds = pastListState.pages.flat();
    if (rounds.length === 0) {
      return <p className="leaderboard-screen__empty">No rounds have closed yet.</p>;
    }

    return (
      <>
        <ol className="leaderboard-screen__round-list">
          {rounds.map((round) => (
            <li key={round.roundId} className="leaderboard-screen__round-list-item">
              <button
                type="button"
                className="leaderboard-screen__round-list-button"
                onClick={() => handleSelectRound(round)}
              >
                Closed {round.closedAt}
              </button>
            </li>
          ))}
        </ol>
        {pastListState.hasMore && (
          <button
            type="button"
            className="leaderboard-screen__load-more"
            onClick={handleLoadMoreRoundList}
            disabled={pastListState.loadingMore}
          >
            {pastListState.loadingMore ? 'Loading more…' : 'Load more'}
          </button>
        )}
        {pastListState.loadMoreError && (
          <p className="leaderboard-screen__load-more-error">{pastListState.loadMoreError}</p>
        )}
      </>
    );
  }

  function renderWindow() {
    return (
      <>
        {/* REQ-405: a secondary, nested tab row — same role="tab"/
            aria-selected pattern as the top-level scope tabs above, styled
            as a nested/secondary row (smaller, no bottom border of its own)
            rather than a second, visually-competing tab bar. */}
        <div
          className="leaderboard-screen__window-tabs"
          role="tablist"
          aria-label="Time window"
        >
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
          />
        )}
      </>
    );
  }

  return (
    <div className="leaderboard-screen">
      <div className="leaderboard-screen__header">
        <div className="leaderboard-screen__title-row">
          <h2>Global leaderboard</h2>
          {/* REQ-213 (S-068): opens SCREEN-06's general scoring/live-updates
              explainer — same component/content as GridScreen.tsx's entry
              point, reachable regardless of which scope tab is active or
              whether that scope's data is loading, empty, or errored (it
              reads no scope/round state at all). */}
          <button
            type="button"
            className="leaderboard-screen__info-toggle"
            onClick={() => setExplainerOpen(true)}
            aria-label="How scoring works"
          >
            ⓘ
          </button>
        </div>
        {/* ADR-0021/design-document.md SCREEN-03: scored like golf — this
            corrects the natural "higher number = better" assumption before
            a player reads any rank. Must never be omitted or left implicit
            in the ranking order alone. Shown for every scope, since it's a
            property of every ranked list this screen can show. */}
        <p className="leaderboard-screen__subtitle">Lowest total wins</p>
      </div>
      {/* REQ-406/407/408: a new, separate scope selector — distinct from the
          not-yet-built custom-league tabs (design-document.md SCREEN-03). */}
      <div className="leaderboard-screen__scope-tabs" role="tablist" aria-label="Leaderboard scope">
        <button
          type="button"
          role="tab"
          aria-selected={scope === 'all-time'}
          className={`leaderboard-screen__scope-tab ${scope === 'all-time' ? 'leaderboard-screen__scope-tab--active' : ''}`}
          onClick={() => setScope('all-time')}
        >
          All-time
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={scope === 'live'}
          className={`leaderboard-screen__scope-tab ${scope === 'live' ? 'leaderboard-screen__scope-tab--active' : ''}`}
          onClick={() => setScope('live')}
        >
          Current Round
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={scope === 'past'}
          className={`leaderboard-screen__scope-tab ${scope === 'past' ? 'leaderboard-screen__scope-tab--active' : ''}`}
          onClick={() => setScope('past')}
        >
          Previous Rounds
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={scope === 'window'}
          className={`leaderboard-screen__scope-tab ${scope === 'window' ? 'leaderboard-screen__scope-tab--active' : ''}`}
          onClick={() => setScope('window')}
        >
          Time Windows
        </button>
      </div>
      {scope === 'all-time' && renderAllTime()}
      {scope === 'live' && renderLive()}
      {scope === 'past' && renderPast()}
      {scope === 'window' && renderWindow()}
      {explainerOpen && <ScoringExplainer onClose={() => setExplainerOpen(false)} />}
    </div>
  );
}

import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { fetchClosedRoundLeaderboard, fetchClosedRounds } from '../lib/leaderboard';
import type { ClosedRoundSummary } from '../lib/types';
import { LeaderboardRowsList, type RowsReadyState } from './LeaderboardRowsList';
import type { GameKey } from './LeaderboardScreen';

type PastRoundsListReadyState = {
  pages: ClosedRoundSummary[][];
  nextCursor: number | null;
  hasMore: boolean;
  loadingMore: boolean;
  loadMoreError: string | null;
};

// REQ-408 (S-054): the round-selection list, fetched once on first
// selection of the "past rounds" scope (same 'idle' reasoning as
// LiveLeaderboard's own state).
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

export interface PastRoundsLeaderboardProps {
  accessToken: string;
  gameKey: GameKey;
  onAuthError: () => void;
  // Whether "Previous Rounds" is the currently selected scope tab. This
  // component is always mounted alongside the other three scopes (see
  // LeaderboardScreen.tsx) so a drilled-into round's detail survives a
  // switch away and back — `active` (rather than unmount/remount) is what
  // drives the "fetch on entry, refetch on every re-entry" effect below.
  active: boolean;
  // REQ-1210/ADR-0082: seeds a direct jump straight into this specific
  // round's detail the first time this scope becomes active, bypassing the
  // round-selection list entirely — used by the round-completion banner's
  // leaderboard link once the completed round has already closed (see
  // LeaderboardScreen.tsx's own doc comment on `initialRoundId`). Consumed
  // exactly once (the ref-guarded effect below) so a later "Back to
  // previous rounds" click, or re-entering this scope normally afterward,
  // never re-triggers the jump.
  initialRoundId?: string;
}

// REQ-406/407/408/405 (S-053/S-054/S-027, split out of LeaderboardScreen.tsx
// in S-121): REQ-408's browsable closed-round list + drill-in detail.
export function PastRoundsLeaderboard({
  accessToken,
  gameKey,
  onAuthError,
  active,
  initialRoundId,
}: PastRoundsLeaderboardProps) {
  const [pastListState, setPastListState] = useState<PastListState>({ phase: 'idle' });
  // REQ-1210/ADR-0082: split from a single `selectedRound: ClosedRoundSummary
  // | null` into an always-known id plus an optionally-known summary —
  // picking a round from the list (handleSelectRound below) has the full
  // summary (including `closedAt`, shown in the detail header) up front;
  // jumping in externally via `initialRoundId` only ever has the bare id
  // (the completion banner's link only knows `roundId`, per REQ-1210's own
  // "no backend change" scoping — see ADR-0082). The detail header below
  // simply omits the "Closed {date}" line when the summary isn't known.
  const [selectedRoundId, setSelectedRoundId] = useState<string | null>(null);
  const [selectedRoundSummary, setSelectedRoundSummary] = useState<ClosedRoundSummary | null>(null);
  const [pastDetailState, setPastDetailState] = useState<PastDetailState | null>(null);

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

  // REQ-410 (S-087): a drilled-into round belongs to exactly one game — if
  // the player switches games while looking at a specific past round's
  // detail (`selectedRound`/`pastDetailState` set), that round no longer
  // means anything under the new game, so back out to the round list rather
  // than leave a stale, now-cross-game round detail on screen. Judgement
  // call (not spelled out in the story text beyond "use your judgement"):
  // this resets on *any* gameKey change, not just while this scope is
  // active, so a stale selection can't resurface later either (e.g. drill
  // into a round under xG Grid, switch to "Current Round", switch games,
  // then click back into "Previous Rounds" — without this, the old round's
  // now-mismatched detail would still be sitting in state). Mirrors
  // `handleBackToRoundList`'s own reset below exactly, just triggered by a
  // game switch instead of a button click. Guarded by a ref (not putting
  // `gameKey` in a dependency array that also calls setState unconditionally)
  // so it only fires on a genuine change, never on mount.
  const prevGameKeyForPastDetailRef = useRef<GameKey>(gameKey);
  useEffect(() => {
    if (prevGameKeyForPastDetailRef.current !== gameKey) {
      setSelectedRoundId(null);
      setSelectedRoundSummary(null);
      setPastDetailState(null);
    }
    prevGameKeyForPastDetailRef.current = gameKey;
  }, [gameKey]);

  // The round-selection list, fetched on every transition into this
  // scope — same "idle until picked" and "re-entry, not a one-time latch"
  // reasoning, and same prev-ref-rather-than-phase-in-deps fix, as
  // LiveLeaderboard's own effect. REQ-410 (S-087): also re-fetched on a game
  // switch while already active, same reasoning as LiveLeaderboard's
  // `isSwitchingGameWhileLive`.
  // REQ-1210/ADR-0082 fix: initialized to `false`, not `active` — same fix
  // and same reasoning as LiveLeaderboard.tsx's own `prevActiveRef` (see
  // that file's comment): a mount that starts already active
  // (LeaderboardScreen's new `initialScope: 'past'`) must still count as
  // "entering" this scope so the round list actually gets fetched (needed
  // so "Back to previous rounds" has something real to show after an
  // externally-seeded `initialRoundId` jump straight into a round's
  // detail). Every existing caller (always mounts with `active: false`
  // initially) is unaffected.
  const prevActiveRef = useRef(false);
  const prevGameKeyForPastListRef = useRef<GameKey>(gameKey);
  useEffect(() => {
    const isEnteringPast = active && !prevActiveRef.current;
    const isSwitchingGameWhilePast = active && prevGameKeyForPastListRef.current !== gameKey;
    prevActiveRef.current = active;
    prevGameKeyForPastListRef.current = gameKey;
    if (!isEnteringPast && !isSwitchingGameWhilePast) return;
    let cancelled = false;
    setPastListState({ phase: 'loading' });

    fetchClosedRounds(accessToken, gameKey)
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
  }, [active, gameKey, accessToken, handleAuthError]);

  async function handleLoadMoreRoundList() {
    if (pastListState.phase !== 'ready' || pastListState.nextCursor == null || pastListState.loadingMore) return;
    const cursor = pastListState.nextCursor;

    setPastListState((prev) =>
      prev.phase === 'ready' ? { ...prev, loadingMore: true, loadMoreError: null } : prev,
    );

    try {
      const response = await fetchClosedRounds(accessToken, gameKey, cursor);
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

  // REQ-1210/ADR-0082: extracted out of the former handleSelectRound so
  // both a real list click (which also knows the full `ClosedRoundSummary`)
  // and an externally-seeded `initialRoundId` jump (which only ever knows
  // the bare id) share the exact same fetch/error-branching logic — never
  // two copies of the 404/409/other-error handling below.
  const fetchRoundDetail = useCallback(
    (roundId: string) => {
      setPastDetailState({ phase: 'loading' });

      fetchClosedRoundLeaderboard(accessToken, roundId)
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
    },
    [accessToken, handleAuthError],
  );

  function handleSelectRound(round: ClosedRoundSummary) {
    setSelectedRoundId(round.roundId);
    setSelectedRoundSummary(round);
    fetchRoundDetail(round.roundId);
  }

  // REQ-1210/ADR-0082: the `initialRoundId` jump itself — fires once this
  // scope first becomes active with a still-unconsumed `initialRoundId`,
  // mirroring a real `handleSelectRound` click but with no known
  // `ClosedRoundSummary` (see `selectedRoundSummary`'s own doc comment
  // above). Guarded by a ref, not a plain "already selected" check, so it
  // still fires correctly even if `initialRoundId` happens to be the same
  // id a player had already manually selected before this seed arrived.
  const hasConsumedInitialRoundIdRef = useRef(false);
  useEffect(() => {
    if (!active || !initialRoundId || hasConsumedInitialRoundIdRef.current) return;
    hasConsumedInitialRoundIdRef.current = true;
    setSelectedRoundId(initialRoundId);
    setSelectedRoundSummary(null);
    fetchRoundDetail(initialRoundId);
  }, [active, initialRoundId, fetchRoundDetail]);

  function handleBackToRoundList() {
    setSelectedRoundId(null);
    setSelectedRoundSummary(null);
    setPastDetailState(null);
  }

  async function handleLoadMoreRoundDetail() {
    if (
      !selectedRoundId ||
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
      const response = await fetchClosedRoundLeaderboard(accessToken, selectedRoundId, cursor);
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

  if (!active) return null;

  if (selectedRoundId && pastDetailState) {
    return (
      <>
        <div className="leaderboard-screen__past-detail-header">
          <button type="button" className="leaderboard-screen__back" onClick={handleBackToRoundList}>
            Back to previous rounds
          </button>
          {/* REQ-1210/ADR-0082: `closedAt` is only known when this round was
              reached by picking it from the list below (handleSelectRound) —
              an externally-seeded `initialRoundId` jump (the completion
              banner's link) never had it to begin with, so this line is
              simply omitted rather than showing a fabricated date. */}
          {selectedRoundSummary && (
            <p className="leaderboard-screen__scope-note">Closed {selectedRoundSummary.closedAt}</p>
          )}
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
          <p className="leaderboard-screen__status leaderboard-screen__status--error">{pastDetailState.message}</p>
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
    return <p className="leaderboard-screen__status leaderboard-screen__status--error">{pastListState.message}</p>;
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

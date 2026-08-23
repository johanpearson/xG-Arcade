import { useCallback, useEffect, useState, type Dispatch, type SetStateAction } from 'react';
import { ApiError, describeError } from './apiClient';
import { warmUpAutocomplete } from './rounds';
import { formatRoundEndTime, type RoundEndTimeDisplay } from './roundTime';

// S-169: GridScreen.tsx and PathScreen.tsx independently defined the exact
// same `LoadState` union and the exact same mount-fetch effect (differing
// only in which fetch function — fetchCurrentRound vs. fetchCurrentPath —
// they called). Extracted here once that duplication was flagged as
// byte-for-byte identical control flow, not just similar shape.
export type LoadState<TRound> =
  | { phase: 'loading' }
  | { phase: 'empty' }
  | { phase: 'error'; message: string }
  // REQ-303: roundEndTime is computed exactly once, right here at
  // fetch-success time (referenceTime = "now" at this instant), never
  // recomputed on a later render/timer — see lib/roundTime.ts's own doc
  // comment for why that's deliberate, not a shortcut to fix later.
  | { phase: 'ready'; round: TRound; roundEndTime: RoundEndTimeDisplay };

export interface UseRoundFetchResult<TRound> {
  state: LoadState<TRound>;
  // Exposed raw, not wrapped, so each screen's own mutate-fetched-state
  // logic (GridScreen's applyScoredGuess, PathScreen's post-guess re-fetch
  // in handleSubmitGuess) keeps working exactly as before, just reading/
  // writing this hook's state instead of a local one. This hook only owns
  // what happens *on mount* — it deliberately doesn't try to own every
  // later state transition a screen might need, the same way
  // useAuthedFetch.ts's own doc comment draws its "fetch succeeded/failed
  // vs. what the response means" line.
  setState: Dispatch<SetStateAction<LoadState<TRound>>>;
  // See this function's own doc comment below.
  checkRoundStillLive: (roundId: string) => Promise<'live' | 'past'>;
}

// Fetches the current round/path on mount and owns the load-state machine
// both GridScreen.tsx and PathScreen.tsx otherwise hand-rolled identically:
// loading -> (empty | error | ready), guarding against a fetch that resolves
// after the component has unmounted, and escalating a 401 to `onAuthError`
// (the caller owns logging the user out; this hook only reports it) rather
// than surfacing it as a generic error message.
//
// `TRound` is intentionally constrained to `{ roundId, endTime }` rather
// than left fully generic: `endTime` is needed to compute `roundEndTime`
// at fetch-success time, and `roundId` is needed by `checkRoundStillLive`
// below — both xG Grid's `CurrentRoundResponse` and xG Path's
// `CurrentPathResponse` already carry both fields, so this isn't a
// speculative constraint, it's exactly what both current callers have.
//
// Two things this hook deliberately does NOT fold in, and why:
//
// - The fire-and-forget `warmUpAutocomplete(accessToken)` mount effect
//   (S-151/REQ-207) is a second, genuinely independent effect in both
//   screens — it never touches `TRound` or this hook's `state` at all, and
//   folding it in here would conflate two unrelated effects under a name
//   ("useRoundFetch") that only one of them actually matches. It's `useAutocompleteWarmup`
//   below instead — a one-line addition each screen calls alongside this
//   hook, not a param or option of it.
//
// - `checkRoundStillLive` (REQ-1210/ADR-0083's live-vs-past leaderboard-
//   target resolution) IS folded in, because its actual duplicated core —
//   re-call `fetchFn`, compare the result's `roundId` to decide 'live' vs.
//   'past', falling through to 'past' on any thrown error — is exactly the
//   same shape as the mount fetch above, just reused. But it is read-only:
//   it must NEVER call `setState`. The original GridScreen/PathScreen code
//   never did either (see each file's own `handleViewCompletedRoundLeaderboard`,
//   pre-extraction) — they call the fetch function in a local `try` and only
//   ever read the result's `roundId`, never write it into component state.
//   That matters concretely: GridScreen.test.tsx's "reports the 'past'
//   scope once GET /rounds/current no longer returns this round" test has
//   the re-check 404 (fetchFn resolves to `null`). If this function fed
//   that `null` into `setState` the way the mount effect does, the screen
//   would flip from 'ready' to 'empty' right as `onViewRoundLeaderboard`
//   fires, blanking out the already-rendered completed round (and its
//   "View leaderboard" button) out from under the player mid-click. Each
//   screen keeps its own thin `handleViewCompletedRoundLeaderboard`
//   wrapper around this — it owns `checkingLeaderboardTarget` and the
//   screen-specific `gameKey`, this hook only owns the re-fetch-and-compare.
export function useRoundFetch<TRound extends { roundId: string; endTime: string }>(
  accessToken: string,
  fetchFn: (accessToken: string) => Promise<TRound | null>,
  onAuthError: () => void,
): UseRoundFetchResult<TRound> {
  const [state, setState] = useState<LoadState<TRound>>({ phase: 'loading' });

  useEffect(() => {
    let cancelled = false;

    fetchFn(accessToken)
      .then((round) => {
        if (cancelled) return;
        setState(
          round
            ? { phase: 'ready', round, roundEndTime: formatRoundEndTime(round.endTime, new Date()) }
            : { phase: 'empty' },
        );
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        if (error instanceof ApiError && error.status === 401) {
          onAuthError();
          return;
        }
        setState({ phase: 'error', message: describeError(error) });
      });

    return () => {
      cancelled = true;
    };
  }, [accessToken, fetchFn, onAuthError]);

  const checkRoundStillLive = useCallback(
    async (roundId: string): Promise<'live' | 'past'> => {
      try {
        const current = await fetchFn(accessToken);
        return current && current.roundId === roundId ? 'live' : 'past';
      } catch {
        // Falls through to 'past' — see this hook's own doc comment above
        // for why that's the deliberate choice, not an oversight.
        return 'past';
      }
    },
    [accessToken, fetchFn],
  );

  return { state, setState, checkRoundStillLive };
}

// S-151/REQ-207: fire-and-forget cold-start warm-up, independent of
// useRoundFetch's own mount effect — this must never gate or affect the
// round load's own loading/error state (see warmUpAutocomplete's own
// comment in rounds.ts). Left as its own tiny hook, deliberately not folded
// into useRoundFetch — see that hook's own doc comment for why.
export function useAutocompleteWarmup(accessToken: string): void {
  useEffect(() => {
    warmUpAutocomplete(accessToken);
  }, [accessToken]);
}

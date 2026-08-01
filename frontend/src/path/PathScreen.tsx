import { useCallback, useEffect, useState } from 'react';
import { ApiError, describeError, fetchCurrentPath, submitGuess } from '../lib/api';
import type { CurrentPathResponse } from '../lib/types';
import { PathGuessInput } from './PathGuessInput';
import { PathTimeline } from './PathTimeline';
import './PathScreen.css';

export interface PathScreenProps {
  accessToken: string;
  // Called when the round fetch itself finds the token invalid (401) — same
  // contract as GridScreenProps.onAuthError (the caller owns logging the
  // user out, this component only reports it).
  onAuthError: () => void;
  // No isGuest prop, unlike GridScreenProps: nothing on this screen is
  // guest-gated — SCREEN-10 has no REQ-215 suggestion entry point (out of
  // this story's scope, see PathGuessInput.tsx's own comment) and no other
  // guest-specific behavior, so there's nothing here for it to control.
}

type LoadState =
  | { phase: 'loading' }
  | { phase: 'empty' }
  | { phase: 'error'; message: string }
  | { phase: 'ready'; round: CurrentPathResponse };

export function PathScreen({ accessToken, onAuthError }: PathScreenProps) {
  const [state, setState] = useState<LoadState>({ phase: 'loading' });
  // S-086: which of the round's puzzles is currently shown — purely
  // client-side, per SCREEN-10's "'Next puzzle' is an explicit action,
  // never automatic" requirement. GET /path/current always returns every
  // puzzle in the round at once (no per-puzzle fetch), so this is just an
  // index into that same array, never re-derived from the server.
  const [puzzleIndex, setPuzzleIndex] = useState(0);
  // Quality-gate fix (S-086 follow-up): distinct from PathGuessInput's own
  // `error` state. That one means "the guess submission itself failed" (the
  // POST never landed) and is shown *instead of* a scored outcome. This one
  // means the opposite: the guess WAS recorded server-side (the POST
  // succeeded, result.isCorrect is real) but the follow-up GET /path/current
  // that picks up the newly revealed clue failed — so the player must never
  // be told their guess failed here, only that the screen couldn't refresh.
  const [refetchWarning, setRefetchWarning] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    fetchCurrentPath(accessToken)
      .then((round) => {
        if (cancelled) return;
        setState(round ? { phase: 'ready', round } : { phase: 'empty' });
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
  }, [accessToken, onAuthError]);

  // REQ-1203/1204 (S-086): xG Path's POST .../guesses response
  // (SubmitGuessResponse) carries isCorrect/attemptCount/locked but no clue
  // data at all — unlike GridScreen's handleSubmitGuess, which patches cell
  // state directly from that response, the only way to pick up the newly
  // revealed clue turn (or, for a correct guess, the frozen final turn plus
  // resolved name/photo) is a follow-up GET /path/current. This is exactly
  // what that endpoint's own doc comment means by "this response IS the
  // revealed-so-far state, no separate reveal endpoint" — the client re-asks
  // for it rather than the server pushing a delta. Resolves to whether the
  // guess was correct (PathGuessInput's own shake-cue trigger); throws on a
  // genuine request failure, same as GridScreen's onSubmit contract.
  const handleSubmitGuess = useCallback(
    async (submittedName: string): Promise<boolean> => {
      if (state.phase !== 'ready') return false;
      const puzzle = state.round.puzzles[puzzleIndex];
      if (!puzzle) return false;

      // Left to throw/propagate to PathGuessInput's own catch block — a
      // genuine submission failure (the POST never landed, no attempt was
      // consumed) is exactly what that error path is for.
      const result = await submitGuess(accessToken, state.round.roundId, puzzle.puzzleId, submittedName);

      // Quality-gate fix (S-086 follow-up): the guess above already
      // succeeded — REQ-1205's attempt cap was already consumed server-side
      // — so a failure in this follow-up GET must never surface as if the
      // whole submission failed (that would make a player retry and burn a
      // second attempt against the fixed 7-attempt cap without realizing one
      // was already spent). Handled separately from the submitGuess call
      // above, with its own try/catch, rather than letting it throw into the
      // same catch block PathGuessInput uses for a real submission failure.
      setRefetchWarning(null);
      let fresh: CurrentPathResponse | null;
      try {
        fresh = await fetchCurrentPath(accessToken);
      } catch {
        setRefetchWarning("Guess submitted, but couldn't refresh — try reloading this screen.");
        return result.isCorrect;
      }

      if (fresh) {
        setState({ phase: 'ready', round: fresh });
      } else {
        // The round closed between the submit and this re-fetch — treat it
        // the same as any other "no active round" case rather than leaving
        // the stale pre-guess puzzle on screen indefinitely.
        setState({ phase: 'empty' });
      }

      return result.isCorrect;
    },
    [accessToken, state, puzzleIndex],
  );

  if (state.phase === 'loading') {
    return <p className="path-screen__status">Loading this round…</p>;
  }

  if (state.phase === 'error') {
    return <p className="path-screen__status path-screen__status--error">{state.message}</p>;
  }

  // design-document.md §5: "empty states are invitations" — same calm,
  // non-error empty state GridScreen already uses for "no active round,"
  // reworded for xG Path's own puzzle vocabulary (no distinct SCREEN-10
  // empty-state copy is specified, so this reuses the established voice
  // rather than inventing new wording).
  if (state.phase === 'empty') {
    return (
      <div className="path-screen__empty">
        <h2>No puzzle to play right now</h2>
        <p>The next round is on its way — check back soon.</p>
      </div>
    );
  }

  const puzzles = state.round.puzzles;
  // Defensive clamp only — puzzles.length is fixed for the lifetime of a
  // round (REQ-1201: 3-5 puzzles generated once at round creation), so
  // puzzleIndex should never actually exceed it once initialized at 0.
  const clampedPuzzleIndex = Math.min(puzzleIndex, puzzles.length - 1);
  const puzzle = puzzles[clampedPuzzleIndex];
  const isLastPuzzle = clampedPuzzleIndex === puzzles.length - 1;
  const solved = puzzle.guess?.isCorrect ?? false;
  // REQ-1205 judgment call (flagged, not literal SCREEN-10 text): the
  // design doc only describes "Next puzzle" appearing once solved. A puzzle
  // that instead exhausts its fixed 7-attempt cap without a correct guess
  // (REQ-1205's own "locks it as unsolved" case) is an equally real,
  // reachable state this screen must not silently strand the player in —
  // so "Next puzzle" is shown whenever the puzzle is locked at all (solved
  // or exhausted), not only when solved.
  const locked = puzzle.guess?.locked ?? false;

  return (
    <div className="path-screen">
      <div className="path-screen__header">
        <h2>xG Path</h2>
        <p className="path-screen__puzzle-position mono-figure">
          Puzzle {clampedPuzzleIndex + 1} of {puzzles.length}
        </p>
      </div>

      {/* key={`${puzzle.puzzleId}-...`}: forces a clean remount on every
          puzzle switch — both components carry their own local state
          (typed-in guess text, the shake token, which nodes have already
          animated in) that must never leak from one puzzle into the next.
          Suffixed per component (quality-gate fix, S-086 follow-up) so the
          two sibling keys are never identical — an identical key on two
          sibling elements is a real React warning ("Encountered two
          children with the same key"), not just a cosmetic duplicate. */}
      <PathTimeline
        key={`${puzzle.puzzleId}-timeline`}
        clues={puzzle.clues}
        solved={solved}
        resolvedPlayerName={puzzle.guess?.resolvedPlayerName}
        resolvedPlayerPhotoUrl={puzzle.guess?.resolvedPlayerPhotoUrl}
      />

      <PathGuessInput
        key={`${puzzle.puzzleId}-guess`}
        clueCount={puzzle.clues.length}
        guess={puzzle.guess}
        onSubmit={handleSubmitGuess}
      />

      {/* Quality-gate fix (S-086 follow-up): a successful guess whose
          follow-up re-fetch failed — the guess itself is not in question, so
          this is worded as a refresh problem, never as if the guess failed. */}
      {refetchWarning && <p className="path-screen__refetch-warning">{refetchWarning}</p>}

      {locked &&
        (isLastPuzzle ? (
          <p className="path-screen__complete">You&rsquo;ve completed every puzzle in this round.</p>
        ) : (
          <button
            type="button"
            className="path-screen__next-button"
            onClick={() => {
              setRefetchWarning(null);
              setPuzzleIndex((current) => Math.min(current + 1, puzzles.length - 1));
            }}
          >
            Next puzzle
          </button>
        ))}
    </div>
  );
}

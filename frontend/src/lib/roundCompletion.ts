import { useEffect, useRef, useState } from 'react';

// REQ-1210/ADR-0082: a generic, cross-game "has this player finished every
// cell available to them in this round" signal, computed entirely
// client-side from data both games' current-round responses already
// return (`CurrentRoundResponse.cells[].guess` for xG Grid,
// `CurrentPathResponse.puzzles[].guess` for xG Path — see
// `frontend/src/lib/types.ts`). Deliberately the *only* shape this module
// knows about — GridScreen.tsx maps its own `cells` array into this shape,
// PathScreen.tsx maps its own `puzzles` array into it, and both call the
// same `computeRoundCompletion`/`useCompletionTransition` below. This is
// what keeps the signal generic across games (ADR-0003's boundary: no
// game-specific branching lives in a cross-game component) rather than a
// per-game special case hard-coded here or duplicated in each screen.
export interface CompletableItem {
  locked: boolean;
  // The item's own already-authoritative points value (xG Grid: a correct
  // cell's live/locked value or a locked-incorrect cell's known worst-case
  // value; xG Path: a locked puzzle's own final `points`) — null whenever
  // that value isn't known yet (an unanswered cell, an incorrect guess with
  // attempts remaining, or a correct guess whose live value hasn't been
  // re-fetched yet). Never a second, independently-computed score — see
  // each screen's own mapping function for exactly which existing field
  // this is sourced from.
  points: number | null;
}

export interface RoundCompletionResult {
  isComplete: boolean;
  currentPoints: number;
}

// REQ-1210: "every cell in the round now has a locked outcome for that
// player" — true only once every item is locked. `items.length > 0` is a
// defensive guard against vacuously reporting "complete" for a round with
// no cells at all (shouldn't happen — REQ-101/102/1201 always generate at
// least one — but a `.every()` over an empty array is trivially true, and
// that's never a real completion). `currentPoints` sums exactly the
// already-known point values passed in — a null (not-yet-known) value
// contributes 0, mirroring how GridScreen's own pre-existing
// `totalKnownPoints`/PathScreen's per-puzzle points already treat an
// unknown value as "not counted yet," not as zero-and-final.
export function computeRoundCompletion(items: readonly CompletableItem[]): RoundCompletionResult {
  const isComplete = items.length > 0 && items.every((item) => item.locked);
  const currentPoints = items.reduce((sum, item) => sum + (item.points ?? 0), 0);
  return { isComplete, currentPoints };
}

// REQ-1210 §7 (open question, resolved conservatively — see
// `requirements-document.md`'s 2026-08-22 entry and ADR-0082's "For AI
// agents" note): whether the completion animation should replay on every
// later view of an already-complete round is explicitly left open, since
// resolving it "properly" would need a new piece of persisted
// per-player-per-round state that doesn't exist anywhere today. The safe
// default implemented here: fire only on an in-session `false -> true`
// transition detected *while this hook's caller stays mounted* — never on
// the very first `isComplete` value this hook ever observes, even when
// that first value is already `true` (e.g. reloading an already-finished
// round, or navigating back into the game screen after it was already
// completed earlier in the same browser session — GridScreen/PathScreen
// remount on every screen switch, so "mounted" here effectively means
// "this specific visit to the screen," not just "this browser tab's
// lifetime").
//
// `isComplete` is `null` while the round hasn't loaded yet (the caller's
// own loading/error/empty phases) — distinct from a real, loaded `false`,
// so the "first value ever observed" baseline is captured from real round
// data, never from a placeholder used only because nothing has been
// fetched yet.
export function useCompletionTransition(isComplete: boolean | null): boolean {
  const baselineRef = useRef<boolean | null>(null);
  const [justCompleted, setJustCompleted] = useState(false);

  useEffect(() => {
    if (isComplete === null) return;

    if (baselineRef.current === null) {
      // First real value this hook has ever seen for this mount — this
      // establishes the baseline only, never itself "just completed."
      baselineRef.current = isComplete;
      return;
    }

    if (isComplete && !baselineRef.current) {
      setJustCompleted(true);
    }
    baselineRef.current = isComplete;
  }, [isComplete]);

  return justCompleted;
}

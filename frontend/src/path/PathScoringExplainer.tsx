import { useEffect, useRef } from 'react';
import { MAX_CLUES_PER_PUZZLE } from '../lib/pathRules';
import { MAX_POINTS_PER_CELL } from '../lib/scoringRules';
import './PathScoringExplainer.css';

export interface PathScoringExplainerProps {
  onClose: () => void;
}

// SCREEN-10 / REQ-213 (second consumer, 2026-08-08): a player-facing
// "How scoring works" explainer for xG Path, opened via a header (ⓘ) button
// in PathScreen.tsx — the same pattern xG Grid established (REQ-213/S-041,
// see ../grid/ScoringExplainer.tsx). This is a NEW, separate component
// rather than a game-aware branch inside ScoringExplainer.tsx or a literal
// reuse of it: xG Grid and xG Path share almost nothing in their actual
// rules (no uniqueness concept here at all, no live/locked point
// distinction — a locked xG Path score is final immediately, never
// provisional — a completely different attempt-cap/clue model, and no
// player-pool or leaderboard-ranking content belongs here either). Forcing
// one component to carry both games' content via a `gameKey` prop would
// mean every paragraph is wrapped in a branch, which reads worse and risks
// one game's edit accidentally bleeding into the other's copy — the kind of
// "two similar-but-diverging copies" duplication this codebase warns
// against is the *content*, not the *shell*, so a second small component
// with its own file is the better fit here. The modal shell itself (focus
// management, Escape-to-close, dialog markup) IS duplicated below rather
// than extracted into a shared hook/component: at exactly two call sites,
// each under 20 lines, extracting a shared abstraction for this would be
// premature (this repo's own "three similar lines is better than a
// premature abstraction" guidance) — if a third game ever needs the same
// shell, that's the point to extract it, not before.
//
// Known, pre-existing, out-of-scope gap (flagged, not fixed here):
// LeaderboardScreen.tsx's own (ⓘ) entry point still opens xG Grid's
// ScoringExplainer verbatim even when the xG Path leaderboard tab is
// active, showing Grid-specific content (uniqueness, live/locked cells,
// median ranking) that doesn't describe xG Path's rules. That predates this
// change and is unrelated to PathScreen.tsx — left as a follow-up
// candidate, not touched here.
export function PathScoringExplainer({ onClose }: PathScoringExplainerProps) {
  const closeButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  // Same focus-in-on-open / focus-returns-to-trigger-on-close behavior as
  // ScoringExplainer.tsx — see that component's own comment for the
  // rationale (a keyboard/screen-reader user's focus must never be left
  // stranded on a now-invisible element).
  useEffect(() => {
    const previouslyFocused = document.activeElement as HTMLElement | null;
    closeButtonRef.current?.focus();
    return () => {
      previouslyFocused?.focus();
    };
  }, []);

  return (
    <div className="path-scoring-explainer-backdrop" onClick={onClose}>
      <div
        className="path-scoring-explainer"
        role="dialog"
        aria-modal="true"
        aria-label="How scoring works"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="path-scoring-explainer__header">
          <h3>How scoring works</h3>
          <button
            ref={closeButtonRef}
            type="button"
            className="path-scoring-explainer__close"
            onClick={onClose}
            aria-label="Close"
          >
            ×
          </button>
        </div>
        <p className="path-scoring-explainer__text">
          Each round has a handful of puzzles. Every puzzle reveals up to {MAX_CLUES_PER_PUZZLE} clues about
          one target player, one at a time: first their career clubs (spread across 3 turns, oldest
          first), then one turn showing the start&ndash;end years for every club revealed so far, then
          position, nationality, and age.
        </p>
        <p className="path-scoring-explainer__text">
          A wrong guess reveals the next clue. A correct guess stops the sequence immediately &mdash; no
          further clue is ever revealed once you've solved it.
        </p>
        <p className="path-scoring-explainer__text">
          You get {MAX_CLUES_PER_PUZZLE} attempts per puzzle. If none of them is correct, the puzzle locks
          unsolved and reveals the answer.
        </p>
        <p className="path-scoring-explainer__text">
          xG Arcade is scored like golf &mdash; lower is better. A correct guess scores better the fewer
          clues you needed: guessing right on the very first clue scores {Math.round((1 / MAX_CLUES_PER_PUZZLE) * MAX_POINTS_PER_CELL)}{' '}
          pts, needing every clue scores the full {MAX_POINTS_PER_CELL} pts. A puzzle that locks unsolved
          scores that same maximum &mdash; the same worst-case score as using every clue and still
          guessing wrong.
        </p>
        <p className="path-scoring-explainer__text">
          Once a puzzle locks, its score is final right away &mdash; it never changes afterwards, so
          there's no live or provisional value to watch update.
        </p>
      </div>
    </div>
  );
}

import { useEffect, useRef } from 'react';
import { MAX_ATTEMPTS_PER_CELL } from '../lib/guessRules';
import { MAX_POINTS_PER_CELL } from '../lib/scoringRules';
import './ScoringExplainer.css';

export interface ScoringExplainerProps {
  onClose: () => void;
}

// SCREEN-06 / REQ-213: a general explainer for how a live vs. locked point
// value works and how golf-style scoring works overall — opened from the
// grid header's (ⓘ) entry point (GridScreen.tsx). This is where the
// %-breakdown/round-end content S-041 removed from SCREEN-01a's per-cell
// disclosure now lives, reworded to be general rather than tied to one
// cell's specific numbers — REQ-213 requires no cell-specific numbers here,
// so the content stays valid regardless of which cells the player has
// attempted.
//
// Content expanded 2026-07-14, requested directly by a player in the same
// message that reported the SCREEN-01a locked-incorrect-cell bug (see
// CellState.tsx/S-033): attempts count, the wrong-guess/unanswered-cell
// max-score rule (ADR-0021/S-028 — previously each only documented in
// isolation, nothing connecting them for a player reading this), and the
// player-pool restriction (REQ-112/ADR-0025), none of which were
// previously stated anywhere player-facing at all.
//
// Content expanded again 2026-07-21 (S-068): the all-time leaderboard's
// median ranking (REQ-409) plus its 5-qualifying-round minimum, and the
// never-played/live-scope-untouched-cell fairness rules (REQ-404/406/407,
// S-056) — these were genuinely player-visible on the leaderboard screen
// (SCREEN-03) already but explained nowhere a player actually reads them.
// This same component is now also opened from LeaderboardScreen.tsx's own
// `(ⓘ)` entry point, deliberately reusing this one component rather than
// building a second, divergent explainer (see REQ-213's 2026-07-21 note) —
// its content is identical regardless of which screen opened it, so these
// three paragraphs render every time, including from the grid screen.
export function ScoringExplainer({ onClose }: ScoringExplainerProps) {
  const closeButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  // A modal (unlike GuessInput, which doesn't do this yet — a known,
  // separate gap out of this story's scope) moves focus in on open and
  // returns it to whatever triggered it (the header's (ⓘ) button) on
  // close, rather than leaving a keyboard/screen-reader user's focus
  // stranded on a now-invisible element.
  useEffect(() => {
    const previouslyFocused = document.activeElement as HTMLElement | null;
    closeButtonRef.current?.focus();
    return () => {
      previouslyFocused?.focus();
    };
  }, []);

  return (
    <div className="scoring-explainer-backdrop" onClick={onClose}>
      <div
        className="scoring-explainer"
        role="dialog"
        aria-modal="true"
        aria-label="How scoring works"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="scoring-explainer__header">
          <h3>How scoring works</h3>
          <button
            ref={closeButtonRef}
            type="button"
            className="scoring-explainer__close"
            onClick={onClose}
            aria-label="Close"
          >
            ×
          </button>
        </div>
        <p className="scoring-explainer__text">You get {MAX_ATTEMPTS_PER_CELL} attempts per cell.</p>
        <p className="scoring-explainer__text">
          A correct cell shows a live estimate that can still change until the round closes.
        </p>
        <p className="scoring-explainer__text">
          Once the round closes, that value is locked and won't change again.
        </p>
        <p className="scoring-explainer__text">
          A wrong guess (after all {MAX_ATTEMPTS_PER_CELL} attempts) locks in the maximum score (
          {MAX_POINTS_PER_CELL} pts) for that cell — the same maximum score you'd get by not guessing at
          all once the round closes.
        </p>
        <p className="scoring-explainer__text">
          xG Arcade is scored like golf — lower is better. An answer fewer other players also
          guessed scores better than a common one.
        </p>
        <p className="scoring-explainer__text">
          Answers are footballers who are male and born in 1939 or later.
        </p>
        <p className="scoring-explainer__text">
          The all-time leaderboard ranks players by the median of their per-round scores, not a
          total — the same golf-style framing above (lower is better) applies to that median
          exactly the same way it applies to any single round's score. That hasn't changed.
        </p>
        <p className="scoring-explainer__text">
          A player needs at least 5 qualifying rounds — closed, with at least one guess in that
          round — before they appear on the all-time ranked list at all. Fewer than that, and not
          appearing yet is expected, not a bug.
        </p>
        <p className="scoring-explainer__text">
          A league member who has never submitted a single guess doesn't appear on the ranked list
          at all — never ranked first with a default of zero. In the Current Round leaderboard,
          once a player has made at least one guess anywhere in that round's grid, every other
          cell they haven't touched counts at the maximum score — the same value a cell locks at
          once the round closes without a correct guess.
        </p>
      </div>
    </div>
  );
}

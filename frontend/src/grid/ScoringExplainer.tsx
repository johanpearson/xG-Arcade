import { useEffect } from 'react';
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
export function ScoringExplainer({ onClose }: ScoringExplainerProps) {
  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

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
            type="button"
            className="scoring-explainer__close"
            onClick={onClose}
            aria-label="Close"
          >
            ×
          </button>
        </div>
        <p className="scoring-explainer__text">
          A correct cell shows a live estimate that can still change until the round closes.
        </p>
        <p className="scoring-explainer__text">
          Once the round closes, that value is locked and won't change again.
        </p>
        <p className="scoring-explainer__text">
          xG Arcade is scored like golf — lower is better. An answer fewer other players also
          guessed scores better than a common one.
        </p>
      </div>
    </div>
  );
}

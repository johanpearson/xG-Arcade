import { useEffect, useRef } from 'react';
import { PREDICT_POINTS_PER_COMPONENT } from '../lib/scoringRules';
import './PredictScoringExplainer.css';

export interface PredictScoringExplainerProps {
  onClose: () => void;
}

// SCREEN-03 / REQ-213 (third consumer, S-198): a player-facing "How scoring
// works" explainer for xG Predict, opened only from LeaderboardScreen.tsx's
// own `(ⓘ)` entry point today — unlike ScoringExplainer.tsx/
// PathScoringExplainer.tsx, PredictScreen.tsx has no `(ⓘ)` button of its own
// yet (out of scope for this story; see docs/backlog.md's S-198 entry for
// that as a flagged, non-blocking follow-up, not silently forgotten).
//
// A new, separate component rather than a game-aware branch inside
// ScoringExplainer.tsx or PathScoringExplainer.tsx, for the same reasoning
// PathScoringExplainer.tsx's own doc comment already gives for being
// separate from ScoringExplainer.tsx: xG Predict shares almost nothing in
// its actual rules with either sibling game (no uniqueness, no live/locked
// point distinction, no clue/attempt sequence — three independent
// outcome/home-goals/away-goals components scored per match instead), and
// REQ-1304/ADR-0095's scoring *direction* is the opposite of both siblings
// (higher is better, not golf-style) — the one thing this explainer must
// never leave ambiguous. The modal shell itself (focus management,
// Escape-to-close, dialog markup) is duplicated below rather than extracted
// into a shared hook/component, same "three similar-but-small call sites is
// not yet a premature-abstraction problem" reasoning
// PathScoringExplainer.tsx's own comment already applies at exactly two
// call sites — now three, still under this repo's own rule-of-three
// threshold for extraction, not over it.
export function PredictScoringExplainer({ onClose }: PredictScoringExplainerProps) {
  const closeButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  // Same focus-in-on-open / focus-returns-to-trigger-on-close behavior as
  // ScoringExplainer.tsx/PathScoringExplainer.tsx — see either's own comment
  // for the rationale (a keyboard/screen-reader user's focus must never be
  // left stranded on a now-invisible element).
  useEffect(() => {
    const previouslyFocused = document.activeElement as HTMLElement | null;
    closeButtonRef.current?.focus();
    return () => {
      previouslyFocused?.focus();
    };
  }, []);

  return (
    <div className="predict-scoring-explainer-backdrop" onClick={onClose}>
      <div
        className="predict-scoring-explainer"
        role="dialog"
        aria-modal="true"
        aria-label="How scoring works"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="predict-scoring-explainer__header">
          <h3>How scoring works</h3>
          <button
            ref={closeButtonRef}
            type="button"
            className="predict-scoring-explainer__close"
            onClick={onClose}
            aria-label="Close"
          >
            ×
          </button>
        </div>
        <p className="predict-scoring-explainer__text">
          Each round has 5 matches. Predict every match's final score before it kicks off — a home-goal
          count and an away-goal count for each.
        </p>
        <p className="predict-scoring-explainer__text">
          Unlike xG Arcade's other games, xG Predict is scored the conventional way &mdash; higher is
          better. More correct predictions means a bigger number, and rank #1 on this leaderboard is the
          <strong> highest</strong> total, not the lowest.
        </p>
        <p className="predict-scoring-explainer__text">
          Once a match is played and graded, your prediction for it earns points from three independent
          components, each worth {PREDICT_POINTS_PER_COMPONENT} pts on its own:
        </p>
        <p className="predict-scoring-explainer__text">
          1. <strong>Outcome</strong> &mdash; the predicted result (home win, draw, or away win) matches
          the actual result.
          <br />
          2. <strong>Home goals</strong> &mdash; the predicted home-team goal count exactly matches the
          actual count.
          <br />
          3. <strong>Away goals</strong> &mdash; the predicted away-team goal count exactly matches the
          actual count.
        </p>
        <p className="predict-scoring-explainer__text">
          Each component scores on its own — a prediction can earn the outcome points without earning
          either goal-count component, or the other way around. Predicting 2&ndash;1 for an actual 3&ndash;1
          result, for example, earns the outcome and away-goals components but not the home-goals one. A
          component that doesn't match earns 0, never a penalty.
        </p>
        <p className="predict-scoring-explainer__text">
          Your round total is the sum of every component across all 5 matches. A match that hasn't been
          played and graded yet contributes nothing to your total until it is.
        </p>
      </div>
    </div>
  );
}

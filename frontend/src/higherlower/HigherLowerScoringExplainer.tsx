import { ScoringExplainerShell } from '../components/ScoringExplainerShell';
import './HigherLowerScoringExplainer.css';

export interface HigherLowerScoringExplainerProps {
  onClose: () => void;
}

// REQ-1505 / REQ-213 (fourth consumer, S-228): a player-facing "How scoring
// works" explainer for xG Higher/Lower, opened from LeaderboardScreen.tsx's
// own `(ⓘ)` entry point — mirrors PredictScoringExplainer's own "no `(ⓘ)` on
// the game screen itself yet" scope note, same reasoning (out of this
// story's listed file set; HigherLowerScreen.tsx has no explainer entry
// point of its own for the same reason). This is required, not optional
// scope creep: LeaderboardScreen.tsx's `GameKey` union and its
// `explainerForGameKey` exhaustive switch (REQ-404/ADR-0095's own pattern)
// mean any GameKey reachable via `LeaderboardRoundTarget` — which
// HigherLowerScreen.tsx's REQ-1210 wiring produces — must have a real
// explainer case, not a stub; there is no "leaderboard tab without an
// explainer" shape anywhere else in this codebase to fall back to.
//
// A new, separate component rather than a branch inside any sibling
// explainer, for the same reasoning PathScoringExplainer's/
// PredictScoringExplainer's own doc comments already give: xG Higher/Lower
// shares nothing in its actual rules with any sibling game (no uniqueness,
// no live/locked distinction, no clue/attempt sequence, no independently-
// scored components — a single streak counter is the entire mechanic). The
// modal shell itself is shared via ScoringExplainerShell, same as every
// sibling.
export function HigherLowerScoringExplainer({ onClose }: HigherLowerScoringExplainerProps) {
  return (
    <ScoringExplainerShell
      onClose={onClose}
      backdropClassName="higher-lower-scoring-explainer-backdrop"
      dialogClassName="higher-lower-scoring-explainer"
      headerClassName="higher-lower-scoring-explainer__header"
      closeClassName="higher-lower-scoring-explainer__close"
    >
      <p className="higher-lower-scoring-explainer__text">
        Each round has one fixed sequence of players to compare, the same sequence for every
        participant. You start from a revealed baseline player and guess whether the next, hidden
        player is Higher or Lower on that round's stat.
      </p>
      <p className="higher-lower-scoring-explainer__text">
        Guess correctly and that player's value is revealed, becomes your new baseline, and you move
        on to the next player in the sequence. Guess incorrectly, or correctly guess the very last
        player in the sequence, and your attempt ends there.
      </p>
      <p className="higher-lower-scoring-explainer__text">
        Unlike xG Arcade's golf-scored games, xG Higher/Lower is scored the conventional way &mdash;
        higher is better. Your score is your streak: how many players in a row you guessed correctly
        before your attempt ended.
      </p>
    </ScoringExplainerShell>
  );
}

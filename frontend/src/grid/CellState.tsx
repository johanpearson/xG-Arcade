import { MAX_ATTEMPTS_PER_CELL } from '../lib/guessRules';
import './CellState.css';

export type RoundStatus = 'active' | 'closed';

export interface CellStateProps {
  // The guessed/revealed player's name. GET /rounds/current does not return
  // this (only isCorrect/attemptCount/locked) — the real name is only known
  // client-side for a guess submitted in the current session. Falls back to
  // a plain, non-fabricated label rather than inventing a name; see the
  // frontend report for this gap flagged back for a follow-up REQ.
  playerName?: string;
  isCorrect: boolean;
  attemptCount: number;
  locked: boolean;
  // Explicit prop (not inferred) so this component can be exercised for
  // state 4 (round closed) via constructed props in tests, without wiring
  // up a live "closed round" flow that GET /rounds/current can't produce
  // today (S-011 scope).
  roundStatus: RoundStatus;
}

// SCREEN-01a: the four "attempted" cell states from REQ-210. An unattempted
// cell (guess === null) is a simple guard clause in the caller (GridCell),
// not a fifth case here — this component always assumes at least one
// attempt was made.
export function CellState({
  playerName,
  isCorrect,
  attemptCount,
  locked,
  roundStatus,
}: CellStateProps) {
  const name = playerName ?? 'Guess submitted';

  // State 4: round closed — either prior outcome, now permanent. No "live"
  // dot at all, regardless of correctness.
  if (roundStatus === 'closed') {
    return (
      <div
        className={`cell-state cell-state--final cell-state--${isCorrect ? 'correct' : 'incorrect'}`}
      >
        <Row name={name} correct={isCorrect} />
        <p className="cell-state__meta">final</p>
      </div>
    );
  }

  // State 1: correct, round still active — locked from further guessing,
  // but still "live" until round close (REQ-203/204 — no uniqueness percent
  // exists yet, so that line is omitted rather than fabricated).
  if (isCorrect) {
    return (
      <div className="cell-state cell-state--live cell-state--correct">
        <Row name={name} correct />
        <p className="cell-state__meta">
          <span className="cell-state__live-dot" aria-hidden="true" />
          live
        </p>
      </div>
    );
  }

  const attemptsLeft = MAX_ATTEMPTS_PER_CELL - attemptCount;

  // State 2: incorrect, at least one attempt remaining.
  if (!locked && attemptsLeft > 0) {
    return (
      <div className="cell-state cell-state--incorrect">
        <Row name={name} correct={false} />
        <p className="cell-state__meta">
          {attemptsLeft} attempt{attemptsLeft === 1 ? '' : 's'} left
        </p>
      </div>
    );
  }

  // State 3: incorrect, no attempts remaining (round still active). REQ-205/
  // 206 don't exist yet, so no "0 pts" line — same reasoning as state 1.
  return (
    <div className="cell-state cell-state--incorrect cell-state--locked">
      <Row name={name} correct={false} />
      <p className="cell-state__meta">no attempts left</p>
    </div>
  );
}

function Row({ name, correct }: { name: string; correct: boolean }) {
  return (
    <div className="cell-state__row">
      <span className="cell-state__name">{name}</span>
      <span
        className={`cell-state__icon cell-state__icon--${correct ? 'correct' : 'incorrect'}`}
        aria-hidden="true"
      >
        {correct ? '✓' : '✕'}
      </span>
    </div>
  );
}

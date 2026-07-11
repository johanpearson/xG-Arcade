import { useEffect, useRef, useState } from 'react';
import { MAX_ATTEMPTS_PER_CELL } from '../lib/guessRules';
import { CategoryGlyph } from './CategoryLabel';
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
  // REQ-204: live unique_percent (0-1), re-derived on every request. Only
  // ever set when isCorrect — undefined/null means "not correct yet" or
  // (state 4) "not wired to a live source yet," never fabricated.
  uniquePercent?: number | null;
  // REQ-204's "updates until round closes on [date/time]" microcopy — only
  // rendered alongside uniquePercent, since it describes that value's
  // liveness, not the cell generally.
  roundEndTime?: string;
  // REQ-205/206: the locked, permanent score — only meaningful once
  // roundStatus is "closed". Not reachable via GET /rounds/current today
  // (that endpoint only ever returns an Active round, same S-011-scope gap
  // as SCREEN-01a's state 4 generally), so this is exercised via
  // constructed props only, same as roundStatus="closed" itself.
  finalPoints?: number | null;
  // design-document.md §2's "signature element: badge dock" — the two
  // categories this cell combines, needed to render their flag/badge
  // glyphs docked beside the revealed name on a correct guess. Optional:
  // callers that don't pass these simply get no badge dock (e.g. existing
  // tests constructed before S-015).
  rowCategoryType?: string;
  rowCategoryValue?: string;
  colCategoryType?: string;
  colCategoryValue?: string;
}

interface CategoryRef {
  categoryType: string;
  value: string;
}

// design-document.md §2: "Used only at guess-submit and round-close reveal,
// nowhere else." Both are *transitions* observed while this component stays
// mounted (the cell isn't remounted across a guess submission or a round
// closing) — never on first mount already in that state, e.g. a page
// reload showing a cell that was already correct/closed. Returns a token
// that increments once per qualifying transition; 0 means "never
// revealed," i.e. render the badges already docked, no animation.
function useRevealToken(isCorrect: boolean, roundStatus: RoundStatus): number {
  const prevRef = useRef({ isCorrect, roundStatus });
  const [token, setToken] = useState(0);

  useEffect(() => {
    const prev = prevRef.current;
    const justAnsweredCorrectly = isCorrect && !prev.isCorrect && roundStatus === 'active';
    const justClosedWhileCorrect =
      isCorrect && prev.roundStatus === 'active' && roundStatus === 'closed';
    if (justAnsweredCorrectly || justClosedWhileCorrect) {
      setToken((current) => current + 1);
    }
    prevRef.current = { isCorrect, roundStatus };
  }, [isCorrect, roundStatus]);

  return token;
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
  uniquePercent,
  roundEndTime,
  finalPoints,
  rowCategoryType,
  rowCategoryValue,
  colCategoryType,
  colCategoryValue,
}: CellStateProps) {
  const name = playerName ?? 'Guess submitted';
  const revealToken = useRevealToken(isCorrect, roundStatus);
  const badges =
    rowCategoryType != null && rowCategoryValue != null && colCategoryType != null && colCategoryValue != null
      ? {
          row: { categoryType: rowCategoryType, value: rowCategoryValue },
          col: { categoryType: colCategoryType, value: colCategoryValue },
        }
      : undefined;

  // State 4: round closed — either prior outcome, now permanent. No "live"
  // dot at all, regardless of correctness.
  if (roundStatus === 'closed') {
    return (
      <div
        key={revealToken}
        className={`cell-state cell-state--final cell-state--${isCorrect ? 'correct' : 'incorrect'} ${
          isCorrect && revealToken > 0 ? 'cell-state--reveal' : ''
        }`}
      >
        <Row name={name} correct={isCorrect} badges={isCorrect ? badges : undefined} />
        {isCorrect && uniquePercent != null && finalPoints != null && (
          <p className="cell-state__meta">
            {formatPercent(uniquePercent)}% unique · {finalPoints} pts
          </p>
        )}
        <p className="cell-state__meta">final</p>
      </div>
    );
  }

  // State 1: correct, round still active — locked from further guessing,
  // but still "live" until round close (REQ-203/204).
  if (isCorrect) {
    return (
      <div
        key={revealToken}
        className={`cell-state cell-state--live cell-state--correct ${
          revealToken > 0 ? 'cell-state--reveal' : ''
        }`}
      >
        <Row name={name} correct badges={badges} />
        <p className="cell-state__meta">
          <span className="cell-state__live-dot" aria-hidden="true" />
          live
        </p>
        {uniquePercent != null && (
          <>
            <p className="cell-state__meta mono-figure">{formatPercent(uniquePercent)}% unique</p>
            {roundEndTime && (
              <p className="cell-state__meta cell-state__meta--muted">
                updates until round closes on {formatDateTime(roundEndTime)}
              </p>
            )}
          </>
        )}
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

// REQ-204: uniquePercent arrives as a 0-1 fraction from the API.
function formatPercent(uniquePercent: number): number {
  return Math.round(uniquePercent * 100);
}

function formatDateTime(isoDateTime: string): string {
  return new Date(isoDateTime).toLocaleString(undefined, {
    weekday: 'short',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function Row({
  name,
  correct,
  badges,
}: {
  name: string;
  correct: boolean;
  badges?: { row: CategoryRef; col: CategoryRef };
}) {
  return (
    <div className="cell-state__row">
      {badges && (
        <span
          className="cell-state__badge-dock cell-state__badge-dock--row"
          data-testid="badge-dock-row"
          aria-hidden="true"
        >
          <CategoryGlyph categoryType={badges.row.categoryType} value={badges.row.value} size="small" />
        </span>
      )}
      <span className="cell-state__name">{name}</span>
      {badges && (
        <span
          className="cell-state__badge-dock cell-state__badge-dock--col"
          data-testid="badge-dock-col"
          aria-hidden="true"
        >
          <CategoryGlyph categoryType={badges.col.categoryType} value={badges.col.value} size="small" />
        </span>
      )}
      <span
        className={`cell-state__icon cell-state__icon--${correct ? 'correct' : 'incorrect'}`}
        aria-hidden="true"
      >
        {correct ? '✓' : '✕'}
      </span>
    </div>
  );
}

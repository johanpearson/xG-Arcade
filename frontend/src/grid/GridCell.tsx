import { useState } from 'react';
import { CellState, type RoundStatus } from './CellState';
import type { CurrentRoundCell } from '../lib/types';

export interface GridCellProps {
  cell: CurrentRoundCell;
  roundStatus: RoundStatus;
  // S-020: true only when this cell's guess was submitted in this browser
  // session (GridScreen tracks this per cellId) — false for a guess loaded
  // from GET /rounds/current on page load (e.g. a reload showing a cell
  // someone already attempted). See CellState's submittedThisSession doc
  // comment for why this distinction matters for the shake cue.
  submittedThisSession?: boolean;
  onOpenGuess: (cell: CurrentRoundCell) => void;
}

// Guard clause for the implicit fifth visual (an unattempted cell) lives
// here, before CellState's four-state logic — not a fifth case inside that
// component (SCREEN-01a).
export function GridCell({ cell, roundStatus, submittedThisSession, onOpenGuess }: GridCellProps) {
  const { guess } = cell;
  const canOpen = guess === null || !guess.locked;
  // REQ-212 (S-041): whether this cell's guessed player name/badge dock is
  // currently shown. Owned here, not CellState — the click/tap target that
  // toggles it is the whole cell (this component's own rendered button),
  // replacing S-019/S-040's small in-cell reveal toggle. Only meaningful
  // once the guess is locked+correct (isRevealable below); a fresh guess
  // (or a page reload) always starts hidden.
  const [revealed, setRevealed] = useState(false);
  const isRevealable = guess !== null && guess.locked && guess.isCorrect;

  const content =
    guess === null ? (
      <span className="grid-cell__empty" aria-hidden="true">
        +
      </span>
    ) : (
      <CellState
        // Frontend name-display fix: only a correct guess ever gets a name —
        // resolvedPlayerName is the canonical Player.FullName, never the raw
        // as-typed submittedName. An incorrect guess passes no name at all.
        playerName={guess.isCorrect ? (guess.resolvedPlayerName ?? undefined) : undefined}
        // REQ-214: same isCorrect gate as the name above — an incorrect
        // guess never gets a photo, regardless of what the API happens to
        // send (see CurrentRoundGuess.resolvedPlayerPhotoUrl for the
        // confirmed backend field this maps to).
        photoUrl={guess.isCorrect ? guess.resolvedPlayerPhotoUrl : undefined}
        isCorrect={guess.isCorrect}
        attemptCount={guess.attemptCount}
        locked={guess.locked}
        roundStatus={roundStatus}
        livePoints={guess.livePoints}
        submittedThisSession={submittedThisSession}
        rowCategoryType={cell.rowCategoryType}
        rowCategoryValue={cell.rowCategoryValue}
        colCategoryType={cell.colCategoryType}
        colCategoryValue={cell.colCategoryValue}
        revealed={revealed}
      />
    );

  // data-testid is shared by all branches below so E2E tests can re-select
  // the same cell across this state change without depending on exact copy
  // (tests/e2e/play-grid.spec.ts, S-010).
  if (canOpen) {
    return (
      <button
        type="button"
        className="grid-cell"
        onClick={() => onOpenGuess(cell)}
        aria-label={
          guess === null
            ? `Guess ${cell.rowCategoryValue} × ${cell.colCategoryValue}`
            : undefined
        }
        // aria-label is intentionally only set for the unattempted state
        // above (an attempted cell's accessible name comes from CellState's
        // own rendered text instead).
        data-testid={`grid-cell-${cell.cellId}`}
      >
        {content}
      </button>
    );
  }

  // A locked+correct cell (states 1/4) can no longer open the guess input,
  // but it IS the click/tap target for REQ-212's reveal — a real, focusable
  // <button> (not the disabled div below), since CellState no longer owns
  // any control of its own to nest one inside (S-041 removed it).
  if (isRevealable) {
    return (
      <button
        type="button"
        className="grid-cell"
        onClick={() => setRevealed((current) => !current)}
        aria-expanded={revealed}
        aria-label={
          revealed
            ? `Hide guessed player for ${cell.rowCategoryValue} × ${cell.colCategoryValue}`
            : `Show guessed player for ${cell.rowCategoryValue} × ${cell.colCategoryValue}`
        }
        data-testid={`grid-cell-${cell.cellId}`}
      >
        {content}
      </button>
    );
  }

  // A locked+incorrect cell (state 3, or state 4's incorrect outcome) stays
  // non-interactive — there is nothing to reveal, no name is ever shown for
  // a wrong guess (S-029). `role="group"` + `aria-disabled` keeps the same
  // "not open-able" semantics for E2E assertions (Playwright's
  // toBeDisabled/toBeEnabled only honor `aria-disabled` on elements whose
  // role appears in its aria-disabled-eligible role list, which "group"
  // does but a bare <div>'s implicit role does not).
  return (
    <div
      className="grid-cell grid-cell--locked"
      role="group"
      aria-disabled="true"
      data-testid={`grid-cell-${cell.cellId}`}
    >
      {content}
    </div>
  );
}

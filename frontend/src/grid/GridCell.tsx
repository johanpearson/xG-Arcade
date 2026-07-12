import { CellState, type RoundStatus } from './CellState';
import type { CurrentRoundCell } from '../lib/types';

export interface GridCellProps {
  cell: CurrentRoundCell;
  roundStatus: RoundStatus;
  roundEndTime: string;
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
export function GridCell({ cell, roundStatus, roundEndTime, submittedThisSession, onOpenGuess }: GridCellProps) {
  const { guess } = cell;
  const canOpen = guess === null || !guess.locked;

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
        isCorrect={guess.isCorrect}
        attemptCount={guess.attemptCount}
        locked={guess.locked}
        roundStatus={roundStatus}
        uniquePercent={guess.uniquePercent}
        livePoints={guess.livePoints}
        roundEndTime={roundEndTime}
        submittedThisSession={submittedThisSession}
        rowCategoryType={cell.rowCategoryType}
        rowCategoryValue={cell.rowCategoryValue}
        colCategoryType={cell.colCategoryType}
        colCategoryValue={cell.colCategoryValue}
      />
    );

  // data-testid is shared by both branches below so E2E tests can re-select
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

  // A locked cell (correct-and-live, or out of attempts) can no longer open
  // the guess input, but state 1's CellState now renders its own focusable
  // reveal-toggle button (S-019) — nesting that inside a disabled <button>
  // would make it unreachable by keyboard (and interactive-in-interactive is
  // invalid HTML besides). `role="group"` + `aria-disabled` on a plain
  // container keeps the same "not open-able" semantics for E2E assertions
  // (Playwright's toBeDisabled/toBeEnabled only honor `aria-disabled` on
  // elements whose role appears in its aria-disabled-eligible role list,
  // which "group" does but a bare <div>'s implicit role does not) without
  // disabling CellState's own controls.
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

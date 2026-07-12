import { CellState, type RoundStatus } from './CellState';
import type { CurrentRoundCell } from '../lib/types';

export interface GridCellProps {
  cell: CurrentRoundCell;
  roundStatus: RoundStatus;
  roundEndTime: string;
  knownPlayerName?: string;
  onOpenGuess: (cell: CurrentRoundCell) => void;
}

// Guard clause for the implicit fifth visual (an unattempted cell) lives
// here, before CellState's four-state logic — not a fifth case inside that
// component (SCREEN-01a).
export function GridCell({ cell, roundStatus, roundEndTime, knownPlayerName, onOpenGuess }: GridCellProps) {
  const { guess } = cell;
  const canOpen = guess === null || !guess.locked;

  const content =
    guess === null ? (
      <span className="grid-cell__empty" aria-hidden="true">
        +
      </span>
    ) : (
      <CellState
        // knownPlayerName (this browser session's own submission) wins
        // when present since it's the freshest; guess.submittedName
        // (REQ-303) is what makes a cell answered before this session
        // still show a name after a reload — POST .../guesses' own
        // response doesn't echo the name back, so the session cache can't
        // be retired in favor of it.
        playerName={knownPlayerName ?? guess.submittedName}
        isCorrect={guess.isCorrect}
        attemptCount={guess.attemptCount}
        locked={guess.locked}
        roundStatus={roundStatus}
        uniquePercent={guess.uniquePercent}
        livePoints={guess.livePoints}
        roundEndTime={roundEndTime}
        // S-020: knownPlayerName is only ever set by GridScreen right when
        // this browser session submitted this cell's guess (see CellState's
        // submittedThisSession doc comment) — a guess loaded from GET
        // /rounds/current on page load has no entry here, so this is false
        // for that case, same as intended.
        submittedThisSession={knownPlayerName != null}
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

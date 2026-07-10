import { CellState, type RoundStatus } from './CellState';
import type { CurrentRoundCell } from '../lib/types';

export interface GridCellProps {
  cell: CurrentRoundCell;
  roundStatus: RoundStatus;
  knownPlayerName?: string;
  onOpenGuess: (cell: CurrentRoundCell) => void;
}

// Guard clause for the implicit fifth visual (an unattempted cell) lives
// here, before CellState's four-state logic — not a fifth case inside that
// component (SCREEN-01a).
export function GridCell({ cell, roundStatus, knownPlayerName, onOpenGuess }: GridCellProps) {
  const { guess } = cell;
  const canOpen = guess === null || !guess.locked;

  return (
    <button
      type="button"
      className="grid-cell"
      disabled={!canOpen}
      onClick={canOpen ? () => onOpenGuess(cell) : undefined}
      aria-label={
        guess === null
          ? `Guess ${cell.rowCategoryValue} × ${cell.colCategoryValue}`
          : undefined
      }
    >
      {guess === null ? (
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
        />
      )}
    </button>
  );
}

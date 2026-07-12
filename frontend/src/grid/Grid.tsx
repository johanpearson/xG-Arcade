import { CategoryLabel } from './CategoryLabel';
import { GridCell } from './GridCell';
import type { RoundStatus } from './CellState';
import type { CurrentRoundCell } from '../lib/types';
import './Grid.css';

export interface GridProps {
  cells: CurrentRoundCell[];
  roundStatus: RoundStatus;
  roundEndTime: string;
  // S-020: cellIds whose guess was submitted in this browser session — see
  // GridCell's own doc comment on why this (not a name) is what's tracked.
  submittedThisSessionCellIds: ReadonlySet<string>;
  onCellClick: (cell: CurrentRoundCell) => void;
}

// SCREEN-01: cells arrive as a flat (row, col)-sorted array — row/column
// headers and the (row, col) grid layout are derived here, not assumed to
// be pre-grouped, and work for any N×N size (seeded rounds are sometimes
// just one cell).
export function Grid({ cells, roundStatus, roundEndTime, submittedThisSessionCellIds, onCellClick }: GridProps) {
  const rowHeaders = uniqueByAxis(cells, 'row');
  const colHeaders = uniqueByAxis(cells, 'col');
  const cellByPosition = new Map(cells.map((cell) => [positionKey(cell.row, cell.col), cell]));

  return (
    <div className="grid-scroll">
      <table className="grid-table">
        <thead>
          <tr>
            <th className="grid-table__corner" aria-hidden="true" />
            {colHeaders.map((col) => (
              <th key={`col-${col.col}`} scope="col" className="grid-table__col-header">
                <CategoryLabel
                  categoryType={col.colCategoryType}
                  value={col.colCategoryValue}
                  size="small"
                />
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rowHeaders.map((row) => (
            <tr key={`row-${row.row}`}>
              <th scope="row" className="grid-table__row-header">
                <CategoryLabel
                  categoryType={row.rowCategoryType}
                  value={row.rowCategoryValue}
                  size="small"
                />
              </th>
              {colHeaders.map((col) => {
                const cell = cellByPosition.get(positionKey(row.row, col.col));
                return (
                  <td key={`cell-${row.row}-${col.col}`} className="grid-table__cell">
                    {cell ? (
                      <GridCell
                        cell={cell}
                        roundStatus={roundStatus}
                        roundEndTime={roundEndTime}
                        submittedThisSession={submittedThisSessionCellIds.has(cell.cellId)}
                        onOpenGuess={onCellClick}
                      />
                    ) : null}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function positionKey(row: number, col: number): string {
  return `${row}-${col}`;
}

function uniqueByAxis(
  cells: CurrentRoundCell[],
  axis: 'row' | 'col',
): CurrentRoundCell[] {
  const seen = new Map<number, CurrentRoundCell>();
  for (const cell of cells) {
    if (!seen.has(cell[axis])) seen.set(cell[axis], cell);
  }
  return [...seen.values()].sort((a, b) => a[axis] - b[axis]);
}

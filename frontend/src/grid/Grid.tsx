import { CategoryLabel } from '../components/CategoryLabel';
import { GridCell } from './GridCell';
import type { RoundStatus } from './CellState';
import type { CurrentRoundCell } from '../lib/types';
import './Grid.css';

export interface GridProps {
  cells: CurrentRoundCell[];
  roundStatus: RoundStatus;
  // S-020: cellIds whose guess was submitted in this browser session — see
  // GridCell's own doc comment on why this (not a name) is what's tracked.
  submittedThisSessionCellIds: ReadonlySet<string>;
  onCellClick: (cell: CurrentRoundCell) => void;
}

// SCREEN-01: cells arrive as a flat (row, col)-sorted array — row/column
// headers and the (row, col) grid layout are derived here, not assumed to
// be pre-grouped, and work for any N×N size (seeded rounds are sometimes
// just one cell).
export function Grid({ cells, roundStatus, submittedThisSessionCellIds, onCellClick }: GridProps) {
  const rowHeaders = uniqueByAxis(cells, 'row');
  const colHeaders = uniqueByAxis(cells, 'col');
  const cellByPosition = new Map(cells.map((cell) => [positionKey(cell.row, cell.col), cell]));

  return (
    <div className="grid-scroll">
      <table className="grid-table">
        {/* S-040: explicit <col> widths are what actually let the ≤480px
            breakpoint's table-layout: fixed enforce the row-header column's
            width cap — a plain CSS max-width on .grid-table__row-header
            alone is not enough, since the browser's column-width algorithm
            reads it from the *first row's* cell in that column (the empty
            corner cell in <thead>), not from the row-header cells that
            actually live in <tbody> (Grid.css).

            S-055: every data <col> now also carries `grid-table__data-col`
            (previously unclassed) — table-layout: fixed applies at every
            breakpoint as of this story (Grid.css), not just ≤480px, and an
            unclassed <col> would fall back to that algorithm's "equally
            divide whatever width is left" rule, which only produces equal
            columns when the *table's* own width is itself a known,
            deliberate total (true at ≤480px, where the table is forced to
            width: 100%; not true above it, where the table is deliberately
            left at width: auto/shrink-to-fit, per S-047/S-049 — see
            Grid.css). Giving every data column the same explicit width
            directly is what makes columns uniform at those breakpoints too,
            regardless of table width being open-ended. */}
        <colgroup>
          <col className="grid-table__row-header-col" />
          {colHeaders.map((col) => (
            <col key={`colgroup-${col.col}`} className="grid-table__data-col" />
          ))}
        </colgroup>
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
                // Product feedback (2026-08-03): a correct cell (SCREEN-01a
                // states 1/4 — round-active or round-closed, `guess.isCorrect`
                // either way) gets a persistent light-green border, not just
                // the checkmark/points text tint — see design-document.md
                // SCREEN-01a's matching note. Applied here, on the `<td>`
                // itself, rather than on `.grid-cell` (the button, GridCell.tsx)
                // or `.cell-state`/`.cell-state--photo` (CellState.tsx):
                // `.cell-state--photo`'s photo layer bleeds via `inset: 0` only
                // as far as this element's own *padding* edge (Grid.css's
                // `.grid-table__cell`/S-050 comment), never into its *border*
                // area — so a border declared here is spatially guaranteed to
                // never sit under the photo, in either variant, regardless of
                // stacking order. A border on `.grid-cell` instead would risk
                // exactly that: it occupies the same box the photo bleeds to,
                // and (verified against Grid.css's own painting-order notes)
                // a positioned descendant like `.cell-state--photo` paints
                // after `.grid-cell`'s own non-positioned border in that case.
                const isCorrectCell = cell?.guess?.isCorrect === true;
                // REQ-216 (2026-08-03): the same reasoning that put the
                // correct-cell border here (not `.grid-cell`/`CellState.tsx`)
                // now applies to a locked-incorrect cell too — REQ-216 can
                // put a full-bleed matched-player photo or placeholder
                // avatar on an incorrect cell the same way a correct cell
                // can have one, so the border needs the identical guarantee
                // of rendering above/around that layer regardless of
                // stacking order. Only once locked: state 2 (an attempt
                // remains) still gets no border at all, unaffected.
                const isLockedIncorrectCell = cell?.guess?.locked === true && cell?.guess?.isCorrect === false;
                const cellClassName = isCorrectCell
                  ? 'grid-table__cell grid-table__cell--correct'
                  : isLockedIncorrectCell
                    ? 'grid-table__cell grid-table__cell--incorrect'
                    : 'grid-table__cell';
                return (
                  <td key={`cell-${row.row}-${col.col}`} className={cellClassName}>
                    {cell ? (
                      <GridCell
                        cell={cell}
                        roundStatus={roundStatus}
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

import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Grid } from './Grid';
import type { CurrentRoundCell } from '../lib/types';

// S-047 (design-document.md §4's "Grid cell aspect ratio" note): a Tier-0
// (3x3) grid's cells must not stretch into flat/short rectangles just
// because the viewport is wide. Root cause was `.grid-table`'s
// unconditional `width: 100%` combined with the browser's default
// `table-layout: auto` above 480px — jsdom has no real table-layout engine
// (no box model, so it can't report the actual rendered cell width/height
// the way a real browser could), so this test checks the CSS *mechanism*
// itself (the declared width/margin on `.grid-table`) rather than a
// computed pixel box, the same "check the layout-affecting properties, not
// a snapshot" approach CellState.test.tsx's REQ-214 footprint tests already
// use. jsdom's default viewport (1024px wide) is itself above the 480px
// breakpoint, so this exercises the un-media-queried base rule directly —
// see this story's own real-browser verification for the pixel-level check
// this doesn't replace.
function threeByThreeCells(): CurrentRoundCell[] {
  const rows = ['France', 'Brazil', 'Spain'];
  const cols = ['Arsenal', 'Milan', 'Bayern'];
  const cells: CurrentRoundCell[] = [];
  rows.forEach((rowValue, row) => {
    cols.forEach((colValue, col) => {
      cells.push({
        cellId: `cell-${row}-${col}`,
        row,
        col,
        rowCategoryType: 'country',
        rowCategoryValue: rowValue,
        colCategoryType: 'club',
        colCategoryValue: colValue,
        guess: null,
      });
    });
  });
  return cells;
}

describe('Grid table sizing (S-047)', () => {
  it("does not force .grid-table to width: 100% above the 480px breakpoint — the fix that let a Tier-0 grid's few columns stretch to fill the viewport instead of staying close to their own min-width/height floor", () => {
    const { container } = render(
      <Grid
        cells={threeByThreeCells()}
        roundStatus="active"
        submittedThisSessionCellIds={new Set()}
        onCellClick={() => {}}
      />,
    );

    const table = container.querySelector('.grid-table');
    expect(table).toBeInTheDocument();
    const style = getComputedStyle(table as Element);
    // Not '100%' — the table now sizes from its own columns' content/
    // min-width floor (shrink-to-fit) at this (>480px) viewport, rather
    // than being forced to fill whatever width the container happens to
    // have.
    expect(style.width).not.toBe('100%');
    expect(style.width).toBe('auto');
    // Centers the (now possibly narrower-than-container) table instead of
    // leaving it hard left-aligned.
    expect(style.marginLeft).toBe('auto');
    expect(style.marginRight).toBe('auto');
  });

  it('keeps every data cell at the same min-width/height touch-target floor regardless of column count — the actual mechanism (per CSS2.1 automatic table layout) that keeps cells close to square once the table itself is no longer force-stretched', () => {
    const { container } = render(
      <Grid
        cells={threeByThreeCells()}
        roundStatus="active"
        submittedThisSessionCellIds={new Set()}
        onCellClick={() => {}}
      />,
    );

    const tableCells = container.querySelectorAll('.grid-table__cell');
    expect(tableCells.length).toBe(9);
    for (const cell of tableCells) {
      const style = getComputedStyle(cell);
      // min-width and height are both driven by the same
      // --touch-target-min (44px) token at this viewport — the floor that,
      // combined with the table no longer being forced to width: 100%,
      // keeps a data cell's own width from ballooning independently of its
      // height.
      expect(style.minWidth).toBe('var(--touch-target-min)');
      expect(style.height).toBe('var(--touch-target-min)');
    }
  });
});

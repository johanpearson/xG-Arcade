import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Grid } from './Grid';
import type { CurrentRoundCell } from '../lib/types';
// S-049: the desktop (≥960px) target size lives inside an `@media
// (min-width: 960px)` block, which jsdom's getComputedStyle never applies
// regardless of window width — confirmed directly (`window.matchMedia` isn't
// even implemented in this jsdom version), which is also why every existing
// test in this file already scopes itself to "the un-media-queried base
// rule" rather than attempting to assert a media-scoped value via computed
// style. Reading the stylesheet's raw source text is the only way to make
// this story's specific numeric change (64px floor → 120px target)
// regression-testable at the unit level; it's a source-text check, not a
// computed-style one, and doesn't replace the real-browser verification this
// story's own backlog entry records for the actual rendered pixel sizes.
import gridCss from './Grid.css?raw';

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

// Extracts a top-level `{ ... }` block's contents starting at `openBraceIndex`
// (the index of that block's own `{`) by counting brace depth, rather than
// matching against whatever selector happens to follow the block in the
// source text. S-049's own quality-gate review flagged the original version
// of this file (a regex anchored on the literal `.grid-cell {` that happened
// to follow the ≥960px block) as fragile — any reformat or inserted rule
// between the two would break the test for a reason unrelated to the actual
// regression this story cares about. This has no such dependency: it stops
// at the first brace that brings the depth back to 0, wherever that is.
function extractBraceBlock(source: string, openBraceIndex: number): string {
  let depth = 0;
  for (let i = openBraceIndex; i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}') {
      depth--;
      if (depth === 0) return source.slice(openBraceIndex + 1, i);
    }
  }
  throw new Error('unbalanced braces: no matching close brace found');
}

describe('Grid desktop cell target size (S-049)', () => {
  it("gives .grid-table__cell a real target size at ≥960px (120px), not just S-047/S-040's 64px floor — extracted from the raw stylesheet since jsdom can't apply media-scoped computed styles (see this file's own import comment)", () => {
    const mediaOpenIndex = gridCss.indexOf('@media (min-width: 960px) {');
    expect(mediaOpenIndex, 'expected to find the ≥960px desktop media block in Grid.css').toBeGreaterThanOrEqual(0);
    const desktopBlock = extractBraceBlock(gridCss, mediaOpenIndex + '@media (min-width: 960px) '.length);

    const cellRuleIndex = desktopBlock.indexOf('.grid-table__cell {');
    expect(cellRuleIndex, 'expected .grid-table__cell to still be styled inside the ≥960px block').toBeGreaterThanOrEqual(
      0,
    );
    const cellRule = extractBraceBlock(desktopBlock, cellRuleIndex + '.grid-table__cell '.length);

    expect(cellRule).toContain('min-width: 120px');
    expect(cellRule).toContain('height: 120px');
    // Not the old S-040/S-047 floor value — this story raises it, not just
    // re-derives the same number under a new comment.
    expect(cellRule).not.toContain('64px');
    // Padding grows in step with the bigger footprint (design-document.md
    // §4's S-049 note) rather than leaving a larger empty box around the
    // same tight S-040 spacing.
    expect(cellRule).toContain('padding: var(--space-3)');
  });

  it('leaves the 481-959px shrink-to-fit range and the ≤480px table-layout: fixed range untouched by the ≥960px target-size change', () => {
    // "120px" appears only inside the ≥960px block asserted above — this
    // story is scoped to that breakpoint only (design-document.md §4's
    // S-049 note). Checked by subtracting the desktop block's own count from
    // the whole file's count (rather than asserting a fixed global count of
    // 2), so an unrelated future "120px" value elsewhere in this stylesheet
    // fails this test for the right reason — appearing outside the ≥960px
    // block — not merely by changing the total.
    const mediaOpenIndex = gridCss.indexOf('@media (min-width: 960px) {');
    const desktopBlock = extractBraceBlock(gridCss, mediaOpenIndex + '@media (min-width: 960px) '.length);
    const totalOccurrences = gridCss.split('120px').length - 1;
    const desktopBlockOccurrences = desktopBlock.split('120px').length - 1;
    expect(totalOccurrences).toBe(desktopBlockOccurrences);

    // S-040's ≤480px fixed-layout block (unrelated breakpoint) keeps its own
    // actual values, not just the strings that mention it — extracted the
    // same brace-matching way as the desktop block above, so a future value
    // regression there (not just a deleted comment) would fail this test.
    const mobileMediaOpenIndex = gridCss.indexOf('@media (max-width: 480px) {');
    expect(mobileMediaOpenIndex, 'expected to find the ≤480px mobile media block in Grid.css').toBeGreaterThanOrEqual(
      0,
    );
    const mobileBlock = extractBraceBlock(gridCss, mobileMediaOpenIndex + '@media (max-width: 480px) '.length);

    const mobileTableRuleIndex = mobileBlock.indexOf('.grid-table {');
    expect(mobileTableRuleIndex, 'expected .grid-table to still be styled inside the ≤480px block').toBeGreaterThanOrEqual(
      0,
    );
    const mobileTableRule = extractBraceBlock(mobileBlock, mobileTableRuleIndex + '.grid-table '.length);
    expect(mobileTableRule).toContain('table-layout: fixed');
    expect(mobileTableRule).toContain('width: 100%');

    const rowHeaderColRuleIndex = mobileBlock.indexOf('.grid-table__row-header-col {');
    expect(
      rowHeaderColRuleIndex,
      'expected .grid-table__row-header-col to still be styled inside the ≤480px block',
    ).toBeGreaterThanOrEqual(0);
    const rowHeaderColRule = extractBraceBlock(mobileBlock, rowHeaderColRuleIndex + '.grid-table__row-header-col '.length);
    expect(rowHeaderColRule).toContain('width: 88px');
  });
});

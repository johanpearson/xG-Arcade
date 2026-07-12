import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { GridCell } from './GridCell';
import type { CurrentRoundCell } from '../lib/types';

const baseCell: Omit<CurrentRoundCell, 'guess'> = {
  cellId: 'cell-1',
  row: 0,
  col: 0,
  rowCategoryType: 'country',
  rowCategoryValue: 'France',
  colCategoryType: 'club',
  colCategoryValue: 'Arsenal',
};

// S-019: GridCell renders a real <button> when a cell can still open the
// guess-submit UI, and a plain <div role="group" aria-disabled="true">
// once it can't (correct+live, or out of attempts) — the div branch exists
// so CellState's own reveal-toggle button (state 1) is independently
// focusable, rather than nested inside a disabled <button> (which would be
// both unreachable by keyboard and invalid HTML). role="group" is what
// keeps `aria-disabled` meaningful for anything that checks disabled state
// the way a bare <div> alone would not (Playwright's toBeDisabled/
// toBeEnabled, exercised in tests/e2e/play-grid.spec.ts, only honor
// `aria-disabled` for ARIA roles in its disabled-eligible role list, which
// "group" is on and a bare <div>'s implicit role is not).
describe('GridCell', () => {
  it('REQ-201: an unattempted cell renders a real, enabled button that opens the guess UI on click', async () => {
    const user = userEvent.setup();
    const onOpenGuess = vi.fn();
    render(
      <GridCell
        cell={{ ...baseCell, guess: null }}
        roundStatus="active"
        roundEndTime="2026-07-11T18:00:00Z"
        onOpenGuess={onOpenGuess}
      />,
    );

    const cell = screen.getByRole('button', { name: 'Guess France × Arsenal' });
    expect(cell).toBeEnabled();

    await user.click(cell);
    expect(onOpenGuess).toHaveBeenCalledWith({ ...baseCell, guess: null });
  });

  it('REQ-210: an incorrect cell with attempts remaining renders a real, enabled button (still open-able for another guess)', () => {
    render(
      <GridCell
        cell={{
          ...baseCell,
          guess: {
            isCorrect: false,
            attemptCount: 1,
            locked: false,
            submittedName: 'Wrong Guess',
            uniquePercent: null,
            livePoints: null,
          },
        }}
        roundStatus="active"
        roundEndTime="2026-07-11T18:00:00Z"
        onOpenGuess={vi.fn()}
      />,
    );

    expect(screen.getByTestId('grid-cell-cell-1').tagName).toBe('BUTTON');
    expect(screen.getByTestId('grid-cell-cell-1')).toBeEnabled();
  });

  it('S-019: a correct+locked cell (state 1) renders a non-button container marked aria-disabled, not a disabled <button>, so its inner reveal-toggle stays keyboard-focusable', () => {
    render(
      <GridCell
        cell={{
          ...baseCell,
          guess: {
            isCorrect: true,
            attemptCount: 1,
            locked: true,
            submittedName: 'Henry',
            uniquePercent: 0.12,
            livePoints: 12,
          },
        }}
        roundStatus="active"
        roundEndTime="2026-07-11T18:00:00Z"
        onOpenGuess={vi.fn()}
      />,
    );

    const cell = screen.getByTestId('grid-cell-cell-1');
    expect(cell.tagName).toBe('DIV');
    expect(cell).toHaveAttribute('role', 'group');
    expect(cell).toHaveAttribute('aria-disabled', 'true');

    // The reveal-toggle (LiveMetaDisclosure) must still be a real, reachable
    // button — not trapped inside a disabled ancestor.
    const toggle = screen.getByRole('button', { name: /live/i });
    expect(toggle).toBeEnabled();
  });

  it('REQ-210: an incorrect, out-of-attempts cell (state 3) also renders a non-button container marked aria-disabled', () => {
    render(
      <GridCell
        cell={{
          ...baseCell,
          guess: {
            isCorrect: false,
            attemptCount: 2,
            locked: true,
            submittedName: 'Wrong Guess',
            uniquePercent: null,
            livePoints: null,
          },
        }}
        roundStatus="active"
        roundEndTime="2026-07-11T18:00:00Z"
        onOpenGuess={vi.fn()}
      />,
    );

    const cell = screen.getByTestId('grid-cell-cell-1');
    expect(cell.tagName).toBe('DIV');
    expect(cell).toHaveAttribute('role', 'group');
    expect(cell).toHaveAttribute('aria-disabled', 'true');
  });
});

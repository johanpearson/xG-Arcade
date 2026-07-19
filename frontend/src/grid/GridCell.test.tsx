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

// S-041 (REQ-212): GridCell now renders one of three shapes. (a) canOpen —
// unattempted or unlocked, a real enabled <button> that opens the guess UI
// (unchanged). (b) isRevealable (locked+correct, states 1/4) — now ALSO a
// real, enabled <button> (no longer a disabled div with a nested toggle),
// whose click/tap toggles GridCell's own `revealed` state and exposes
// aria-expanded. (c) locked+incorrect (state 3, or state 4's incorrect
// outcome) — still the non-interactive <div role="group"
// aria-disabled="true">, never a click target for reveal.
describe('GridCell', () => {
  it('REQ-201: an unattempted cell renders a real, enabled button that opens the guess UI on click', async () => {
    const user = userEvent.setup();
    const onOpenGuess = vi.fn();
    render(<GridCell cell={{ ...baseCell, guess: null }} roundStatus="active" onOpenGuess={onOpenGuess} />);

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
            resolvedPlayerName: null,
            uniquePercent: null,
            livePoints: null,
          },
        }}
        roundStatus="active"
        onOpenGuess={vi.fn()}
      />,
    );

    expect(screen.getByTestId('grid-cell-cell-1').tagName).toBe('BUTTON');
    expect(screen.getByTestId('grid-cell-cell-1')).toBeEnabled();
  });

  it('REQ-212: a locked+correct cell (state 1) renders a real, enabled button — not a disabled div — with aria-expanded="false" at rest and no name shown yet', () => {
    render(
      <GridCell
        cell={{
          ...baseCell,
          guess: {
            isCorrect: true,
            attemptCount: 1,
            locked: true,
            submittedName: 'henry',
            resolvedPlayerName: 'Henry',
            uniquePercent: 0.12,
            livePoints: 12,
          },
        }}
        roundStatus="active"
        onOpenGuess={vi.fn()}
      />,
    );

    const cell = screen.getByTestId('grid-cell-cell-1');
    expect(cell.tagName).toBe('BUTTON');
    expect(cell).toBeEnabled();
    expect(cell).toHaveAttribute('aria-expanded', 'false');
    expect(cell).toHaveAttribute('aria-label', 'Show guessed player for France × Arsenal');
    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
  });

  it('REQ-212: clicking a locked+correct cell reveals the guessed player name and badge dock, toggles aria-expanded to true, and updates the aria-label; a second click hides it again', async () => {
    const user = userEvent.setup();
    render(
      <GridCell
        cell={{
          ...baseCell,
          guess: {
            isCorrect: true,
            attemptCount: 1,
            locked: true,
            submittedName: 'henry',
            resolvedPlayerName: 'Henry',
            uniquePercent: 0.12,
            livePoints: 12,
          },
        }}
        roundStatus="active"
        onOpenGuess={vi.fn()}
      />,
    );

    const cell = screen.getByTestId('grid-cell-cell-1');

    await user.click(cell);
    expect(cell).toHaveAttribute('aria-expanded', 'true');
    expect(cell).toHaveAttribute('aria-label', 'Hide guessed player for France × Arsenal');
    expect(screen.getByText('Henry')).toBeInTheDocument();
    expect(screen.getByTestId('badge-dock-row')).toBeInTheDocument();
    expect(screen.getByTestId('badge-dock-col')).toBeInTheDocument();

    await user.click(cell);
    expect(cell).toHaveAttribute('aria-expanded', 'false');
    expect(cell).toHaveAttribute('aria-label', 'Show guessed player for France × Arsenal');
    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
  });

  it('REQ-212: keyboard activation (Enter) of a locked+correct cell produces the same reveal toggle as a click', async () => {
    const user = userEvent.setup();
    render(
      <GridCell
        cell={{
          ...baseCell,
          guess: {
            isCorrect: true,
            attemptCount: 1,
            locked: true,
            submittedName: 'henry',
            resolvedPlayerName: 'Henry',
            uniquePercent: 0.12,
            livePoints: 12,
          },
        }}
        roundStatus="active"
        onOpenGuess={vi.fn()}
      />,
    );

    const cell = screen.getByTestId('grid-cell-cell-1');
    await user.tab();
    expect(cell).toHaveFocus();

    await user.keyboard('{Enter}');
    expect(cell).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('Henry')).toBeInTheDocument();

    await user.keyboard('{Enter}');
    expect(cell).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
  });

  it('REQ-212: keyboard activation (Space) of a locked+correct cell produces the same reveal toggle as a click', async () => {
    const user = userEvent.setup();
    render(
      <GridCell
        cell={{
          ...baseCell,
          guess: {
            isCorrect: true,
            attemptCount: 1,
            locked: true,
            submittedName: 'henry',
            resolvedPlayerName: 'Henry',
            uniquePercent: 0.12,
            livePoints: 12,
          },
        }}
        roundStatus="active"
        onOpenGuess={vi.fn()}
      />,
    );

    const cell = screen.getByTestId('grid-cell-cell-1');
    await user.tab();
    expect(cell).toHaveFocus();

    await user.keyboard(' ');
    expect(cell).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('Henry')).toBeInTheDocument();
  });

  // REQ-214 (2026-07-18 status note): end-to-end wiring check that GridCell
  // actually forwards the guess's photo field down to CellState, and that
  // the photo shows at rest (no click) while the name stays click/tap-gated
  // — CellState.test.tsx covers the rendering/fallback rules themselves via
  // constructed props directly, this just confirms GridCell doesn't drop
  // the field on the way through, and that GridCell's own `revealed` state
  // (which it owns, unlike the photo) doesn't accidentally gate the photo
  // too.
  it('REQ-214: a locked+correct cell with a resolvedPlayerPhotoUrl shows the photo immediately at rest, before any click — the name stays hidden until clicked', () => {
    const { container } = render(
      <GridCell
        cell={{
          ...baseCell,
          guess: {
            isCorrect: true,
            attemptCount: 1,
            locked: true,
            submittedName: 'henry',
            resolvedPlayerName: 'Henry',
            resolvedPlayerPhotoUrl: 'https://example.test/henry.jpg',
            uniquePercent: 0.12,
            livePoints: 12,
          },
        }}
        roundStatus="active"
        onOpenGuess={vi.fn()}
      />,
    );

    const img = container.querySelector('.cell-state__photo-img');
    expect(img).toBeInTheDocument();
    expect(img).toHaveAttribute('src', 'https://example.test/henry.jpg');
    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
  });

  it('REQ-214: clicking a locked+correct cell with a resolvedPlayerPhotoUrl reveals the name over the already-showing photo; a second click hides the name again but the photo stays', async () => {
    const user = userEvent.setup();
    const { container } = render(
      <GridCell
        cell={{
          ...baseCell,
          guess: {
            isCorrect: true,
            attemptCount: 1,
            locked: true,
            submittedName: 'henry',
            resolvedPlayerName: 'Henry',
            resolvedPlayerPhotoUrl: 'https://example.test/henry.jpg',
            uniquePercent: 0.12,
            livePoints: 12,
          },
        }}
        roundStatus="active"
        onOpenGuess={vi.fn()}
      />,
    );

    const cell = screen.getByTestId('grid-cell-cell-1');
    expect(container.querySelector('.cell-state__photo-img')).toBeInTheDocument();

    await user.click(cell);
    expect(screen.getByText('Henry')).toBeInTheDocument();
    let img = container.querySelector('.cell-state__photo-img');
    expect(img).toBeInTheDocument();
    expect(img).toHaveAttribute('src', 'https://example.test/henry.jpg');

    await user.click(cell);
    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
    // The photo is unaffected by the second click — it was never gated by
    // this toggle in the first place.
    img = container.querySelector('.cell-state__photo-img');
    expect(img).toBeInTheDocument();
  });

  it('REQ-214: a locked, incorrect cell never renders a photo even if the guess somehow carries a resolvedPlayerPhotoUrl, unchanged from the name rule (state 3, non-interactive)', () => {
    const { container } = render(
      <GridCell
        cell={{
          ...baseCell,
          guess: {
            isCorrect: false,
            attemptCount: 2,
            locked: true,
            submittedName: 'Wrong Guess',
            resolvedPlayerName: null,
            resolvedPlayerPhotoUrl: 'https://example.test/should-never-show.jpg',
            uniquePercent: null,
            livePoints: null,
          },
        }}
        roundStatus="active"
        onOpenGuess={vi.fn()}
      />,
    );

    expect(container.querySelector('.cell-state__photo-img')).not.toBeInTheDocument();
  });

  it('REQ-212: an incorrect, out-of-attempts cell (state 3) remains a non-interactive aria-disabled div, is never a click target for reveal, and exposes no aria-expanded/button role at all', async () => {
    const user = userEvent.setup();
    render(
      <GridCell
        cell={{
          ...baseCell,
          guess: {
            isCorrect: false,
            attemptCount: 2,
            locked: true,
            submittedName: 'Wrong Guess',
            resolvedPlayerName: null,
            uniquePercent: null,
            livePoints: null,
          },
        }}
        roundStatus="active"
        onOpenGuess={vi.fn()}
      />,
    );

    const cell = screen.getByTestId('grid-cell-cell-1');
    expect(cell.tagName).toBe('DIV');
    expect(cell).toHaveAttribute('role', 'group');
    expect(cell).toHaveAttribute('aria-disabled', 'true');
    expect(cell).not.toHaveAttribute('aria-expanded');
    expect(screen.queryByRole('button')).not.toBeInTheDocument();

    // Clicking it (there is no button to click, but assert the whole
    // element produces no reveal — no name ever appears for a wrong guess).
    await user.click(cell);
    expect(screen.queryByText('Wrong Guess')).not.toBeInTheDocument();
  });

  it('REQ-212: state 4\'s incorrect outcome (round closed, wrong guess) also stays a non-interactive aria-disabled div', () => {
    render(
      <GridCell
        cell={{
          ...baseCell,
          guess: {
            isCorrect: false,
            attemptCount: 2,
            locked: true,
            submittedName: 'Wrong Guess',
            resolvedPlayerName: null,
            uniquePercent: null,
            livePoints: null,
          },
        }}
        roundStatus="closed"
        onOpenGuess={vi.fn()}
      />,
    );

    const cell = screen.getByTestId('grid-cell-cell-1');
    expect(cell.tagName).toBe('DIV');
    expect(cell).toHaveAttribute('role', 'group');
    expect(cell).toHaveAttribute('aria-disabled', 'true');
    expect(cell).not.toHaveAttribute('aria-expanded');
  });
});

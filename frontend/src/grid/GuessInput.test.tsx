import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { GuessInput } from './GuessInput';
import type { CurrentRoundCell } from '../lib/types';

function makeCell(overrides: Partial<CurrentRoundCell> = {}): CurrentRoundCell {
  return {
    cellId: 'cell-1',
    row: 0,
    col: 0,
    rowCategoryType: 'country',
    rowCategoryValue: 'France',
    colCategoryType: 'club',
    colCategoryValue: 'Arsenal',
    guess: null,
    ...overrides,
  };
}

// SCREEN-02: plain text input, no autocomplete (REQ-207 deferred / Tier 0).
describe('GuessInput', () => {
  it('shows the category header with both flag and club badge context', () => {
    render(<GuessInput cell={makeCell()} onSubmit={vi.fn()} onClose={vi.fn()} />);

    expect(screen.getByText('France')).toBeInTheDocument();
    expect(screen.getByText('Arsenal')).toBeInTheDocument();
  });

  it('shows no attempt count line for an untried cell', () => {
    render(<GuessInput cell={makeCell()} onSubmit={vi.fn()} onClose={vi.fn()} />);

    expect(screen.queryByText(/attempts used/)).not.toBeInTheDocument();
  });

  it('shows the attempt count once at least one attempt has been used', () => {
    const cell = makeCell({ guess: { isCorrect: false, attemptCount: 1, locked: false } });
    render(<GuessInput cell={cell} onSubmit={vi.fn()} onClose={vi.fn()} />);

    expect(screen.getByText('1 of 2 attempts used')).toBeInTheDocument();
  });

  it('submits the typed name and closes on success', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const onClose = vi.fn();
    render(<GuessInput cell={makeCell()} onSubmit={onSubmit} onClose={onClose} />);

    await user.type(screen.getByLabelText('Player name'), 'Thierry Henry');
    await user.click(screen.getByRole('button', { name: 'Submit guess' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith('Thierry Henry'));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
  });

  it('shows the server error detail and stays open on failure', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockRejectedValue(new Error('No attempts remaining'));
    const onClose = vi.fn();
    render(<GuessInput cell={makeCell()} onSubmit={onSubmit} onClose={onClose} />);

    await user.type(screen.getByLabelText('Player name'), 'Someone');
    await user.click(screen.getByRole('button', { name: 'Submit guess' }));

    await waitFor(() => expect(screen.getByText('No attempts remaining')).toBeInTheDocument());
    expect(onClose).not.toHaveBeenCalled();
  });

  it('hides the input entirely once the cell is locked', () => {
    const cell = makeCell({ guess: { isCorrect: false, attemptCount: 2, locked: true } });
    render(<GuessInput cell={cell} onSubmit={vi.fn()} onClose={vi.fn()} />);

    expect(screen.queryByLabelText('Player name')).not.toBeInTheDocument();
    expect(screen.getByText(/locked/i)).toBeInTheDocument();
  });
});

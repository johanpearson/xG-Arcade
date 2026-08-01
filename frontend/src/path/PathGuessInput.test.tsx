import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { PathGuessInput } from './PathGuessInput';

describe('PathGuessInput', () => {
  it('REQ-1205: shows the clue counter against the puzzle\'s own fixed cap (7), not any other fixed number', () => {
    render(<PathGuessInput clueCount={4} guess={null} onSubmit={vi.fn()} />);

    expect(screen.getByText('Clue 4 of 7')).toBeInTheDocument();
  });

  it('REQ-1204: submitting a name calls onSubmit with the trimmed value', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue(true);

    render(<PathGuessInput clueCount={1} guess={null} onSubmit={onSubmit} />);

    await user.type(screen.getByLabelText('Player name'), '  Lionel Messi  ');
    await user.click(screen.getByRole('button', { name: 'Guess' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith('Lionel Messi'));
  });

  it('a rejected guess shows the shake cue and clears the field for another attempt', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue(false);

    render(<PathGuessInput clueCount={2} guess={null} onSubmit={onSubmit} />);

    await user.type(screen.getByLabelText('Player name'), 'Wrong Name');
    await user.click(screen.getByRole('button', { name: 'Guess' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalled());
    // key={shakeToken} (PathGuessInput.tsx) remounts the form to restart the
    // shake animation, same technique CellState.tsx's useShakeToken uses —
    // the field element itself is a new DOM node after the rejection, so
    // this re-queries rather than reusing a now-stale element reference.
    await waitFor(() => expect((screen.getByLabelText('Player name') as HTMLInputElement).value).toBe(''));
    expect(document.querySelector('.path-guess-input--shake')).toBeInTheDocument();
  });

  it('a network/API failure shows an inline error message, stating what happened', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockRejectedValue(new Error('Something went wrong. Please try again.'));

    render(<PathGuessInput clueCount={1} guess={null} onSubmit={onSubmit} />);

    await user.type(screen.getByLabelText('Player name'), 'Someone');
    await user.click(screen.getByRole('button', { name: 'Guess' }));

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
  });

  it('REQ-1204: once solved, the input and Guess button disable and no further submission is possible', () => {
    render(
      <PathGuessInput
        clueCount={3}
        guess={{
          isCorrect: true,
          attemptCount: 3,
          locked: true,
          submittedName: 'Lionel Messi',
          resolvedPlayerName: 'Lionel Messi',
          resolvedPlayerPhotoUrl: null,
        }}
        onSubmit={vi.fn()}
      />,
    );

    expect(screen.getByLabelText('Player name')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Guess' })).toBeDisabled();
  });

  it('REQ-1205: once the attempt cap is exhausted without a correct guess, the form is disabled and states what happened', () => {
    render(
      <PathGuessInput
        clueCount={7}
        guess={{
          isCorrect: false,
          attemptCount: 7,
          locked: true,
          submittedName: 'Wrong Name',
          resolvedPlayerName: null,
          resolvedPlayerPhotoUrl: null,
        }}
        onSubmit={vi.fn()}
      />,
    );

    expect(screen.getByLabelText('Player name')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Guess' })).toBeDisabled();
    expect(screen.getByText('No attempts remain for this puzzle.')).toBeInTheDocument();
  });
});

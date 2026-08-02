import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PathGuessInput } from './PathGuessInput';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

// A fetch stub that never resolves any suggestions — used by tests that
// aren't exercising autocomplete at all, so a debounced fetch that might
// still fire in the background (e.g. from typing a long guess) has
// something harmless to hit rather than a real network call. Same pattern
// GuessInput.test.tsx (xG Grid) uses for the identical, shared endpoint.
function stubNoSuggestions() {
  vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse([])));
}

// REQ-207 (S-091): PlayerNameIndex-backed autocomplete suggestions, wired
// into xG Path's guess field the same way GuessInput.tsx already does for
// xG Grid — see the REQ207-prefixed tests below for this story's own
// acceptance criteria. No REQ-209 disambiguation-picker tests here —
// deliberately out of scope, see PathGuessInput.tsx's own comment.
describe('PathGuessInput', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('REQ-1205: shows the clue counter against the puzzle\'s own fixed cap (7), not any other fixed number', () => {
    stubNoSuggestions();
    render(<PathGuessInput clueCount={4} guess={null} accessToken="token" onSubmit={vi.fn()} />);

    expect(screen.getByText('Clue 4 of 7')).toBeInTheDocument();
  });

  it('REQ-1204: submitting a name calls onSubmit with the trimmed value', async () => {
    stubNoSuggestions();
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue(true);

    render(<PathGuessInput clueCount={1} guess={null} accessToken="token" onSubmit={onSubmit} />);

    await user.type(screen.getByLabelText('Player name'), '  Lionel Messi  ');
    await user.click(screen.getByRole('button', { name: 'Guess' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith('Lionel Messi'));
  });

  it('a rejected guess shows the shake cue and clears the field for another attempt', async () => {
    stubNoSuggestions();
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue(false);

    render(<PathGuessInput clueCount={2} guess={null} accessToken="token" onSubmit={onSubmit} />);

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
    stubNoSuggestions();
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockRejectedValue(new Error('Something went wrong. Please try again.'));

    render(<PathGuessInput clueCount={1} guess={null} accessToken="token" onSubmit={onSubmit} />);

    await user.type(screen.getByLabelText('Player name'), 'Someone');
    await user.click(screen.getByRole('button', { name: 'Guess' }));

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
  });

  it('REQ-1204: once solved, the input and Guess button disable and no further submission is possible', () => {
    stubNoSuggestions();
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
        accessToken="token"
        onSubmit={vi.fn()}
      />,
    );

    expect(screen.getByLabelText('Player name')).toBeDisabled();
    // The field is empty (nothing was typed in this render), so the button
    // shows its "Next clue" label per the skip fix below — it's disabled
    // either way, since `disabled` is driven by `isCorrect`/`locked`, not by
    // which label happens to be showing.
    expect(screen.getByRole('button', { name: 'Next clue' })).toBeDisabled();
  });

  it('REQ-1205: once the attempt cap is exhausted without a correct guess, the form is disabled and states what happened', () => {
    stubNoSuggestions();
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
        accessToken="token"
        onSubmit={vi.fn()}
      />,
    );

    expect(screen.getByLabelText('Player name')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Next clue' })).toBeDisabled();
    expect(screen.getByText('No attempts remain for this puzzle.')).toBeInTheDocument();
  });

  // User-testing fix (2026-08-02): a player who doesn't want to guess yet
  // can now advance to the next clue without typing anything.
  it('the submit button reads "Next clue" while the field is empty and "Guess" once text is entered', async () => {
    stubNoSuggestions();
    const user = userEvent.setup();

    render(<PathGuessInput clueCount={1} guess={null} accessToken="token" onSubmit={vi.fn()} />);

    expect(screen.getByRole('button', { name: 'Next clue' })).toBeInTheDocument();

    await user.type(screen.getByLabelText('Player name'), 'Lionel Messi');
    expect(screen.getByRole('button', { name: 'Guess' })).toBeInTheDocument();

    await user.clear(screen.getByLabelText('Player name'));
    expect(screen.getByRole('button', { name: 'Next clue' })).toBeInTheDocument();
  });

  it('submitting an empty field calls onSubmit with a placeholder that can never match a real player, not a validation error', async () => {
    stubNoSuggestions();
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue(false);

    render(<PathGuessInput clueCount={1} guess={null} accessToken="token" onSubmit={onSubmit} />);

    await user.click(screen.getByRole('button', { name: 'Next clue' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith('(skipped)'));
    expect(screen.queryByText('Type a player name to submit a guess.')).not.toBeInTheDocument();
  });

  it('a skip never fires the rejected-guess shake cue, unlike a real wrong guess', async () => {
    stubNoSuggestions();
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue(false);

    render(<PathGuessInput clueCount={1} guess={null} accessToken="token" onSubmit={onSubmit} />);

    await user.click(screen.getByRole('button', { name: 'Next clue' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalled());
    expect(document.querySelector('.path-guess-input--shake')).not.toBeInTheDocument();
  });

  it('REQ207_showsSuggestionsAfterTwoCharacters: fetches and renders suggestions once the trimmed query reaches the 2-character minimum, and not before', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse([
        { playerId: 'p1', name: 'Thierry Henry', birthYear: 1977 },
        { playerId: 'p2', name: 'Theo Hernandez' },
      ]),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<PathGuessInput clueCount={1} guess={null} accessToken="token" onSubmit={vi.fn()} />);

    await user.type(screen.getByLabelText('Player name'), 'T');
    await vi.advanceTimersByTimeAsync(500);
    expect(fetchMock).not.toHaveBeenCalled();
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();

    await user.type(screen.getByLabelText('Player name'), 'h');
    await vi.advanceTimersByTimeAsync(500);

    await waitFor(() => expect(screen.getByRole('listbox')).toBeInTheDocument());
    const list = within(screen.getByRole('listbox'));
    expect(list.getByText('Thierry Henry')).toBeInTheDocument();
    expect(list.getByText('Theo Hernandez')).toBeInTheDocument();
    expect(list.getByText('1977')).toBeInTheDocument();
  });

  it('REQ207_debouncesRapidTyping: waits for a pause in typing before firing a single suggestions request, not one per keystroke', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse([{ playerId: 'p1', name: 'Thierry Henry' }]),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<PathGuessInput clueCount={1} guess={null} accessToken="token" onSubmit={vi.fn()} />);

    const field = screen.getByLabelText('Player name');
    await user.type(field, 'Th');
    await vi.advanceTimersByTimeAsync(100);
    await user.type(field, 'ie');
    await vi.advanceTimersByTimeAsync(100);
    await user.type(field, 'rry');

    // Still within the debounce window of the last keystroke — no request
    // fired yet despite 5 keystrokes having landed by now.
    expect(fetchMock).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(500);

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    expect(fetchMock.mock.calls[0][0]).toContain('query=Thierry');
  });

  it('REQ207_selectingFillsInputWithoutSubmitting: selecting a suggestion fills the field but does not submit the guess', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue(true);
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse([{ playerId: 'p1', name: 'Thierry Henry', birthYear: 1977 }]),
      ),
    );

    render(<PathGuessInput clueCount={1} guess={null} accessToken="token" onSubmit={onSubmit} />);

    await user.type(screen.getByLabelText('Player name'), 'Th');
    await vi.advanceTimersByTimeAsync(500);
    await waitFor(() => expect(screen.getByRole('option', { name: /Thierry Henry/ })).toBeInTheDocument());

    await user.click(screen.getByRole('option', { name: /Thierry Henry/ }));

    expect(screen.getByLabelText('Player name')).toHaveValue('Thierry Henry');
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();

    // The player still has to explicitly click Guess — selecting never
    // auto-submits (REQ-207: suggestion ≠ correctness).
    await user.click(screen.getByRole('button', { name: 'Guess' }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith('Thierry Henry'));
  });

  it('REQ207_keyboardNavigation: arrow keys move through suggestions, Enter picks the highlighted one, Escape dismisses the list', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse([
          { playerId: 'p1', name: 'Thierry Henry' },
          { playerId: 'p2', name: 'Theo Hernandez' },
        ]),
      ),
    );

    render(<PathGuessInput clueCount={1} guess={null} accessToken="token" onSubmit={vi.fn()} />);

    const field = screen.getByLabelText('Player name');
    await user.type(field, 'Th');
    await vi.advanceTimersByTimeAsync(500);
    await waitFor(() => expect(screen.getByRole('listbox')).toBeInTheDocument());

    // Escape dismisses the list without touching the typed text.
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    expect(field).toHaveValue('Th');

    // Re-open, then navigate with arrows and pick with Enter.
    await user.type(field, 'i');
    await vi.advanceTimersByTimeAsync(500);
    await waitFor(() => expect(screen.getByRole('listbox')).toBeInTheDocument());

    await user.keyboard('{ArrowDown}{ArrowDown}{Enter}');

    expect(field).toHaveValue('Theo Hernandez');
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });

  it('REQ207_failedFetchNeverBlocksSubmission: a failed suggestions fetch shows no suggestions but still allows submitting the guess', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue(true);
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));

    render(<PathGuessInput clueCount={1} guess={null} accessToken="token" onSubmit={onSubmit} />);

    await user.type(screen.getByLabelText('Player name'), 'Thierry Henry');
    await vi.advanceTimersByTimeAsync(500);

    // The failed background fetch never surfaces as a form error and never
    // renders a suggestion list.
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    expect(screen.queryByText(/failed to fetch/i)).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Guess' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith('Thierry Henry'));
  });
});

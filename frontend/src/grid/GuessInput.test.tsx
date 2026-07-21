import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
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
// something harmless to hit rather than a real network call.
function stubNoSuggestions() {
  vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse([])));
}

// SCREEN-02: bottom sheet on mobile / inline popover on desktop.
// REQ-207 (S-032): PlayerNameIndex-backed autocomplete suggestions — see
// the REQ207-prefixed tests below for this story's own acceptance criteria.
describe('GuessInput', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('REQ-201: shows the category header with both flag and club badge context', () => {
    stubNoSuggestions();
    render(<GuessInput cell={makeCell()} accessToken="token" onSubmit={vi.fn()} onResolveDisambiguation={vi.fn()} onClose={vi.fn()} />);

    expect(screen.getByText('France')).toBeInTheDocument();
    expect(screen.getByText('Arsenal')).toBeInTheDocument();
  });

  it('REQ-210: shows no attempt count line for an untried cell', () => {
    stubNoSuggestions();
    render(<GuessInput cell={makeCell()} accessToken="token" onSubmit={vi.fn()} onResolveDisambiguation={vi.fn()} onClose={vi.fn()} />);

    expect(screen.queryByText(/attempts used/)).not.toBeInTheDocument();
  });

  it('REQ-210: shows the attempt count once at least one attempt has been used', () => {
    stubNoSuggestions();
    const cell = makeCell({ guess: { isCorrect: false, attemptCount: 1, locked: false } });
    render(<GuessInput cell={cell} accessToken="token" onSubmit={vi.fn()} onResolveDisambiguation={vi.fn()} onClose={vi.fn()} />);

    expect(screen.getByText('1 of 2 attempts used')).toBeInTheDocument();
  });

  it('REQ-201: submits the typed name and closes on success', async () => {
    stubNoSuggestions();
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const onClose = vi.fn();
    render(<GuessInput cell={makeCell()} accessToken="token" onSubmit={onSubmit} onResolveDisambiguation={vi.fn()} onClose={onClose} />);

    await user.type(screen.getByLabelText('Player name'), 'Thierry Henry');
    await user.click(screen.getByRole('button', { name: 'Submit guess' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith('Thierry Henry'));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
  });

  it('REQ-202: shows the server error detail and stays open on failure', async () => {
    stubNoSuggestions();
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockRejectedValue(new Error('No attempts remaining'));
    const onClose = vi.fn();
    render(<GuessInput cell={makeCell()} accessToken="token" onSubmit={onSubmit} onResolveDisambiguation={vi.fn()} onClose={onClose} />);

    await user.type(screen.getByLabelText('Player name'), 'Someone');
    await user.click(screen.getByRole('button', { name: 'Submit guess' }));

    await waitFor(() => expect(screen.getByText('No attempts remaining')).toBeInTheDocument());
    expect(onClose).not.toHaveBeenCalled();
  });

  it('REQ-210: hides the input entirely once the cell is locked', () => {
    stubNoSuggestions();
    const cell = makeCell({ guess: { isCorrect: false, attemptCount: 2, locked: true } });
    render(<GuessInput cell={cell} accessToken="token" onSubmit={vi.fn()} onResolveDisambiguation={vi.fn()} onClose={vi.fn()} />);

    expect(screen.queryByLabelText('Player name')).not.toBeInTheDocument();
    expect(screen.getByText(/locked/i)).toBeInTheDocument();
  });

  it('REQ207_showsSuggestionsAfterTwoCharacters: fetches and renders suggestions once the trimmed query reaches the 2-character minimum, and not before', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse([
        { playerId: 'p1', name: 'Thierry Henry', birthYear: 1977, nationality: 'France' },
        { playerId: 'p2', name: 'Theo Hernandez' },
      ]),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<GuessInput cell={makeCell()} accessToken="token" onSubmit={vi.fn()} onResolveDisambiguation={vi.fn()} onClose={vi.fn()} />);

    await user.type(screen.getByLabelText('Player name'), 'T');
    await vi.advanceTimersByTimeAsync(500);
    expect(fetchMock).not.toHaveBeenCalled();
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();

    await user.type(screen.getByLabelText('Player name'), 'h');
    await vi.advanceTimersByTimeAsync(500);

    await waitFor(() => expect(screen.getByRole('listbox')).toBeInTheDocument());
    expect(screen.getByText('Thierry Henry')).toBeInTheDocument();
    expect(screen.getByText('Theo Hernandez')).toBeInTheDocument();
    // Disambiguation context is shown, but never anything implying validity.
    expect(screen.getByText('France · 1977')).toBeInTheDocument();
  });

  it('REQ207_debouncesRapidTyping: waits for a pause in typing before firing a single suggestions request, not one per keystroke', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse([{ playerId: 'p1', name: 'Thierry Henry' }]),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<GuessInput cell={makeCell()} accessToken="token" onSubmit={vi.fn()} onResolveDisambiguation={vi.fn()} onClose={vi.fn()} />);

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
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse([{ playerId: 'p1', name: 'Thierry Henry', nationality: 'France' }]),
      ),
    );

    render(<GuessInput cell={makeCell()} accessToken="token" onSubmit={onSubmit} onResolveDisambiguation={vi.fn()} onClose={vi.fn()} />);

    await user.type(screen.getByLabelText('Player name'), 'Th');
    await vi.advanceTimersByTimeAsync(500);
    await waitFor(() => expect(screen.getByRole('option', { name: /Thierry Henry/ })).toBeInTheDocument());

    await user.click(screen.getByRole('option', { name: /Thierry Henry/ }));

    expect(screen.getByLabelText('Player name')).toHaveValue('Thierry Henry');
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();

    // The player still has to explicitly click Submit — selecting never
    // auto-submits (REQ-207: suggestion ≠ correctness).
    await user.click(screen.getByRole('button', { name: 'Submit guess' }));
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

    render(<GuessInput cell={makeCell()} accessToken="token" onSubmit={vi.fn()} onResolveDisambiguation={vi.fn()} onClose={vi.fn()} />);

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

    // Two ArrowDowns from -1 lands on index 1 (Theo Hernandez).
    expect(field).toHaveValue('Theo Hernandez');
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });

  it('REQ207_failedFetchNeverBlocksSubmission: a failed suggestions fetch shows no suggestions but still allows submitting the guess', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const onClose = vi.fn();
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));

    render(<GuessInput cell={makeCell()} accessToken="token" onSubmit={onSubmit} onResolveDisambiguation={vi.fn()} onClose={onClose} />);

    await user.type(screen.getByLabelText('Player name'), 'Thierry Henry');
    await vi.advanceTimersByTimeAsync(500);

    // The failed background fetch never surfaces as a form error and never
    // renders a suggestion list.
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    expect(screen.queryByText(/failed to fetch/i)).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Submit guess' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith('Thierry Henry'));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
  });

  // SCREEN-02a/REQ-209: a submission that resolves to more than one fitting
  // candidate renders the disambiguation picker instead of closing.
  describe('REQ-209 disambiguation prompt', () => {
    const candidates = [
      {
        playerId: 'p1',
        name: 'Ronaldo',
        distinguishingAttributes: ['1976', 'Brazil', 'Real Madrid'],
      },
      {
        playerId: 'p2',
        name: 'Ronaldo',
        distinguishingAttributes: ['1993', 'Brazil', 'Real Madrid'],
      },
    ];

    it('REQ209_rendersPickerWithAllCandidatesAndAttributes: shows every candidate and its distinguishing attributes, and does not close', async () => {
      stubNoSuggestions();
      const user = userEvent.setup();
      const onSubmit = vi.fn().mockResolvedValue(candidates);
      const onClose = vi.fn();
      render(
        <GuessInput
          cell={makeCell()}
          accessToken="token"
          onSubmit={onSubmit}
          onResolveDisambiguation={vi.fn()}
          onClose={onClose}
        />,
      );

      await user.type(screen.getByLabelText('Player name'), 'Ronaldo');
      await user.click(screen.getByRole('button', { name: 'Submit guess' }));

      await waitFor(() => expect(screen.getByRole('radiogroup')).toBeInTheDocument());
      expect(screen.getAllByText('Ronaldo')).toHaveLength(2);
      expect(screen.getByText('1976 · Brazil · Real Madrid')).toBeInTheDocument();
      expect(screen.getByText('1993 · Brazil · Real Madrid')).toBeInTheDocument();
      // Nothing was scored yet — the sheet stays open, it never closes just
      // because candidates came back (REQ-210: showing the prompt doesn't
      // consume an attempt or resolve anything on its own).
      expect(onClose).not.toHaveBeenCalled();
    });

    it('REQ209_candidateWithNoDistinguishingAttributesRendersCleanly: a candidate with an empty distinguishingAttributes array shows no broken/empty meta line', async () => {
      stubNoSuggestions();
      const user = userEvent.setup();
      const onSubmit = vi.fn().mockResolvedValue([
        { playerId: 'p1', name: 'Ronaldo', distinguishingAttributes: [] },
        { playerId: 'p2', name: 'Ronaldo Nazário', distinguishingAttributes: ['1976'] },
      ]);
      render(
        <GuessInput
          cell={makeCell()}
          accessToken="token"
          onSubmit={onSubmit}
          onResolveDisambiguation={vi.fn()}
          onClose={vi.fn()}
        />,
      );

      await user.type(screen.getByLabelText('Player name'), 'Ronaldo');
      await user.click(screen.getByRole('button', { name: 'Submit guess' }));

      await waitFor(() => expect(screen.getByRole('radiogroup')).toBeInTheDocument());
      const options = screen.getAllByRole('radio');
      expect(options).toHaveLength(2);
      // The first candidate's row has a name but no meta/caption line at all
      // — not an empty one.
      const firstRow = options[0].closest('label');
      expect(firstRow?.querySelector('.guess-input__candidate-meta')).toBeNull();
      expect(screen.getByText('1976')).toBeInTheDocument();
    });

    it('REQ209_pickingCandidateResolvesWithChosenPlayerIdAndCloses: choosing a candidate and confirming calls onResolveDisambiguation with its playerId and closes on a scored result', async () => {
      stubNoSuggestions();
      const user = userEvent.setup();
      const onSubmit = vi.fn().mockResolvedValue(candidates);
      const onResolveDisambiguation = vi.fn().mockResolvedValue(undefined);
      const onClose = vi.fn();
      render(
        <GuessInput
          cell={makeCell()}
          accessToken="token"
          onSubmit={onSubmit}
          onResolveDisambiguation={onResolveDisambiguation}
          onClose={onClose}
        />,
      );

      await user.type(screen.getByLabelText('Player name'), 'Ronaldo');
      await user.click(screen.getByRole('button', { name: 'Submit guess' }));
      await waitFor(() => expect(screen.getByRole('radiogroup')).toBeInTheDocument());

      await user.click(screen.getByText('1993 · Brazil · Real Madrid'));
      await user.click(screen.getByRole('button', { name: 'Confirm' }));

      await waitFor(() =>
        expect(onResolveDisambiguation).toHaveBeenCalledWith('p2', 'Ronaldo'),
      );
      await waitFor(() => expect(onClose).toHaveBeenCalled());
    });

    it('REQ209_confirmDisabledUntilACandidateIsChosen: the Confirm button starts disabled and is only enabled once a candidate is picked', async () => {
      stubNoSuggestions();
      const user = userEvent.setup();
      const onSubmit = vi.fn().mockResolvedValue(candidates);
      render(
        <GuessInput
          cell={makeCell()}
          accessToken="token"
          onSubmit={onSubmit}
          onResolveDisambiguation={vi.fn()}
          onClose={vi.fn()}
        />,
      );

      await user.type(screen.getByLabelText('Player name'), 'Ronaldo');
      await user.click(screen.getByRole('button', { name: 'Submit guess' }));
      await waitFor(() => expect(screen.getByRole('radiogroup')).toBeInTheDocument());

      expect(screen.getByRole('button', { name: 'Confirm' })).toBeDisabled();

      await user.click(screen.getByRole('radio', { name: /1976/ }));
      expect(screen.getByRole('button', { name: 'Confirm' })).toBeEnabled();
    });

    it('REQ209_resolveFailureShowsErrorAndStaysOpen: a rejected resubmission shows the error inline and does not close the picker', async () => {
      stubNoSuggestions();
      const user = userEvent.setup();
      const onSubmit = vi.fn().mockResolvedValue(candidates);
      const onResolveDisambiguation = vi.fn().mockRejectedValue(new Error('Something went wrong. Try again.'));
      const onClose = vi.fn();
      render(
        <GuessInput
          cell={makeCell()}
          accessToken="token"
          onSubmit={onSubmit}
          onResolveDisambiguation={onResolveDisambiguation}
          onClose={onClose}
        />,
      );

      await user.type(screen.getByLabelText('Player name'), 'Ronaldo');
      await user.click(screen.getByRole('button', { name: 'Submit guess' }));
      await waitFor(() => expect(screen.getByRole('radiogroup')).toBeInTheDocument());

      await user.click(screen.getByRole('radio', { name: /1976/ }));
      await user.click(screen.getByRole('button', { name: 'Confirm' }));

      await waitFor(() =>
        expect(screen.getByText('Something went wrong. Try again.')).toBeInTheDocument(),
      );
      expect(onClose).not.toHaveBeenCalled();
      expect(screen.getByRole('radiogroup')).toBeInTheDocument();
    });

    it('REQ209_keyboardNavigation: arrow keys move the selection between candidates and Confirm submits the highlighted one', async () => {
      stubNoSuggestions();
      const user = userEvent.setup();
      const onSubmit = vi.fn().mockResolvedValue(candidates);
      const onResolveDisambiguation = vi.fn().mockResolvedValue(undefined);
      render(
        <GuessInput
          cell={makeCell()}
          accessToken="token"
          onSubmit={onSubmit}
          onResolveDisambiguation={onResolveDisambiguation}
          onClose={vi.fn()}
        />,
      );

      await user.type(screen.getByLabelText('Player name'), 'Ronaldo');
      await user.click(screen.getByRole('button', { name: 'Submit guess' }));
      await waitFor(() => expect(screen.getByRole('radiogroup')).toBeInTheDocument());

      // Clicking the first radio focuses it; arrow keys then move (and
      // select) within the native radio group, same as any native radio
      // button set.
      await user.click(screen.getByRole('radio', { name: /1976/ }));
      expect(screen.getByRole('radio', { name: /1976/ })).toHaveFocus();
      await user.keyboard('{ArrowDown}');
      expect(screen.getByRole('radio', { name: /1993/ })).toHaveFocus();
      expect(screen.getByRole('radio', { name: /1993/ })).toBeChecked();

      await user.click(screen.getByRole('button', { name: 'Confirm' }));
      await waitFor(() => expect(onResolveDisambiguation).toHaveBeenCalledWith('p2', 'Ronaldo'));
    });

    it('REQ209_cancelAbandonsPromptWithoutSubmitting: closing via Cancel while the picker is open never resolves a guess', async () => {
      stubNoSuggestions();
      const user = userEvent.setup();
      const onSubmit = vi.fn().mockResolvedValue(candidates);
      const onResolveDisambiguation = vi.fn();
      const onClose = vi.fn();
      render(
        <GuessInput
          cell={makeCell()}
          accessToken="token"
          onSubmit={onSubmit}
          onResolveDisambiguation={onResolveDisambiguation}
          onClose={onClose}
        />,
      );

      await user.type(screen.getByLabelText('Player name'), 'Ronaldo');
      await user.click(screen.getByRole('button', { name: 'Submit guess' }));
      await waitFor(() => expect(screen.getByRole('radiogroup')).toBeInTheDocument());

      await user.click(screen.getByRole('button', { name: 'Cancel guess' }));

      expect(onClose).toHaveBeenCalled();
      expect(onResolveDisambiguation).not.toHaveBeenCalled();
    });
  });
});

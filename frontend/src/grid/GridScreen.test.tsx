import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { GridScreen } from './GridScreen';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

describe('GridScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-303: shows a calm empty-state invitation, not an error screen, on 404', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'No active round' }, 404)),
    );

    render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() =>
      expect(screen.getByText('No round to play right now')).toBeInTheDocument(),
    );
  });

  it('REQ-303: derives row/column headers and layout from a flat cells array and renders the grid', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          roundId: 'round-1',
          startTime: '2026-07-10T00:00:00Z',
          endTime: '2026-07-11T00:00:00Z',
          allowGuessChange: false,
          cells: [
            {
              cellId: 'cell-1',
              row: 0,
              col: 0,
              rowCategoryType: 'country',
              rowCategoryValue: 'France',
              colCategoryType: 'club',
              colCategoryValue: 'Arsenal',
              guess: null,
            },
          ],
        }),
      ),
    );

    render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() => expect(screen.getByText('France')).toBeInTheDocument());
    expect(screen.getByText('Arsenal')).toBeInTheDocument();
    expect(screen.getByText('0/1 answered')).toBeInTheDocument();
  });

  it('REQ-303: logs out via onAuthError when the round fetch is unauthorized', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized' }, 401)),
    );
    const onAuthError = vi.fn();

    render(<GridScreen accessToken="stale-token" onAuthError={onAuthError} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalled());
  });

  it('REQ-201/203: opens the guess input, submits, and reflects the result in the cell', async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (String(url).endsWith('/rounds/current')) {
        return jsonResponse({
          roundId: 'round-1',
          startTime: '2026-07-10T00:00:00Z',
          endTime: '2026-07-11T00:00:00Z',
          allowGuessChange: false,
          cells: [
            {
              cellId: 'cell-1',
              row: 0,
              col: 0,
              rowCategoryType: 'country',
              rowCategoryValue: 'France',
              colCategoryType: 'club',
              colCategoryValue: 'Arsenal',
              guess: null,
            },
          ],
        });
      }
      if (String(url).includes('/guesses') && init?.method === 'POST') {
        // Frontend name-display fix: a correct guess's response now carries
        // the canonical resolvedPlayerName, shown instead of the raw as-typed
        // submittedName ("Thierry Henry" below, typed in lowercase).
        return jsonResponse({ isCorrect: true, attemptCount: 1, locked: true, resolvedPlayerName: 'Thierry Henry' });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

    const cellButton = await screen.findByRole('button', { name: 'Guess France × Arsenal' });
    await user.click(cellButton);

    await user.type(screen.getByLabelText('Player name'), 'thierry henry');
    await user.click(screen.getByRole('button', { name: 'Submit guess' }));

    await waitFor(() => expect(screen.getByText('Thierry Henry')).toBeInTheDocument());
    expect(screen.getByText('live')).toBeInTheDocument();
    expect(screen.getByText('1/1 answered')).toBeInTheDocument();
  });

  // S-020: a cell's very first guess this session mounts CellState directly
  // (GridCell renders nothing for an unattempted cell — no CellState exists
  // to transition), so a code-reviewer pass on the CellState-only unit tests
  // flagged that this exact path couldn't distinguish "just rejected" from
  // "loaded already-incorrect from a page reload." This test drives that
  // real path end-to-end (through GridScreen's actual state update, not a
  // constructed CellState prop) to pin down the fix: the rejected-guess
  // shake must fire on this very first submission, not just on a second one.
  it('S-020: a rejected first-ever guess against a fresh cell triggers the shake cue immediately, not only on a subsequent rejection', async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (String(url).endsWith('/rounds/current')) {
        return jsonResponse({
          roundId: 'round-1',
          startTime: '2026-07-10T00:00:00Z',
          endTime: '2026-07-11T00:00:00Z',
          allowGuessChange: false,
          cells: [
            {
              cellId: 'cell-1',
              row: 0,
              col: 0,
              rowCategoryType: 'country',
              rowCategoryValue: 'France',
              colCategoryType: 'club',
              colCategoryValue: 'Arsenal',
              guess: null,
            },
          ],
        });
      }
      if (String(url).includes('/guesses') && init?.method === 'POST') {
        return jsonResponse({ isCorrect: false, attemptCount: 1, locked: false });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

    const cellButton = await screen.findByRole('button', { name: 'Guess France × Arsenal' });
    await user.click(cellButton);

    await user.type(screen.getByLabelText('Player name'), 'Definitely Not A Real Player');
    await user.click(screen.getByRole('button', { name: 'Submit guess' }));

    // Frontend name-display fix: an incorrect guess shows no name at all
    // (not even the raw as-typed text) — waiting on the attempt-count text
    // instead of a name is what proves the state update actually landed.
    await waitFor(() => expect(screen.getByText('1 attempt left')).toBeInTheDocument());
    expect(screen.queryByText('Definitely Not A Real Player')).not.toBeInTheDocument();
    expect(document.querySelector('.cell-state--shake')).toBeInTheDocument();
  });
});

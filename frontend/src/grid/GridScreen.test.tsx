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
        return jsonResponse({ isCorrect: true, attemptCount: 1, locked: true });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

    const cellButton = await screen.findByRole('button', { name: 'Guess France × Arsenal' });
    await user.click(cellButton);

    await user.type(screen.getByLabelText('Player name'), 'Thierry Henry');
    await user.click(screen.getByRole('button', { name: 'Submit guess' }));

    await waitFor(() => expect(screen.getByText('Thierry Henry')).toBeInTheDocument());
    expect(screen.getByText('live')).toBeInTheDocument();
    expect(screen.getByText('1/1 answered')).toBeInTheDocument();
  });
});

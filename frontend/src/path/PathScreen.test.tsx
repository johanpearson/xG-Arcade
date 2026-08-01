import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PathScreen } from './PathScreen';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

const basePuzzle = {
  puzzleId: 'puzzle-1',
  clues: [
    {
      turnNumber: 1,
      kind: 'ClubReveal',
      clubs: [{ clubName: 'Ajax', appearanceCount: 74 }],
      yearRanges: null,
      textValue: null,
    },
  ],
  guess: null,
};

function roundResponse(puzzles: unknown[] = [basePuzzle]) {
  return {
    roundId: 'round-1',
    startTime: '2026-07-10T00:00:00Z',
    endTime: '2026-07-11T00:00:00Z',
    allowGuessChange: false,
    puzzles,
  };
}

describe('PathScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1201: shows a calm empty-state invitation, not an error screen, on 404', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'No active round' }, 404)),
    );

    render(<PathScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() => expect(screen.getByText('No puzzle to play right now')).toBeInTheDocument());
  });

  it('logs out via onAuthError when the round fetch is unauthorized', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized' }, 401)),
    );
    const onAuthError = vi.fn();

    render(<PathScreen accessToken="stale-token" onAuthError={onAuthError} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalled());
  });

  it('REQ-1202: shows "Puzzle N of M" in the header, from the round\'s own puzzle count', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url: string) => {
        if (String(url).endsWith('/path/current')) {
          return jsonResponse(roundResponse([basePuzzle, { ...basePuzzle, puzzleId: 'puzzle-2' }]));
        }
        throw new Error(`Unexpected fetch: ${url}`);
      }),
    );

    render(<PathScreen accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Puzzle 1 of 2')).toBeInTheDocument();
  });

  it('REQ-1205: the clue counter reflects this puzzle\'s own fixed cap (7), not any other fixed number', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url: string) => {
        if (String(url).endsWith('/path/current')) {
          return jsonResponse(roundResponse());
        }
        throw new Error(`Unexpected fetch: ${url}`);
      }),
    );

    render(<PathScreen accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Clue 1 of 7')).toBeInTheDocument();
  });

  it('REQ-1203/1204: a correct guess re-fetches the puzzle state, halts further reveals, and shows the solved node — no more clue turns are ever requested after that', async () => {
    const user = userEvent.setup();
    let pathFetchCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (String(url).endsWith('/path/current')) {
        pathFetchCount += 1;
        if (pathFetchCount === 1) {
          return jsonResponse(roundResponse());
        }
        // Post-guess re-fetch: solved, frozen at the same 1 revealed turn
        // (REQ-1203's "no further clue is ever revealed once solved").
        return jsonResponse(
          roundResponse([
            {
              ...basePuzzle,
              guess: {
                isCorrect: true,
                attemptCount: 1,
                locked: true,
                submittedName: 'Zlatan Ibrahimović',
                resolvedPlayerName: 'Zlatan Ibrahimović',
                resolvedPlayerPhotoUrl: null,
              },
            },
          ]),
        );
      }
      if (String(url).includes('/guesses') && init?.method === 'POST') {
        return jsonResponse({
          isCorrect: true,
          attemptCount: 1,
          locked: true,
          resolvedPlayerName: 'Zlatan Ibrahimović',
          resolvedPlayerPhotoUrl: null,
          candidates: null,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<PathScreen accessToken="token" onAuthError={vi.fn()} />);

    await user.type(await screen.findByLabelText('Player name'), 'Zlatan Ibrahimović');
    await user.click(screen.getByRole('button', { name: 'Guess' }));

    expect(await screen.findByText('Zlatan Ibrahimović')).toBeInTheDocument();
    expect(screen.getByText('Solved')).toBeInTheDocument();
    // Still exactly 1 clue turn rendered — the correct guess never revealed
    // a 2nd one.
    expect(screen.getAllByRole('listitem')).toHaveLength(1);
    expect(screen.getByLabelText('Player name')).toBeDisabled();
    // A single puzzle in the round completes it — no "Next puzzle" button,
    // just the completion message.
    expect(screen.getByText("You’ve completed every puzzle in this round.")).toBeInTheDocument();
  });

  it('"Next puzzle" is an explicit action, never automatic, and advances to the next puzzle in the round', async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      if (String(url).endsWith('/path/current')) {
        return jsonResponse(
          roundResponse([
            {
              ...basePuzzle,
              guess: {
                isCorrect: true,
                attemptCount: 1,
                locked: true,
                submittedName: 'Zlatan Ibrahimović',
                resolvedPlayerName: 'Zlatan Ibrahimović',
                resolvedPlayerPhotoUrl: null,
              },
            },
            {
              puzzleId: 'puzzle-2',
              clues: [
                {
                  turnNumber: 1,
                  kind: 'ClubReveal',
                  clubs: [{ clubName: 'Barcelona', appearanceCount: 200 }],
                  yearRanges: null,
                  textValue: null,
                },
              ],
              guess: null,
            },
          ]),
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<PathScreen accessToken="token" onAuthError={vi.fn()} />);

    await screen.findByText('Puzzle 1 of 2');
    expect(screen.queryByText('Barcelona')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Next puzzle' }));

    expect(await screen.findByText('Puzzle 2 of 2')).toBeInTheDocument();
    expect(screen.getByText('Barcelona')).toBeInTheDocument();
  });
});

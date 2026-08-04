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
    // Still exactly 1 real clue turn rendered — the correct guess never
    // revealed a 2nd one — plus a separate, trailing solved node appended
    // after it (bug fix, 2026-08-03: the solved node used to replace the
    // last clue turn instead of appending after it — see PathTimeline.tsx's
    // own comment).
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    expect(screen.getByLabelText('Player name')).toBeDisabled();
    // A single puzzle in the round completes it — no "Next puzzle" button,
    // just the completion message.
    expect(screen.getByText("You’ve completed every puzzle in this round.")).toBeInTheDocument();
  });

  it('quality-gate fix (S-086 follow-up): a re-fetch failure after a successful submit shows a distinct "couldn\'t refresh" message, never as if the guess itself failed, and still reports the guess outcome', async () => {
    const user = userEvent.setup();
    let pathFetchCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (String(url).endsWith('/path/current')) {
        pathFetchCount += 1;
        if (pathFetchCount === 1) {
          return jsonResponse(roundResponse());
        }
        // The follow-up re-fetch after the guess fails outright (network
        // blip / transient 5xx) — the guess itself already succeeded above.
        return Promise.reject(new Error('Network error'));
      }
      if (String(url).includes('/guesses') && init?.method === 'POST') {
        return jsonResponse({
          isCorrect: false,
          attemptCount: 1,
          locked: false,
          resolvedPlayerName: null,
          resolvedPlayerPhotoUrl: null,
          candidates: null,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<PathScreen accessToken="token" onAuthError={vi.fn()} />);

    await user.type(await screen.findByLabelText('Player name'), 'Wrong Guess');
    await user.click(screen.getByRole('button', { name: 'Guess' }));

    // The honest, distinct message — not PathGuessInput's own submission-
    // failure error text, and no claim that the guess itself failed.
    expect(
      await screen.findByText("Guess submitted, but couldn't refresh — try reloading this screen."),
    ).toBeInTheDocument();
    // The guess's own outcome (incorrect) was still reported back to
    // PathGuessInput, not lost just because the re-fetch failed — observable
    // as PathGuessInput's own post-rejection behavior (it only runs this on
    // a *resolved* falsy value, never when onSubmit throws): the typed name
    // is cleared and the input isn't left showing a submission-failure
    // error of its own.
    expect(screen.getByLabelText('Player name')).toHaveValue('');
    expect(screen.queryByText(/type a player name/i)).not.toBeInTheDocument();
    expect(screen.queryByText('Something went wrong. Check your connection and try again.')).not.toBeInTheDocument();
  });

  it('quality-gate fix (S-086 follow-up): a re-fetch that resolves null (round closed) after a successful submit transitions to the empty state rather than leaving stale puzzle state on screen', async () => {
    const user = userEvent.setup();
    let pathFetchCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (String(url).endsWith('/path/current')) {
        pathFetchCount += 1;
        if (pathFetchCount === 1) {
          return jsonResponse(roundResponse());
        }
        // Round closed between the submit and the re-fetch.
        return jsonResponse({ title: 'No active round' }, 404);
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

    expect(await screen.findByText('No puzzle to play right now')).toBeInTheDocument();
  });

  it('REQ-1205 judgment call: "Next puzzle" also appears once a puzzle locks unsolved (attempt cap exhausted without a correct guess), not only once solved', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url: string) => {
        if (String(url).endsWith('/path/current')) {
          return jsonResponse(
            roundResponse([
              {
                ...basePuzzle,
                guess: {
                  isCorrect: false,
                  attemptCount: 7,
                  locked: true,
                  submittedName: 'Wrong Guess',
                  resolvedPlayerName: null,
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
      }),
    );

    render(<PathScreen accessToken="token" onAuthError={vi.fn()} />);

    await screen.findByText('Puzzle 1 of 2');
    // Not solved — no "Solved" node text, and the input reflects "locked,
    // not correct" per PathGuessInput's own copy — but "Next puzzle" must
    // still be reachable, since REQ-1205's cap-exhausted case leaves the
    // player with no other way to move on.
    expect(screen.queryByText('Solved')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next puzzle' })).toBeInTheDocument();
    // User-testing fix (2026-08-02): PathScreen now passes `locked` down to
    // PathTimeline (alongside the existing `solved`), so a puzzle that's
    // locked without ever being solved gets a distinct, non-gold reveal
    // node instead of the prior silent "nothing beyond the last real clue."
    // This fixture's resolvedPlayerName is null (PathTimeline's own
    // FailedRevealNode comment explains why that's still handled correctly)
    // so only the label renders here; the name-included case is covered
    // below.
    expect(screen.getByText('Out of attempts')).toBeInTheDocument();
  });

  it('User-testing fix (2026-08-02): a locked-unsolved puzzle whose resolvedPlayerName IS available shows the answer in the failed-reveal node', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url: string) => {
        if (String(url).endsWith('/path/current')) {
          return jsonResponse(
            roundResponse([
              {
                ...basePuzzle,
                guess: {
                  isCorrect: false,
                  attemptCount: 7,
                  locked: true,
                  submittedName: 'Wrong Guess',
                  // Anticipates the parallel backend fix (PathEndpoints.cs)
                  // that populates this field whenever `locked` is true, not
                  // only when `isCorrect` — see this screen's own comment on
                  // `locked` for the sequencing note.
                  resolvedPlayerName: 'Zlatan Ibrahimović',
                  resolvedPlayerPhotoUrl: null,
                },
              },
            ]),
          );
        }
        throw new Error(`Unexpected fetch: ${url}`);
      }),
    );

    render(<PathScreen accessToken="token" onAuthError={vi.fn()} />);

    await screen.findByText('Puzzle 1 of 1');
    expect(screen.getByText('Out of attempts')).toBeInTheDocument();
    expect(screen.getByText('Zlatan Ibrahimović')).toBeInTheDocument();
    expect(screen.queryByText('Solved')).not.toBeInTheDocument();
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

  // REQ-303: mirrors GridScreen.test.tsx's own "round end-time indicator"
  // describe block exactly — same indicator, same underlying
  // lib/roundTime.ts formatter, just PathScreen's own header/class names.
  // Wording/bucket logic itself is covered exhaustively by
  // lib/roundTime.test.ts — these tests only check that PathScreen actually
  // renders and wires up the indicator.
  describe('REQ-303: round end-time indicator', () => {
    function stubCurrentPath(endTime: string) {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() => jsonResponse({ ...roundResponse(), endTime })),
      );
    }

    it('REQ-303: renders a relative-duration end-time indicator in the header once the round has loaded', async () => {
      // Comfortably in the "1-24h" bucket regardless of the moment the test
      // actually runs — the exact wording is lib/roundTime.test.ts's job,
      // not this test's.
      const endTime = new Date(Date.now() + 5 * 60 * 60 * 1000).toISOString();
      stubCurrentPath(endTime);

      render(<PathScreen accessToken="token" onAuthError={vi.fn()} />);

      await screen.findByText('Puzzle 1 of 1');

      const indicator = document.querySelector('.path-screen__end-time');
      expect(indicator).toBeInTheDocument();
      expect(indicator).toHaveTextContent(/^Ends in \d/);
    });

    it('REQ-303: exposes the absolute end date/time via the accessible name, not just the relative text', async () => {
      const endTime = '2026-08-01T09:30:00.000Z';
      stubCurrentPath(endTime);

      render(<PathScreen accessToken="token" onAuthError={vi.fn()} />);

      await screen.findByText('Puzzle 1 of 1');

      const expectedAbsoluteLabel = new Date(endTime).toLocaleString(undefined, {
        dateStyle: 'medium',
        timeStyle: 'short',
      });

      const indicator = screen.getByRole('generic', {
        name: new RegExp(`Round ends ${expectedAbsoluteLabel.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\.$`),
      });
      expect(indicator).toBeInTheDocument();
      expect(indicator).toHaveClass('path-screen__end-time');
    });

    it('REQ-303: the end-time indicator is keyboard-focusable (included in tab order)', async () => {
      const endTime = new Date(Date.now() + 5 * 60 * 60 * 1000).toISOString();
      stubCurrentPath(endTime);

      render(<PathScreen accessToken="token" onAuthError={vi.fn()} />);

      await screen.findByText('Puzzle 1 of 1');

      const indicator = document.querySelector('.path-screen__end-time');
      expect(indicator).toHaveAttribute('tabIndex', '0');
    });
  });
});

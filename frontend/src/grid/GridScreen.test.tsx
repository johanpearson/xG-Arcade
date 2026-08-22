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

  it('S-151/REQ-207: fires a fire-and-forget warm-up GET on mount, and a failing one never affects the round render', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
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
      if (String(url).includes('/players/autocomplete/warmup')) {
        return Promise.reject(new Error('cold start'));
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() => expect(screen.getByText('France')).toBeInTheDocument());
    const warmupCall = fetchMock.mock.calls.find(([url]) =>
      String(url).includes('/players/autocomplete/warmup'),
    );
    expect(warmupCall).toBeDefined();
    expect((warmupCall![1] as RequestInit).headers).toMatchObject({
      Authorization: 'Bearer token',
    });
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

  it('REQ-201/203: opens the guess input, submits, and reflects the result in the cell — at rest, only the points value is shown; the name stays hidden until revealed (REQ-212)', async () => {
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

    // S-041/REQ-212: no name is shown at rest anymore — the cell becomes the
    // reveal-toggle button (aria-expanded="false") and shows no points yet
    // either (livePoints isn't in the submit response, only a subsequent
    // GET /rounds/current would carry it — see GridScreen's own comment on
    // why the submitted guess's livePoints/uniquePercent are null here).
    const revealButton = await screen.findByRole('button', {
      name: 'Show guessed player for France × Arsenal',
    });
    expect(revealButton).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByText('Thierry Henry')).not.toBeInTheDocument();
    expect(screen.queryByText(/live/i)).not.toBeInTheDocument();
    expect(screen.getByText('1/1 answered')).toBeInTheDocument();

    // Clicking the cell reveals the guessed player's canonical name
    // (REQ-212), and toggles the aria-label/aria-expanded accordingly.
    await user.click(revealButton);
    expect(screen.getByText('Thierry Henry')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Hide guessed player for France × Arsenal' }),
    ).toHaveAttribute('aria-expanded', 'true');
  });

  // REQ-209/REQ-210: a disambiguation-needed response (candidates non-null)
  // must not be treated as a scored result — the cell stays showing as
  // unanswered/in-progress (still the plain "Guess …" open button, not the
  // reveal toggle a scored guess produces) until a real scored response
  // (here, the chosenPlayerId resubmission) arrives.
  it('REQ-209: a disambiguation response does not write anything into cell state, and the resubmission with chosenPlayerId does', async () => {
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
        const body = JSON.parse(String(init.body)) as { submittedName: string; chosenPlayerId?: string };
        if (body.chosenPlayerId) {
          // The resubmission answering the prompt is always a normal,
          // scored response.
          return jsonResponse({
            isCorrect: true,
            attemptCount: 1,
            locked: true,
            resolvedPlayerName: 'Ronaldo',
            candidates: null,
          });
        }
        // The first submission resolves to more than one fitting candidate
        // — nothing scored yet (REQ-209/REQ-210's own field values).
        return jsonResponse({
          isCorrect: false,
          attemptCount: 0,
          locked: false,
          resolvedPlayerName: null,
          candidates: [
            { playerId: 'p1', name: 'Ronaldo', distinguishingAttributes: ['1976'] },
            { playerId: 'p2', name: 'Ronaldo', distinguishingAttributes: ['1993'] },
          ],
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

    const cellButton = await screen.findByRole('button', { name: 'Guess France × Arsenal' });
    await user.click(cellButton);

    await user.type(screen.getByLabelText('Player name'), 'ronaldo');
    await user.click(screen.getByRole('button', { name: 'Submit guess' }));

    // The picker is showing — and nothing was written into cell state: the
    // grid still has 0/1 answered and the cell is still the plain open
    // button, not a reveal toggle.
    await waitFor(() => expect(screen.getByRole('radiogroup')).toBeInTheDocument());
    expect(screen.getByText('0/1 answered')).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Show guessed player for France × Arsenal' }),
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: /1993/ }));
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    // The resubmission's scored result now does update cell state normally.
    const revealButton = await screen.findByRole('button', {
      name: 'Show guessed player for France × Arsenal',
    });
    expect(revealButton).toHaveAttribute('aria-expanded', 'false');
    expect(screen.getByText('1/1 answered')).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await user.click(revealButton);
    expect(screen.getByText('Ronaldo')).toBeInTheDocument();
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

  // REQ-206: the live "~N pts estimated" running total shown in the header,
  // summed client-side from each correctly-guessed cell's already-fetched
  // livePoints (never fabricated, never the locked REQ-205 total).
  it('REQ-206: shows a live running total summed from multiple correctly-guessed cells’ livePoints', async () => {
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
              guess: {
                isCorrect: true,
                attemptCount: 1,
                locked: true,
                submittedName: 'thierry henry',
                resolvedPlayerName: 'Thierry Henry',
                uniquePercent: 40,
                livePoints: 40,
              },
            },
            {
              cellId: 'cell-2',
              row: 0,
              col: 1,
              rowCategoryType: 'country',
              rowCategoryValue: 'France',
              colCategoryType: 'club',
              colCategoryValue: 'Chelsea',
              guess: {
                isCorrect: true,
                attemptCount: 1,
                locked: true,
                submittedName: 'didier drogba',
                resolvedPlayerName: 'Didier Drogba',
                uniquePercent: 15,
                livePoints: 15,
              },
            },
            {
              cellId: 'cell-3',
              row: 1,
              col: 0,
              rowCategoryType: 'country',
              rowCategoryValue: 'Brazil',
              colCategoryType: 'club',
              colCategoryValue: 'Arsenal',
              guess: null,
            },
          ],
        }),
      ),
    );

    render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() => expect(screen.getByText('~55 pts estimated')).toBeInTheDocument());
  });

  // REQ-206: never fabricates a total before any correct guess's livePoints
  // is actually known — must not render "~0 pts estimated" or similar.
  it('REQ-206: shows no live total when no cell has a known livePoints yet', async () => {
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
            {
              cellId: 'cell-2',
              row: 0,
              col: 1,
              rowCategoryType: 'country',
              rowCategoryValue: 'France',
              colCategoryType: 'club',
              colCategoryValue: 'Chelsea',
              guess: {
                isCorrect: false,
                attemptCount: 1,
                locked: false,
                submittedName: 'wrong guess',
                resolvedPlayerName: null,
                uniquePercent: null,
                livePoints: null,
              },
            },
          ],
        }),
      ),
    );

    render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() => expect(screen.getByText('1/2 answered')).toBeInTheDocument());
    expect(screen.queryByText(/pts estimated/)).not.toBeInTheDocument();
  });

  // S-033 bugfix (REQ-206): a locked-incorrect cell is guaranteed to lock at
  // MaxPointsPerCell (ADR-0021) — the running total must include that known
  // constant, not silently omit it and understate the total (the previous
  // version only ever summed correct guesses' livePoints, so a wrong,
  // locked-out guess contributed nothing at all).
  it('REQ-206: a locked-incorrect cell contributes MaxPointsPerCell to the running total, even with no correct guesses yet', async () => {
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
              guess: {
                isCorrect: false,
                attemptCount: 2,
                locked: true,
                submittedName: 'wrong guess',
                resolvedPlayerName: null,
                uniquePercent: null,
                livePoints: null,
              },
            },
            {
              cellId: 'cell-2',
              row: 0,
              col: 1,
              rowCategoryType: 'country',
              rowCategoryValue: 'France',
              colCategoryType: 'club',
              colCategoryValue: 'Chelsea',
              guess: null,
            },
          ],
        }),
      ),
    );

    render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() => expect(screen.getByText('~100 pts estimated')).toBeInTheDocument());
  });

  it('REQ-206: sums both a correct guess’s livePoints and a locked-incorrect guess’s MaxPointsPerCell together', async () => {
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
              guess: {
                isCorrect: true,
                attemptCount: 1,
                locked: true,
                submittedName: 'thierry henry',
                resolvedPlayerName: 'Thierry Henry',
                uniquePercent: 40,
                livePoints: 40,
              },
            },
            {
              cellId: 'cell-2',
              row: 0,
              col: 1,
              rowCategoryType: 'country',
              rowCategoryValue: 'France',
              colCategoryType: 'club',
              colCategoryValue: 'Chelsea',
              guess: {
                isCorrect: false,
                attemptCount: 2,
                locked: true,
                submittedName: 'wrong guess',
                resolvedPlayerName: null,
                uniquePercent: null,
                livePoints: null,
              },
            },
          ],
        }),
      ),
    );

    render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() => expect(screen.getByText('~140 pts estimated')).toBeInTheDocument());
  });

  // REQ-213: the explainer's header entry point opens it, and opening it
  // must not discard other in-progress state — specifically, an already-open
  // GuessInput sheet (with typed-but-not-yet-submitted text) stays open and
  // untouched alongside the explainer.
  it('REQ-213: opening the explainer via the header (i) button does not discard an already-open, in-progress GuessInput sheet', async () => {
    const user = userEvent.setup();
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

    const cellButton = await screen.findByRole('button', { name: 'Guess France × Arsenal' });
    await user.click(cellButton);
    await user.type(screen.getByLabelText('Player name'), 'thierry henry');

    await user.click(screen.getByRole('button', { name: 'How scoring works' }));

    // The explainer is now open alongside the still-open, still-filled
    // GuessInput sheet — neither was unmounted/reset by opening the other.
    expect(screen.getByRole('dialog', { name: 'How scoring works' })).toBeInTheDocument();
    expect(screen.getByLabelText('Player name')).toHaveValue('thierry henry');
    expect(screen.getByRole('button', { name: 'Submit guess' })).toBeInTheDocument();

    // Closing the explainer alone leaves the guess input untouched too.
    await user.click(screen.getByRole('button', { name: 'Close' }));
    expect(screen.queryByRole('dialog', { name: 'How scoring works' })).not.toBeInTheDocument();
    expect(screen.getByLabelText('Player name')).toHaveValue('thierry henry');
  });

  // REQ-213 (S-068 regression): the 2026-07-21 leaderboard-ranking/fairness
  // content additions must render from the grid-screen entry point too, not
  // only when opened from the leaderboard screen — same component, same
  // content, regardless of which screen's button opened it.
  it('REQ-213: the explainer opened from the grid screen contains all nine required content points, including the three added for the leaderboard', async () => {
    const user = userEvent.setup();
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
    await screen.findByRole('button', { name: 'Guess France × Arsenal' });

    await user.click(screen.getByRole('button', { name: 'How scoring works' }));
    const dialog = screen.getByRole('dialog', { name: 'How scoring works' });

    // Original six.
    expect(dialog.textContent).toMatch(/live estimate/i);
    expect(dialog.textContent).toMatch(/round closes/i);
    expect(dialog.textContent).toMatch(/locked/i);
    expect(dialog.textContent).toMatch(/won't change again|does not change/i);
    expect(dialog.textContent).toMatch(/golf/i);
    expect(dialog.textContent).toMatch(/lower is better/i);
    expect(dialog.textContent).toMatch(/2 attempts per cell/i);
    expect(dialog.textContent).toMatch(/wrong guess/i);
    expect(dialog.textContent).toMatch(/maximum score/i);
    expect(dialog.textContent).toMatch(/not guessing at all/i);
    expect(dialog.textContent).toMatch(/male/i);
    expect(dialog.textContent).toMatch(/born in 1939 or later/i);

    // The three 2026-07-21 leaderboard-ranking/fairness additions —
    // player-visible on SCREEN-03, but must appear here too since this is
    // the same component/content, not a screen-conditioned subset.
    expect(dialog.textContent).toMatch(/median/i);
    expect(dialog.textContent).toMatch(/not a total/i);
    expect(dialog.textContent).toMatch(/at least 5 qualifying rounds/i);
    expect(dialog.textContent).toMatch(/never submitted a single guess/i);
    expect(dialog.textContent).toMatch(/current round/i);
    expect(dialog.textContent).toMatch(/every other cell/i);
  });

  // REQ-303 (2026-07-21 addition): the header's round end-time indicator,
  // next to the (ⓘ) scoring explainer entry point. Wording/bucket logic
  // itself is covered exhaustively by lib/roundTime.test.ts — these tests
  // only check that GridScreen actually renders and wires up the indicator.
  describe('REQ-303: round end-time indicator', () => {
    function stubCurrentRound(endTime: string) {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() =>
          jsonResponse({
            roundId: 'round-1',
            startTime: '2026-07-10T00:00:00Z',
            endTime,
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
    }

    it('REQ-303: renders a relative-duration end-time indicator in the header once the round has loaded', async () => {
      // Comfortably in the "1-24h" bucket regardless of the moment the test
      // actually runs — the exact wording is lib/roundTime.test.ts's job,
      // not this test's.
      const endTime = new Date(Date.now() + 5 * 60 * 60 * 1000).toISOString();
      stubCurrentRound(endTime);

      render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

      await waitFor(() => expect(screen.getByText('France')).toBeInTheDocument());

      const indicator = document.querySelector('.grid-screen__end-time');
      expect(indicator).toBeInTheDocument();
      expect(indicator).toHaveTextContent(/^Ends in \d/);
    });

    it('REQ-303: exposes the absolute end date/time via the accessible name, not just the relative text', async () => {
      const endTime = '2026-08-01T09:30:00.000Z';
      stubCurrentRound(endTime);

      render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

      await waitFor(() => expect(screen.getByText('France')).toBeInTheDocument());

      const expectedAbsoluteLabel = new Date(endTime).toLocaleString(undefined, {
        dateStyle: 'medium',
        timeStyle: 'short',
      });

      // Queried by accessible role/name (not a DOM string grep) — proves the
      // absolute time is exposed as the element's actual accessible name,
      // reachable by a screen reader, not only visible on hover.
      const indicator = screen.getByRole('generic', {
        name: new RegExp(`Round ends ${expectedAbsoluteLabel.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\.$`),
      });
      expect(indicator).toBeInTheDocument();
      expect(indicator).toHaveClass('grid-screen__end-time');
    });

    it('REQ-303: the end-time indicator is keyboard-focusable (included in tab order)', async () => {
      const endTime = new Date(Date.now() + 5 * 60 * 60 * 1000).toISOString();
      stubCurrentRound(endTime);

      render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);

      await waitFor(() => expect(screen.getByText('France')).toBeInTheDocument());

      const indicator = document.querySelector('.grid-screen__end-time');
      expect(indicator).toHaveAttribute('tabIndex', '0');
    });
  });

  // REQ-1210/ADR-0082: the round-completion banner — see
  // lib/roundCompletion.test.ts for the shared trigger/points-sum logic
  // itself; these tests only cover GridScreen's own wiring (mapping cells
  // into the shared shape, the "~N pts estimated" wording, and the
  // live-vs-past leaderboard-target resolution).
  describe('REQ-1210: round-completion banner', () => {
    function singleCellRound(guess: unknown) {
      return {
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
            guess,
          },
        ],
      };
    }

    it('shows the banner after an in-session guess locks the last cell, with the "~N pts estimated" wording', async () => {
      const user = userEvent.setup();
      const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
        if (String(url).endsWith('/rounds/current')) {
          return jsonResponse(singleCellRound(null));
        }
        if (String(url).includes('/guesses') && init?.method === 'POST') {
          return jsonResponse({
            isCorrect: true,
            attemptCount: 1,
            locked: true,
            resolvedPlayerName: 'Thierry Henry',
          });
        }
        throw new Error(`Unexpected fetch: ${url}`);
      });
      vi.stubGlobal('fetch', fetchMock);

      const onViewRoundLeaderboard = vi.fn();
      render(
        <GridScreen accessToken="token" onAuthError={vi.fn()} onViewRoundLeaderboard={onViewRoundLeaderboard} />,
      );

      const cellButton = await screen.findByRole('button', { name: 'Guess France × Arsenal' });
      await user.click(cellButton);
      await user.type(screen.getByLabelText('Player name'), 'thierry henry');
      await user.click(screen.getByRole('button', { name: 'Submit guess' }));

      expect(await screen.findByText('Round complete')).toBeInTheDocument();
      // livePoints isn't in the submit response (see GridScreen's own
      // comment on applyScoredGuess) — the authoritative total at this
      // exact instant is genuinely 0-known, same value the header's own
      // running-total line would show.
      expect(screen.getByText('~0 pts estimated')).toBeInTheDocument();
    });

    it('does not show the banner on an initial load of an already-complete round (REQ-1210 §7 — no replay)', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() =>
          jsonResponse(
            singleCellRound({
              isCorrect: true,
              attemptCount: 1,
              locked: true,
              submittedName: 'Thierry Henry',
              resolvedPlayerName: 'Thierry Henry',
              uniquePercent: 100,
              livePoints: 12,
            }),
          ),
        ),
      );

      render(<GridScreen accessToken="token" onAuthError={vi.fn()} onViewRoundLeaderboard={vi.fn()} />);

      await waitFor(() => expect(screen.getByText('1/1 answered')).toBeInTheDocument());
      expect(screen.queryByText('Round complete')).not.toBeInTheDocument();
    });

    it('activating "View leaderboard" re-checks GET /rounds/current and reports the \'live\' scope when this round is still the active one', async () => {
      const user = userEvent.setup();
      // First `/rounds/current` call (initial load) must return the cell
      // unguessed, same as the "shows the banner..." test above — otherwise
      // the round is already complete on mount and there's no "Guess
      // France × Arsenal" button to click. The later call (triggered by
      // "View leaderboard" itself, via handleViewCompletedRoundLeaderboard's
      // re-check) is what needs to report this round still active — its
      // guess payload doesn't matter for that check, only that `roundId`
      // still matches.
      let currentRoundCallCount = 0;
      const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
        if (String(url).endsWith('/rounds/current')) {
          currentRoundCallCount += 1;
          if (currentRoundCallCount === 1) {
            return jsonResponse(singleCellRound(null));
          }
          return jsonResponse(
            singleCellRound({
              isCorrect: true,
              attemptCount: 1,
              locked: true,
              submittedName: 'Thierry Henry',
              resolvedPlayerName: 'Thierry Henry',
              uniquePercent: 100,
              livePoints: 12,
            }),
          );
        }
        if (String(url).includes('/guesses') && init?.method === 'POST') {
          return jsonResponse({ isCorrect: true, attemptCount: 1, locked: true, resolvedPlayerName: 'Thierry Henry' });
        }
        throw new Error(`Unexpected fetch: ${url}`);
      });
      vi.stubGlobal('fetch', fetchMock);

      const onViewRoundLeaderboard = vi.fn();
      render(
        <GridScreen accessToken="token" onAuthError={vi.fn()} onViewRoundLeaderboard={onViewRoundLeaderboard} />,
      );

      const cellButton = await screen.findByRole('button', { name: 'Guess France × Arsenal' });
      await user.click(cellButton);
      await user.type(screen.getByLabelText('Player name'), 'thierry henry');
      await user.click(screen.getByRole('button', { name: 'Submit guess' }));

      await user.click(await screen.findByRole('button', { name: 'View leaderboard' }));

      await waitFor(() =>
        expect(onViewRoundLeaderboard).toHaveBeenCalledWith({
          gameKey: 'xg-grid',
          scope: 'live',
          roundId: 'round-1',
        }),
      );
    });

    it('reports the \'past\' scope once GET /rounds/current no longer returns this round (it has closed)', async () => {
      const user = userEvent.setup();
      let currentRoundCallCount = 0;
      const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
        if (String(url).endsWith('/rounds/current')) {
          currentRoundCallCount += 1;
          if (currentRoundCallCount === 1) {
            return jsonResponse(singleCellRound(null));
          }
          // REQ-806-style force-close: the round no longer comes back as
          // the active one on this later re-check.
          return jsonResponse({ title: 'No active round' }, 404);
        }
        if (String(url).includes('/guesses') && init?.method === 'POST') {
          return jsonResponse({ isCorrect: true, attemptCount: 1, locked: true, resolvedPlayerName: 'Thierry Henry' });
        }
        throw new Error(`Unexpected fetch: ${url}`);
      });
      vi.stubGlobal('fetch', fetchMock);

      const onViewRoundLeaderboard = vi.fn();
      render(
        <GridScreen accessToken="token" onAuthError={vi.fn()} onViewRoundLeaderboard={onViewRoundLeaderboard} />,
      );

      const cellButton = await screen.findByRole('button', { name: 'Guess France × Arsenal' });
      await user.click(cellButton);
      await user.type(screen.getByLabelText('Player name'), 'thierry henry');
      await user.click(screen.getByRole('button', { name: 'Submit guess' }));

      await user.click(await screen.findByRole('button', { name: 'View leaderboard' }));

      await waitFor(() =>
        expect(onViewRoundLeaderboard).toHaveBeenCalledWith({
          gameKey: 'xg-grid',
          scope: 'past',
          roundId: 'round-1',
        }),
      );
    });

    it('dismissing the banner hides it without affecting the grid underneath', async () => {
      const user = userEvent.setup();
      const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
        if (String(url).endsWith('/rounds/current')) {
          return jsonResponse(singleCellRound(null));
        }
        if (String(url).includes('/guesses') && init?.method === 'POST') {
          return jsonResponse({ isCorrect: true, attemptCount: 1, locked: true, resolvedPlayerName: 'Thierry Henry' });
        }
        throw new Error(`Unexpected fetch: ${url}`);
      });
      vi.stubGlobal('fetch', fetchMock);

      render(<GridScreen accessToken="token" onAuthError={vi.fn()} onViewRoundLeaderboard={vi.fn()} />);

      const cellButton = await screen.findByRole('button', { name: 'Guess France × Arsenal' });
      await user.click(cellButton);
      await user.type(screen.getByLabelText('Player name'), 'thierry henry');
      await user.click(screen.getByRole('button', { name: 'Submit guess' }));

      await screen.findByText('Round complete');
      await user.click(screen.getByRole('button', { name: 'Dismiss' }));

      expect(screen.queryByText('Round complete')).not.toBeInTheDocument();
      expect(screen.getByText('1/1 answered')).toBeInTheDocument();
    });
  });
});

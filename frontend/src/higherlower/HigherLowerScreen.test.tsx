import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { HigherLowerScreen } from './HigherLowerScreen';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function roundResponse(overrides: Record<string, unknown> = {}) {
  return {
    roundId: 'round-1',
    sequenceNumber: 5,
    startTime: '2026-09-01T00:00:00Z',
    endTime: '2026-09-02T00:00:00Z',
    // A real StatCategory value (ADR-0111/ADR-0112: HigherLowerGenerationService's
    // CandidateStatCategories are exactly "trophy"/"international-caps"/
    // "international-goals" as of S-231, never human-readable copy) — using
    // a real value here is what caught the gap-fill bug below in the first
    // place; a fabricated human-readable placeholder string would have
    // hidden it.
    statCategory: 'international-caps',
    comparatorCount: 5,
    streakLength: 0,
    hasEnded: false,
    baseline: { playerId: 'p-baseline', name: 'Pelé', value: 100 },
    nextComparator: { playerId: 'p-next', name: 'Zico' },
    ...overrides,
  };
}

describe('HigherLowerScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1504: shows a loading status before the round resolves', () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => new Promise(() => {})));

    render(<HigherLowerScreen accessToken="token" onAuthError={vi.fn()} />);

    expect(screen.getByText('Loading this round…')).toBeInTheDocument();
  });

  it('REQ-1504: shows a calm empty-state invitation, not an error screen, on 404', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'No active round' }, 404)),
    );

    render(<HigherLowerScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() => expect(screen.getByText('No round to play right now')).toBeInTheDocument());
  });

  it('shows the server-reported error message on a genuine fetch failure', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Something broke' }, 500)),
    );

    render(<HigherLowerScreen accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Something broke')).toBeInTheDocument();
  });

  it('logs out via onAuthError when the round fetch is unauthorized', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized' }, 401)),
    );
    const onAuthError = vi.fn();

    render(<HigherLowerScreen accessToken="stale-token" onAuthError={onAuthError} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalled());
  });

  it('REQ-1504: renders the revealed baseline, the identity-only next comparator, "Streak N of M," and both guess buttons', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url: string) => {
        if (String(url).endsWith('/higher-lower/current')) return jsonResponse(roundResponse());
        throw new Error(`Unexpected fetch: ${url}`);
      }),
    );

    render(<HigherLowerScreen accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Streak 0 of 5')).toBeInTheDocument();
    // Gap-fill (2026-09-10, user-tester report): the raw category string
    // and a bare "100" said nothing to a real player — both are now
    // humanized (ADR-0111/ADR-0112's real category strings map to full
    // English + a unit; see higherLower.ts).
    expect(screen.getByText('Comparing number of international caps')).toBeInTheDocument();
    expect(screen.getByText('Pelé')).toBeInTheDocument();
    expect(screen.getByText('100 caps')).toBeInTheDocument();
    expect(screen.getByText('Zico')).toBeInTheDocument();
    // The next comparator's value is never rendered anywhere on the page —
    // REQ-1504's "hidden until guessed" contract, enforced by the DTO
    // itself carrying no value field to render.
    expect(screen.queryByText('120')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Higher' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Lower' })).toBeInTheDocument();
  });

  it('REQ-1504: a correct, non-terminal guess re-fetches the round, shows the "Correct." outcome, and advances the baseline/streak/next comparator', async () => {
    const user = userEvent.setup();
    let getCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (String(url).endsWith('/higher-lower/current')) {
        getCount += 1;
        if (getCount === 1) return jsonResponse(roundResponse());
        return jsonResponse(
          roundResponse({
            streakLength: 1,
            baseline: { playerId: 'p-next', name: 'Zico', value: 120 },
            nextComparator: { playerId: 'p-next-2', name: 'Ronaldo' },
          }),
        );
      }
      if (String(url).includes('/higher-lower/guesses') && init?.method === 'POST') {
        expect(JSON.parse(init.body as string)).toEqual({ direction: 0 });
        return jsonResponse({
          isCorrect: true,
          revealedPlayerId: 'p-next',
          revealedPlayerName: 'Zico',
          revealedValue: 120,
          streakLength: 1,
          hasEnded: false,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<HigherLowerScreen accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByText('Streak 0 of 5');

    await user.click(screen.getByRole('button', { name: 'Higher' }));

    expect(await screen.findByText(/Correct\./)).toBeInTheDocument();
    expect(await screen.findByText('Streak 1 of 5')).toBeInTheDocument();
    expect(screen.getByText('Ronaldo')).toBeInTheDocument();
    // The new baseline shows the just-revealed value.
    const baselineCard = document.querySelector('.higher-lower-screen__card--baseline');
    expect(baselineCard).toHaveTextContent('Zico');
    expect(baselineCard).toHaveTextContent('120 caps');
  });

  it('REQ-1504: an incorrect guess ends the attempt, shows the "Incorrect." outcome (sourced from the POST response, since GET does not advance the baseline on an incorrect guess), and removes the guess buttons', async () => {
    const user = userEvent.setup();
    let getCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (String(url).endsWith('/higher-lower/current')) {
        getCount += 1;
        if (getCount === 1) return jsonResponse(roundResponse());
        // The backend never advances CurrentBaselinePlayerId/Value on an
        // incorrect guess — baseline stays exactly as it was pre-guess.
        return jsonResponse(roundResponse({ hasEnded: true, nextComparator: null }));
      }
      if (String(url).includes('/higher-lower/guesses') && init?.method === 'POST') {
        return jsonResponse({
          isCorrect: false,
          revealedPlayerId: 'p-next',
          revealedPlayerName: 'Zico',
          revealedValue: 80,
          streakLength: 0,
          hasEnded: true,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<HigherLowerScreen accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByText('Streak 0 of 5');

    await user.click(screen.getByRole('button', { name: 'Lower' }));

    expect(await screen.findByText(/Incorrect\./)).toBeInTheDocument();
    expect(screen.getByText(/Zico/)).toBeInTheDocument();
    expect(screen.getByText('80 caps')).toBeInTheDocument();
    expect(await screen.findByText('You’ve completed this round.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Higher' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Lower' })).not.toBeInTheDocument();
  });

  it('REQ-1504: a correct guess that completes the Round\'s full fixed sequence ends the attempt at the maximum length, the same terminal outcome as an incorrect guess', async () => {
    const user = userEvent.setup();
    let getCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (String(url).endsWith('/higher-lower/current')) {
        getCount += 1;
        if (getCount === 1) return jsonResponse(roundResponse({ comparatorCount: 1 }));
        return jsonResponse(
          roundResponse({
            comparatorCount: 1,
            streakLength: 1,
            hasEnded: true,
            baseline: { playerId: 'p-next', name: 'Zico', value: 120 },
            nextComparator: null,
          }),
        );
      }
      if (String(url).includes('/higher-lower/guesses') && init?.method === 'POST') {
        return jsonResponse({
          isCorrect: true,
          revealedPlayerId: 'p-next',
          revealedPlayerName: 'Zico',
          revealedValue: 120,
          streakLength: 1,
          hasEnded: true,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<HigherLowerScreen accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByText('Streak 0 of 1');

    await user.click(screen.getByRole('button', { name: 'Higher' }));

    expect(await screen.findByText('You’ve completed this round.')).toBeInTheDocument();
    expect(screen.getByText('Streak 1 of 1')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Higher' })).not.toBeInTheDocument();
  });

  it('REQ-1504: a 409 against an already-ended attempt (e.g. a race from a second tab) shows a plain-text message and resyncs from the server', async () => {
    const user = userEvent.setup();
    let getCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (String(url).endsWith('/higher-lower/current')) {
        getCount += 1;
        if (getCount === 1) return jsonResponse(roundResponse());
        return jsonResponse(roundResponse({ hasEnded: true, nextComparator: null }));
      }
      if (String(url).includes('/higher-lower/guesses') && init?.method === 'POST') {
        return jsonResponse(
          { title: 'Attempt has ended', detail: 'no further guesses are accepted.' },
          409,
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<HigherLowerScreen accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByText('Streak 0 of 5');

    await user.click(screen.getByRole('button', { name: 'Higher' }));

    expect(
      await screen.findByText('This attempt has already ended — refreshing the latest state.'),
    ).toBeInTheDocument();
    expect(await screen.findByText('You’ve completed this round.')).toBeInTheDocument();
  });

  describe('REQ-1210: round-completion banner', () => {
    it('shows the banner once the attempt ends, with plain "N pts" (never "estimated"), and reports this game\'s own key', async () => {
      const user = userEvent.setup();
      let getCount = 0;
      const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
        if (String(url).endsWith('/higher-lower/current')) {
          getCount += 1;
          if (getCount === 1) return jsonResponse(roundResponse({ comparatorCount: 1 }));
          return jsonResponse(
            roundResponse({
              comparatorCount: 1,
              streakLength: 1,
              hasEnded: true,
              baseline: { playerId: 'p-next', name: 'Zico', value: 120 },
              nextComparator: null,
            }),
          );
        }
        if (String(url).includes('/higher-lower/guesses') && init?.method === 'POST') {
          return jsonResponse({
            isCorrect: true,
            revealedPlayerId: 'p-next',
            revealedPlayerName: 'Zico',
            revealedValue: 120,
            streakLength: 1,
            hasEnded: true,
          });
        }
        throw new Error(`Unexpected fetch: ${url}`);
      });
      vi.stubGlobal('fetch', fetchMock);

      const onViewRoundLeaderboard = vi.fn();
      render(
        <HigherLowerScreen accessToken="token" onAuthError={vi.fn()} onViewRoundLeaderboard={onViewRoundLeaderboard} />,
      );
      await screen.findByText('Streak 0 of 1');

      await user.click(screen.getByRole('button', { name: 'Higher' }));

      expect(await screen.findByText('Round complete')).toBeInTheDocument();
      // RoundCompletionBanner.tsx renders `role="status"` — scope the
      // points-text query to it (this screen's own outcome text also
      // mentions "Zico," so an unscoped query risks ambiguity).
      const banner = screen.getByRole('status');
      expect(within(banner).getByText('1 pts')).toBeInTheDocument();
      expect(within(banner).queryByText(/estimated/)).not.toBeInTheDocument();

      await user.click(within(banner).getByRole('button', { name: 'View leaderboard' }));

      await waitFor(() =>
        expect(onViewRoundLeaderboard).toHaveBeenCalledWith({
          gameKey: 'xg-higher-lower',
          scope: 'live',
          roundId: 'round-1',
        }),
      );
    });

    it('does not show the banner on an initial load of an already-ended attempt (REQ-1210 §7 — no replay)', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation((url: string) => {
          if (String(url).endsWith('/higher-lower/current')) {
            return jsonResponse(roundResponse({ hasEnded: true, nextComparator: null }));
          }
          throw new Error(`Unexpected fetch: ${url}`);
        }),
      );

      render(<HigherLowerScreen accessToken="token" onAuthError={vi.fn()} onViewRoundLeaderboard={vi.fn()} />);

      await screen.findByText('You’ve completed this round.');
      expect(screen.queryByText('Round complete')).not.toBeInTheDocument();
    });
  });
});

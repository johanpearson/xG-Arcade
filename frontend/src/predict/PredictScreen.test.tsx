import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PredictScreen } from './PredictScreen';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

// REQ-1301: a round is always exactly 5 matches — every fixture below
// mirrors that shape rather than a shortened stand-in, so "all 5 render"/
// "all 5 filled enables confirm" assertions exercise the real contract.
function baseMatches(overrides: Record<string, Partial<Record<string, unknown>>> = {}) {
  const defaults = [
    { matchId: 'm1', homeTeamName: 'Arsenal', awayTeamName: 'Chelsea', kickoffUtc: '2026-09-13T14:00:00Z', homeGoals: null, awayGoals: null },
    { matchId: 'm2', homeTeamName: 'Liverpool', awayTeamName: 'Everton', kickoffUtc: '2026-09-13T14:00:00Z', homeGoals: null, awayGoals: null },
    { matchId: 'm3', homeTeamName: 'Man City', awayTeamName: 'Man United', kickoffUtc: '2026-09-13T14:00:00Z', homeGoals: null, awayGoals: null },
    { matchId: 'm4', homeTeamName: 'Spurs', awayTeamName: 'West Ham', kickoffUtc: '2026-09-13T14:00:00Z', homeGoals: null, awayGoals: null },
    { matchId: 'm5', homeTeamName: 'Newcastle', awayTeamName: 'Brighton', kickoffUtc: '2026-09-13T14:00:00Z', homeGoals: null, awayGoals: null },
  ];
  return defaults.map((match) => ({ ...match, ...(overrides[match.matchId] ?? {}) }));
}

function roundResponse(overrides: {
  matches?: ReturnType<typeof baseMatches>;
  locked?: boolean;
  confirmedLocked?: boolean;
} = {}) {
  return {
    roundId: 'round-1',
    sequenceNumber: 1,
    startTime: '2026-09-10T00:00:00Z',
    endTime: '2026-09-20T00:00:00Z',
    locked: overrides.locked ?? false,
    confirmedLocked: overrides.confirmedLocked ?? false,
    matches: overrides.matches ?? baseMatches(),
  };
}

describe('PredictScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1301: shows a loading state while the round fetch is in flight', async () => {
    let resolveFetch: (value: unknown) => void = () => {};
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(
        () =>
          new Promise((resolve) => {
            resolveFetch = resolve;
          }),
      ),
    );

    render(<PredictScreen accessToken="token" onAuthError={vi.fn()} />);

    expect(screen.getByText('Loading this round…')).toBeInTheDocument();
    resolveFetch(await jsonResponse(roundResponse()));
  });

  it('REQ-1301: shows a calm empty-state invitation, not an error screen, on 404', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'No active round' }, 404)),
    );

    render(<PredictScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() => expect(screen.getByText('No round to predict right now')).toBeInTheDocument());
  });

  it('shows the server-provided error message on a genuine fetch failure', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Server error', detail: 'Something broke' }, 500)),
    );

    render(<PredictScreen accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Something broke')).toBeInTheDocument();
  });

  it('logs out via onAuthError when the round fetch is unauthorized', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized' }, 401)));
    const onAuthError = vi.fn();

    render(<PredictScreen accessToken="stale-token" onAuthError={onAuthError} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalled());
  });

  it('REQ-1301: renders all 5 matches with team names and kickoff times', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url: string) => {
        if (String(url).endsWith('/predict/current')) return jsonResponse(roundResponse());
        throw new Error(`Unexpected fetch: ${url}`);
      }),
    );

    render(<PredictScreen accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByLabelText('Arsenal predicted goals')).toBeInTheDocument();
    for (const name of ['Arsenal', 'Chelsea', 'Liverpool', 'Everton', 'Man City', 'Man United', 'Spurs', 'West Ham', 'Newcastle', 'Brighton']) {
      // Each team name appears twice (the match header, and its own goal
      // field's label) — getAllByText rather than getByText.
      expect(screen.getAllByText(new RegExp(`^${name}$`)).length).toBeGreaterThan(0);
    }
    // Every match shares the same fixture kickoff in this test's data — 5
    // kickoff labels should be present, one per match.
    expect(screen.getAllByLabelText(/Kicks off/)).toHaveLength(5);
  });

  it('REQ-1302: saving a prediction calls POST /predict/matches/{matchId}/predictions and reflects the new value', async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (String(url).endsWith('/predict/current')) return jsonResponse(roundResponse());
      if (String(url).endsWith('/predict/matches/m1/predictions') && init?.method === 'POST') {
        expect(JSON.parse(String(init.body))).toEqual({ homeGoals: 2, awayGoals: 1 });
        return jsonResponse({ matchId: 'm1', homeGoals: 2, awayGoals: 1 });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<PredictScreen accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByLabelText('Arsenal predicted goals');

    await user.type(screen.getByLabelText('Arsenal predicted goals'), '2');
    await user.type(screen.getByLabelText('Chelsea predicted goals'), '1');
    await user.click(screen.getAllByRole('button', { name: 'Save' })[0]);

    expect(await screen.findAllByText('Saved.')).toHaveLength(1);
    expect(screen.getByLabelText('Arsenal predicted goals')).toHaveValue(2);
    expect(screen.getByLabelText('Chelsea predicted goals')).toHaveValue(1);
  });

  it('REQ-1302: an invalid client-side value (negative) is rejected before any request is sent', async () => {
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      if (String(url).endsWith('/predict/current')) return jsonResponse(roundResponse());
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<PredictScreen accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByLabelText('Arsenal predicted goals');

    await user.type(screen.getByLabelText('Arsenal predicted goals'), '-1');
    await user.click(screen.getAllByRole('button', { name: 'Save' })[0]);

    expect(await screen.findByText('Enter a whole number, 0 or higher, for both scores.')).toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/predictions'))).toBe(false);
  });

  it('REQ-1303: a 409 "Round is locked" response mid-edit re-fetches, shows the round-locked notice, and disables every match', async () => {
    const user = userEvent.setup();
    let predictFetchCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (String(url).endsWith('/predict/current')) {
        predictFetchCount += 1;
        if (predictFetchCount === 1) return jsonResponse(roundResponse());
        return jsonResponse(roundResponse({ locked: true }));
      }
      if (String(url).endsWith('/predict/matches/m1/predictions') && init?.method === 'POST') {
        return jsonResponse({ title: 'Round is locked' }, 409);
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<PredictScreen accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByLabelText('Arsenal predicted goals');

    await user.type(screen.getByLabelText('Arsenal predicted goals'), '2');
    await user.type(screen.getByLabelText('Chelsea predicted goals'), '1');
    await user.click(screen.getAllByRole('button', { name: 'Save' })[0]);

    expect(await screen.findByText('Round is locked')).toBeInTheDocument();
    expect(
      await screen.findByText(
        'This round has locked — the first match has kicked off. Predictions can no longer be changed.',
      ),
    ).toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText('Arsenal predicted goals')).toBeDisabled());
    expect(screen.getByLabelText('Newcastle predicted goals')).toBeDisabled();
    for (const button of screen.getAllByRole('button', { name: 'Save' })) {
      expect(button).toBeDisabled();
    }
  });

  it('REQ-1306: the confirm action is hidden until all 5 matches have a stored prediction', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url: string) => {
        if (String(url).endsWith('/predict/current')) {
          return jsonResponse(
            roundResponse({
              matches: baseMatches({ m1: { homeGoals: 1, awayGoals: 0 } }),
            }),
          );
        }
        throw new Error(`Unexpected fetch: ${url}`);
      }),
    );

    render(<PredictScreen accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByLabelText('Arsenal predicted goals');

    expect(screen.queryByRole('button', { name: 'Confirm and lock my predictions' })).not.toBeInTheDocument();
  });

  it('REQ-1306: once all 5 are filled, clicking confirm opens the dialog; cancelling leaves everything editable', async () => {
    const user = userEvent.setup();
    const filledMatches = baseMatches({
      m1: { homeGoals: 1, awayGoals: 0 },
      m2: { homeGoals: 2, awayGoals: 2 },
      m3: { homeGoals: 0, awayGoals: 0 },
      m4: { homeGoals: 3, awayGoals: 1 },
      m5: { homeGoals: 1, awayGoals: 1 },
    });
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url: string) => {
        if (String(url).endsWith('/predict/current')) return jsonResponse(roundResponse({ matches: filledMatches }));
        throw new Error(`Unexpected fetch: ${url}`);
      }),
    );

    render(<PredictScreen accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByLabelText('Arsenal predicted goals');

    const confirmButton = await screen.findByRole('button', { name: 'Confirm and lock my predictions' });
    await user.click(confirmButton);

    const dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveTextContent("Are you sure? You can't change your predictions after confirming.");

    await user.click(screen.getByTestId('predict-confirm-dialog-cancel'));

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Arsenal predicted goals')).not.toBeDisabled();
    expect(screen.queryByText(/confirmed and locked/i)).not.toBeInTheDocument();
  });

  it('REQ-1306: confirming calls POST /predict/confirm and the screen reflects the fully-locked treatment', async () => {
    const user = userEvent.setup();
    const filledMatches = baseMatches({
      m1: { homeGoals: 1, awayGoals: 0 },
      m2: { homeGoals: 2, awayGoals: 2 },
      m3: { homeGoals: 0, awayGoals: 0 },
      m4: { homeGoals: 3, awayGoals: 1 },
      m5: { homeGoals: 1, awayGoals: 1 },
    });
    let predictFetchCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      if (String(url).endsWith('/predict/current')) {
        predictFetchCount += 1;
        if (predictFetchCount === 1) return jsonResponse(roundResponse({ matches: filledMatches }));
        return jsonResponse(roundResponse({ matches: filledMatches, confirmedLocked: true }));
      }
      if (String(url).endsWith('/predict/confirm') && init?.method === 'POST') {
        return jsonResponse({ roundId: 'round-1', lockedAt: '2026-09-12T00:00:00Z' });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<PredictScreen accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByLabelText('Arsenal predicted goals');

    await user.click(await screen.findByRole('button', { name: 'Confirm and lock my predictions' }));
    await user.click(screen.getByTestId('predict-confirm-dialog-confirm'));

    expect(
      await screen.findByText("You've confirmed and locked your predictions for this round."),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Arsenal predicted goals')).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Confirm and lock my predictions' })).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith('/predict/confirm'))).toBe(true);
  });
});

import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ChainBuilder } from './ChainBuilder';
import type { ConnectTerminalState } from '../lib/types';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

const NOT_TERMINAL: ConnectTerminalState = { busted: false, timedOut: false, completed: false };

function renderBuilder(overrides: Partial<Parameters<typeof ChainBuilder>[0]> = {}, fetchMock = vi.fn()) {
  vi.stubGlobal('fetch', fetchMock);
  const onAuthError = vi.fn();
  const onChanged = vi.fn();
  render(
    <ChainBuilder
      matchId="match-1"
      accessToken="token"
      myTargetPick={{ targetPlayerId: 't1', targetPlayerName: 'Lionel Messi', locked: true }}
      opponentTargetPick={{ targetPlayerId: 't2', targetPlayerName: 'Cristiano Ronaldo', locked: true }}
      myChainSteps={[]}
      myTerminalState={NOT_TERMINAL}
      opponentTerminalState={NOT_TERMINAL}
      deadlineUtc="2026-09-03T12:00:00Z"
      onAuthError={onAuthError}
      onChanged={onChanged}
      {...overrides}
    />,
  );
  return { onAuthError, onChanged };
}

async function fillAndSubmit(user: ReturnType<typeof userEvent.setup>, candidate: string, club: string) {
  await user.type(screen.getByLabelText('Candidate player name'), candidate);
  await user.type(screen.getByLabelText('Claimed shared club'), club);
  await user.click(screen.getByRole('button', { name: 'Submit connector' }));
}

// REQ-1406/1407 (design-document.md SCREEN-16's "Active/chain-building
// phase").
describe('ChainBuilder', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1406: shows both target players and the opponent\'s "still playing" status', () => {
    renderBuilder();

    expect(screen.getAllByText('Lionel Messi').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Cristiano Ronaldo').length).toBeGreaterThan(0);
    expect(screen.getByText('Your opponent is still playing.')).toBeInTheDocument();
  });

  it('REQ-1406: an accepted, non-closing step shows confirmation and notifies the parent to refetch', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/players/autocomplete')) return jsonResponse([]);
      if (url.endsWith('/matches/match-1/chain-steps')) {
        return jsonResponse({
          isValid: true,
          chainComplete: false,
          position: 1,
          attemptNumber: 1,
          candidatePlayerId: 'p1',
          claimedClubName: 'Barcelona',
          busted: false,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onChanged } = renderBuilder({}, fetchMock);

    await fillAndSubmit(user, 'Some Player', 'Barcelona');

    expect(await screen.findByText('Connector accepted.')).toBeInTheDocument();
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect((screen.getByLabelText('Candidate player name') as HTMLInputElement).value).toBe('');
  });

  it('REQ-1406: a closing step shows the "chain complete" confirmation', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/players/autocomplete')) return jsonResponse([]);
      if (url.endsWith('/matches/match-1/chain-steps')) {
        return jsonResponse({
          isValid: true,
          chainComplete: true,
          position: 1,
          attemptNumber: 1,
          candidatePlayerId: 'p1',
          claimedClubName: 'Barcelona',
          busted: false,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    renderBuilder({}, fetchMock);

    await fillAndSubmit(user, 'Some Player', 'Barcelona');

    expect(await screen.findByText('Connected! Your chain is complete.')).toBeInTheDocument();
  });

  it('REQ-1407: a first invalid attempt allows a retry, without ending participation', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/players/autocomplete')) return jsonResponse([]);
      if (url.endsWith('/matches/match-1/chain-steps')) {
        return jsonResponse({
          isValid: false,
          chainComplete: false,
          position: 1,
          attemptNumber: 1,
          candidatePlayerId: 'p1',
          claimedClubName: 'Wrong Club',
          busted: false,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onChanged } = renderBuilder({}, fetchMock);

    await fillAndSubmit(user, 'Some Player', 'Wrong Club');

    expect(await screen.findByText(/one more attempt at this position/)).toBeInTheDocument();
    // Nothing persisted differently for the caller to see yet — this
    // component still accepts more submissions (no terminal state).
    expect(screen.getByRole('button', { name: 'Submit connector' })).toBeInTheDocument();
    expect(onChanged).not.toHaveBeenCalled();
  });

  it('REQ-1407: a second consecutive failure busts the player and hides the submission form', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/players/autocomplete')) return jsonResponse([]);
      if (url.endsWith('/matches/match-1/chain-steps')) {
        return jsonResponse({
          isValid: false,
          chainComplete: false,
          position: 1,
          attemptNumber: 2,
          candidatePlayerId: 'p1',
          claimedClubName: 'Wrong Club Again',
          busted: true,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onChanged } = renderBuilder({}, fetchMock);

    await fillAndSubmit(user, 'Some Player', 'Wrong Club Again');

    expect(await screen.findByText(/Busted — that was a second failed attempt/)).toBeInTheDocument();
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
  });

  it('REQ-1406: a name that resolves to no known player shows a distinct "no player found" message', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/players/autocomplete')) return jsonResponse([]);
      if (url.endsWith('/matches/match-1/chain-steps')) {
        return jsonResponse({
          isValid: false,
          chainComplete: false,
          position: null,
          attemptNumber: null,
          candidatePlayerId: null,
          claimedClubName: null,
          busted: false,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onChanged } = renderBuilder({}, fetchMock);

    await fillAndSubmit(user, 'Nobody Real', 'Some Club');

    expect(await screen.findByText(/No player found matching "Nobody Real"/)).toBeInTheDocument();
    expect(onChanged).not.toHaveBeenCalled();
  });

  it('REQ-1407: shows a terminal "you busted" state with no submission form when already busted', () => {
    renderBuilder({ myTerminalState: { busted: true, timedOut: false, completed: false } });

    expect(screen.getByText(/You busted/)).toBeInTheDocument();
    expect(screen.queryByLabelText('Candidate player name')).not.toBeInTheDocument();
    expect(screen.getByText(/No further steps can be submitted/)).toBeInTheDocument();
  });

  it('REQ-1408: shows a terminal "you completed your chain" state with no submission form', () => {
    renderBuilder({ myTerminalState: { busted: false, timedOut: false, completed: true } });

    expect(screen.getByText(/finished their chain/)).toBeInTheDocument();
    expect(screen.queryByLabelText('Candidate player name')).not.toBeInTheDocument();
  });

  it('REQ-1409: shows the opponent\'s terminal state as plain text, without revealing their chain', () => {
    renderBuilder({ opponentTerminalState: { busted: true, timedOut: false, completed: false } });

    expect(screen.getByText(/Your opponent busted/)).toBeInTheDocument();
  });
});

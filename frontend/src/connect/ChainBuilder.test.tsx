import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ChainBuilder } from './ChainBuilder';
import type { ChainBuilderProps } from './ChainBuilder';
import type { ConnectTerminalState } from '../lib/types';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

const NOT_TERMINAL: ConnectTerminalState = { busted: false, timedOut: false, completed: false };

function buildProps(overrides: Partial<ChainBuilderProps> = {}, onAuthError = vi.fn(), onChanged = vi.fn()): ChainBuilderProps {
  return {
    matchId: 'match-1',
    accessToken: 'token',
    myTargetPick: { targetPlayerId: 't1', targetPlayerName: 'Lionel Messi', locked: true },
    opponentTargetPick: { targetPlayerId: 't2', targetPlayerName: 'Cristiano Ronaldo', locked: true },
    myChainSteps: [],
    myTerminalState: NOT_TERMINAL,
    opponentTerminalState: NOT_TERMINAL,
    deadlineUtc: '2026-09-03T12:00:00Z',
    onAuthError,
    onChanged,
    ...overrides,
  };
}

function renderBuilder(overrides: Partial<ChainBuilderProps> = {}, fetchMock = vi.fn()) {
  vi.stubGlobal('fetch', fetchMock);
  const onAuthError = vi.fn();
  const onChanged = vi.fn();
  const props = buildProps(overrides, onAuthError, onChanged);
  const { rerender } = render(<ChainBuilder {...props} />);
  // Simulates what MatchScreen.tsx really does after onChanged() resolves —
  // pass fresh props down (a new `GET /matches/{matchId}` response) without
  // unmounting ChainBuilder, the same way React itself would for a parent
  // re-render that doesn't change this component's position in the tree.
  const rerenderWith = (nextOverrides: Partial<ChainBuilderProps>) =>
    rerender(<ChainBuilder {...buildProps({ ...overrides, ...nextOverrides }, onAuthError, onChanged)} />);
  return { onAuthError, onChanged, rerenderWith };
}

async function fillAndSubmit(user: ReturnType<typeof userEvent.setup>, candidate: string) {
  await user.type(screen.getByLabelText('Candidate player name'), candidate);
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
          matchedClubName: 'Barcelona',
          matchedOverlapStartYear: 2010,
          matchedOverlapEndYear: 2015,
          busted: false,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onChanged } = renderBuilder({}, fetchMock);

    await fillAndSubmit(user, 'Some Player');

    expect(await screen.findByText('Connector accepted — Barcelona, 2010-2015.')).toBeInTheDocument();
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect((screen.getByLabelText('Candidate player name') as HTMLInputElement).value).toBe('');
  });

  it('REQ-1406/S-218: a closing step notifies the parent to refetch, and the "chain complete" confirmation is derived from the refreshed myTerminalState rather than one-shot local state', async () => {
    // S-218 bugfix regression test. The confirmed production bug: the
    // "Connected!" acknowledgment used to be set as local state inside the
    // submit handler, which a concurrent parent re-render (in the real bug,
    // an immediate unmount when the same submission also resolved the
    // match) could wipe before it was ever observed. This test proves the
    // fix's core property: ChainBuilder itself never needs its own local
    // "I just completed my chain" flag to show the acknowledgment — it only
    // needs `myTerminalState.completed` to be true in its props, exactly as
    // a real `onChanged()`-triggered refetch would deliver moments later.
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
          matchedClubName: 'Barcelona',
          matchedOverlapStartYear: 2010,
          matchedOverlapEndYear: 2015,
          busted: false,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onChanged, rerenderWith } = renderBuilder({}, fetchMock);

    await fillAndSubmit(user, 'Some Player');
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));

    // Before the parent's refetch has delivered new props, nothing claims
    // completion yet — there is deliberately no ephemeral local flag racing
    // to show it early only to have it possibly wiped a moment later.
    expect(screen.queryByText('Connected! Your chain is complete.')).not.toBeInTheDocument();

    // Simulate MatchScreen.tsx's post-refetch re-render (still `Active` —
    // this player's own chain is complete, but their opponent hasn't
    // finished, so the match hasn't resolved and ChainBuilder stays
    // mounted): the acknowledgment now appears, sourced entirely from the
    // refreshed `myTerminalState` prop.
    rerenderWith({
      myTerminalState: { busted: false, timedOut: false, completed: true },
      myChainSteps: [
        {
          position: 1,
          attemptNumber: 1,
          candidatePlayerId: 'p1',
          candidatePlayerName: 'Some Player',
          matchedClubName: 'Barcelona',
          matchedOverlapStartYear: 2010,
          matchedOverlapEndYear: 2015,
          isValid: true,
          closesChain: true,
          submittedAt: '2026-09-04T00:00:00Z',
        },
      ],
    });

    expect(screen.getByText('Connected! Your chain is complete.')).toBeInTheDocument();
    // And the terminal-state text (a separate, pre-existing signal) shows
    // alongside it, not instead of it — both are true simultaneously.
    expect(screen.getByText(/finished their chain/)).toBeInTheDocument();
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
          matchedClubName: null,
          matchedOverlapStartYear: null,
          matchedOverlapEndYear: null,
          busted: false,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onChanged } = renderBuilder({}, fetchMock);

    await fillAndSubmit(user, 'Some Player');

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
          matchedClubName: null,
          matchedOverlapStartYear: null,
          matchedOverlapEndYear: null,
          busted: true,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onChanged } = renderBuilder({}, fetchMock);

    await fillAndSubmit(user, 'Some Player');

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
          matchedClubName: null,
          matchedOverlapStartYear: null,
          matchedOverlapEndYear: null,
          busted: false,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onChanged } = renderBuilder({}, fetchMock);

    await fillAndSubmit(user, 'Nobody Real');

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

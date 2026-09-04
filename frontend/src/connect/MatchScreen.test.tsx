import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MatchScreen } from './MatchScreen';
import type { ConnectMatchDetail } from '../lib/types';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function detail(overrides: Partial<ConnectMatchDetail> = {}): ConnectMatchDetail {
  return {
    status: 'AwaitingTargetPicks',
    createdAt: '2026-09-01T00:00:00Z',
    startedAt: null,
    deadlineUtc: null,
    resolvedAt: null,
    outcome: 'Pending',
    opponentUserId: 'b2c3d4e5-0000-0000-0000-000000000000',
    opponentDisplayName: 'Opponent Olivia',
    myTargetPick: null,
    opponentTargetPick: null,
    myChainSteps: [],
    myTerminalState: { busted: false, timedOut: false, completed: false },
    opponentTerminalState: { busted: false, timedOut: false, completed: false },
    myScore: null,
    opponentScore: null,
    ...overrides,
  };
}

function stubDetailAndChat(matchDetail: ConnectMatchDetail) {
  const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
    const url = String(input);
    if (url.endsWith('/matches/match-1/chat-messages')) return jsonResponse([]);
    if (url.endsWith('/matches/match-1')) return jsonResponse(matchDetail);
    if (url.includes('/players/autocomplete')) return jsonResponse([]);
    return jsonResponse([]);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function renderScreen() {
  const onAuthError = vi.fn();
  const onBack = vi.fn();
  render(
    <MatchScreen matchId="match-1" accessToken="token" viewerUserId="me-1" onAuthError={onAuthError} onBack={onBack} />,
  );
  return { onAuthError, onBack };
}

// REQ-1404/1405/1406/1409/1410 (design-document.md SCREEN-16): the
// container that renders the right sub-screen for the match's actual
// phase — each sub-screen's own behavior is covered directly in
// TargetPickPanel.test.tsx/ChainBuilder.test.tsx/MatchResolution.test.tsx/
// MatchChat.test.tsx.
describe('MatchScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1404: renders the target-pick phase while status is AwaitingTargetPicks', async () => {
    stubDetailAndChat(detail());
    renderScreen();

    expect(await screen.findByText('Opponent: Opponent Olivia')).toBeInTheDocument();
    expect(screen.getByText('Pick your target player')).toBeInTheDocument();
  });

  it('REQ-1406: renders the chain-builder phase while status is Active with both picks locked', async () => {
    stubDetailAndChat(
      detail({
        status: 'Active',
        myTargetPick: { targetPlayerId: 't1', targetPlayerName: 'Lionel Messi', locked: true },
        opponentTargetPick: { targetPlayerId: 't2', targetPlayerName: 'Cristiano Ronaldo', locked: true },
      }),
    );
    renderScreen();

    expect(await screen.findByText('Build your chain')).toBeInTheDocument();
  });

  it('REQ-1409: renders the resolution phase once status is Resolved', async () => {
    stubDetailAndChat(
      detail({
        status: 'Resolved',
        outcome: 'Win',
        myScore: 2,
        opponentScore: 3,
        resolvedAt: '2026-09-01T05:00:00Z',
        myTargetPick: { targetPlayerId: 't1', targetPlayerName: 'Lionel Messi', locked: true },
        opponentTargetPick: { targetPlayerId: 't2', targetPlayerName: 'Cristiano Ronaldo', locked: true },
      }),
    );
    renderScreen();

    expect(await screen.findByText('You won!')).toBeInTheDocument();
  });

  it('REQ-1410: always renders chat, regardless of phase', async () => {
    stubDetailAndChat(detail());
    renderScreen();

    expect(await screen.findByText('Chat')).toBeInTheDocument();
  });

  it('clicking "Back to matches" calls onBack', async () => {
    stubDetailAndChat(detail());
    const { onBack } = renderScreen();
    await screen.findByText('Opponent: Opponent Olivia');

    screen.getByRole('button', { name: /Back to matches/ }).click();

    await waitFor(() => expect(onBack).toHaveBeenCalledTimes(1));
  });

  it('a 404 shows an inline error rather than a blank screen', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      Promise.resolve({ ok: false, status: 404, json: () => Promise.reject(new Error('no body')) } as Response),
    );
    vi.stubGlobal('fetch', fetchMock);
    renderScreen();

    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument());
  });
});

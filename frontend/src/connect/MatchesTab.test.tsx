import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MatchesTab } from './MatchesTab';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function renderTab(fetchMock = vi.fn().mockImplementation(() => jsonResponse([]))) {
  vi.stubGlobal('fetch', fetchMock);
  const onAuthError = vi.fn();
  const onOpenMatch = vi.fn();
  render(<MatchesTab accessToken="token" onAuthError={onAuthError} onOpenMatch={onOpenMatch} />);
  return { onAuthError, onOpenMatch };
}

const match = {
  matchId: 'match-1',
  opponentUserId: 'b2c3d4e5-0000-0000-0000-000000000000',
  status: 'Active',
  createdAt: '2026-09-01T00:00:00Z',
  startedAt: '2026-09-01T01:00:00Z',
  deadlineUtc: '2026-09-01T07:00:00Z',
  resolvedAt: null,
  outcome: 'Pending',
  awaitingMyAction: true,
};

// REQ-1404/1411 (design-document.md SCREEN-16's "Matches tab") — the only
// discovery surface for a caller's own matchIds.
describe('MatchesTab', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('S-218: shows an invitation-style empty state with no matches', async () => {
    renderTab();

    expect(
      await screen.findByText(
        "You don't have any xG Connect matches yet. Challenge a friend or opt into matchmaking to start one.",
      ),
    ).toBeInTheDocument();
  });

  it('REQ-1404/1411: renders each match with the opponent\'s shortUserId, status, and an "awaiting my move" indicator', async () => {
    renderTab(vi.fn().mockImplementation(() => jsonResponse([match])));

    expect(await screen.findByText(/Player B2C3D4E5/)).toBeInTheDocument();
    expect(screen.getByText(/Active/)).toBeInTheDocument();
    expect(screen.getByText(/Your move/)).toBeInTheDocument();
  });

  it('S-218: shows a resolved match\'s outcome, and no "awaiting my move" indicator', async () => {
    renderTab(
      vi
        .fn()
        .mockImplementation(() =>
          jsonResponse([{ ...match, status: 'Resolved', outcome: 'Win', awaitingMyAction: false }]),
        ),
    );

    expect(await screen.findByText(/Resolved \(You won\)/)).toBeInTheDocument();
    expect(screen.queryByText(/Your move/)).not.toBeInTheDocument();
  });

  it('S-218: clicking "View match" calls onOpenMatch with that match\'s id', async () => {
    const user = userEvent.setup();
    const { onOpenMatch } = renderTab(vi.fn().mockImplementation(() => jsonResponse([match])));

    await screen.findByText(/Player B2C3D4E5/);
    await user.click(screen.getByRole('button', { name: 'View match' }));

    expect(onOpenMatch).toHaveBeenCalledWith('match-1');
  });

  it('S-218: a null opponentUserId (REQ-710 anonymization) renders "Deleted account"', async () => {
    renderTab(vi.fn().mockImplementation(() => jsonResponse([{ ...match, opponentUserId: null }])));

    expect(await screen.findByText(/Deleted account/)).toBeInTheDocument();
  });
});

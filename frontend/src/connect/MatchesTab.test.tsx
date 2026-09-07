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
  return { onAuthError, onOpenMatch, fetchMock };
}

const match = {
  matchId: 'match-1',
  opponentUserId: 'b2c3d4e5-0000-0000-0000-000000000000',
  opponentDisplayName: 'Opponent Olivia',
  status: 'Active',
  createdAt: '2026-09-01T00:00:00Z',
  startedAt: '2026-09-01T01:00:00Z',
  deadlineUtc: '2026-09-01T07:00:00Z',
  resolvedAt: null,
  outcome: 'Pending',
  awaitingMyAction: true,
};

// REQ-1404/1411/1417 (design-document.md SCREEN-16's "Matches tab") — the
// only discovery surface for a caller's own matchIds, now bucketed into
// Not Started/Ongoing/Completed sub-tabs.
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

  it('REQ-1417: defaults to the "Not Started" sub-tab', async () => {
    renderTab(vi.fn().mockImplementation(() => jsonResponse([match])));

    // The default sub-tab is "Not Started" — an Active match isn't shown
    // until the player switches to "Ongoing".
    await screen.findByRole('tab', { name: 'Not Started' });
    expect(screen.getByRole('tab', { name: 'Not Started' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByText(/Opponent Olivia/)).not.toBeInTheDocument();
  });

  it('REQ-1404/1411: renders each match with the opponent\'s display name, status, and an "awaiting my move" indicator', async () => {
    const user = userEvent.setup();
    renderTab(vi.fn().mockImplementation(() => jsonResponse([match])));

    await screen.findByRole('tab', { name: 'Ongoing' });
    await user.click(screen.getByRole('tab', { name: 'Ongoing' }));

    expect(await screen.findByText(/Opponent Olivia/)).toBeInTheDocument();
    expect(screen.getByText(/Active/)).toBeInTheDocument();
    expect(screen.getByText(/Your move/)).toBeInTheDocument();
  });

  it('S-218: shows a resolved match\'s outcome, and no "awaiting my move" indicator', async () => {
    const user = userEvent.setup();
    renderTab(
      vi
        .fn()
        .mockImplementation(() =>
          jsonResponse([{ ...match, status: 'Resolved', outcome: 'Win', awaitingMyAction: false }]),
        ),
    );

    await screen.findByRole('tab', { name: 'Completed' });
    await user.click(screen.getByRole('tab', { name: 'Completed' }));

    expect(await screen.findByText(/Resolved \(You won\)/)).toBeInTheDocument();
    expect(screen.queryByText(/Your move/)).not.toBeInTheDocument();
  });

  it('S-218: clicking "View match" calls onOpenMatch with that match\'s id', async () => {
    const user = userEvent.setup();
    const { onOpenMatch } = renderTab(vi.fn().mockImplementation(() => jsonResponse([match])));

    await user.click(await screen.findByRole('tab', { name: 'Ongoing' }));
    await screen.findByText(/Opponent Olivia/);
    await user.click(screen.getByRole('button', { name: 'View match' }));

    expect(onOpenMatch).toHaveBeenCalledWith('match-1');
  });

  it('S-218: a null opponentDisplayName (REQ-710 anonymization) renders "a deleted user"', async () => {
    const user = userEvent.setup();
    renderTab(
      vi
        .fn()
        .mockImplementation(() =>
          jsonResponse([{ ...match, opponentUserId: null, opponentDisplayName: null }]),
        ),
    );

    await user.click(await screen.findByRole('tab', { name: 'Ongoing' }));

    expect(await screen.findByText(/a deleted user/)).toBeInTheDocument();
  });

  it('REQ-1417: buckets matches by status so each appears in exactly one sub-tab', async () => {
    const user = userEvent.setup();
    const notStarted = { ...match, matchId: 'match-not-started', status: 'AwaitingTargetPicks', outcome: 'Pending' };
    const ongoing = { ...match, matchId: 'match-ongoing', status: 'Active' };
    const completed = { ...match, matchId: 'match-completed', status: 'Resolved', outcome: 'Loss' };
    renderTab(vi.fn().mockImplementation(() => jsonResponse([notStarted, ongoing, completed])));

    // Default "Not Started" sub-tab shows only that match.
    await screen.findByRole('tab', { name: 'Not Started' });
    expect(screen.getAllByRole('listitem')).toHaveLength(1);
    expect(screen.getByText(/Awaiting target picks/)).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Ongoing' }));
    expect(screen.getAllByRole('listitem')).toHaveLength(1);
    expect(screen.getByText(/Active/)).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Completed' }));
    expect(screen.getAllByRole('listitem')).toHaveLength(1);
    expect(screen.getByText(/Resolved \(You lost\)/)).toBeInTheDocument();
  });

  it('REQ-1417: an individual empty sub-tab shows its own empty-state text, pointing at the two match-creating actions', async () => {
    renderTab(vi.fn().mockImplementation(() => jsonResponse([match])));

    // "match" is Active, so "Not Started" (the default sub-tab) is empty.
    expect(
      await screen.findByText('No matches in this list. Challenge a friend or opt into matchmaking to start one.'),
    ).toBeInTheDocument();
  });

  it('REQ-1417: switching sub-tabs does not trigger a new GET /matches request', async () => {
    const user = userEvent.setup();
    const { fetchMock } = renderTab(vi.fn().mockImplementation(() => jsonResponse([match])));

    await screen.findByRole('tab', { name: 'Not Started' });
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await user.click(screen.getByRole('tab', { name: 'Ongoing' }));
    await screen.findByText(/Opponent Olivia/);
    await user.click(screen.getByRole('tab', { name: 'Completed' }));
    await user.click(screen.getByRole('tab', { name: 'Not Started' }));

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { FriendsScreen } from './FriendsScreen';

// SCREEN-15 (REQ-1401/1402/1403, S-217): isolated coverage of the tab
// container itself — each tab's own fetch/action behavior is covered
// directly in FriendsTab.test.tsx/ChallengesTab.test.tsx/
// MatchmakingTab.test.tsx, mirroring how LeaderboardScreen.test.tsx only
// covers the cross-cutting scope-tab-bar behavior while
// AllTimeLeaderboard.test.tsx covers that one scope's own fetch/poll logic.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function renderFriendsScreen(fetchMock = vi.fn().mockImplementation(() => jsonResponse([]))) {
  vi.stubGlobal('fetch', fetchMock);
  const onAuthError = vi.fn();
  render(<FriendsScreen accessToken="token" onAuthError={onAuthError} />);
  return { onAuthError };
}

describe('FriendsScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1401/1402/1403: renders a "Friends & Challenges" heading and three tabs, defaulting to the "Friends" tab', async () => {
    renderFriendsScreen();

    expect(screen.getByRole('heading', { name: 'Friends & Challenges' })).toBeInTheDocument();
    const tabs = screen.getAllByRole('tab').map((tab) => tab.textContent);
    expect(tabs).toEqual(['Friends', 'Challenges', 'Matchmaking', 'Matches']);
    expect(screen.getByRole('tab', { name: 'Friends' })).toHaveAttribute('aria-selected', 'true');
    expect(await screen.findByText('No pending friend requests.')).toBeVisible();
  });

  it('REQ-1402: selecting the "Challenges" tab shows that panel and hides the others, without unmounting them', async () => {
    const user = userEvent.setup();
    renderFriendsScreen();
    await screen.findByText('No pending friend requests.');

    await user.click(screen.getByRole('tab', { name: 'Challenges' }));

    expect(screen.getByRole('tab', { name: 'Challenges' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'Friends' })).toHaveAttribute('aria-selected', 'false');
    expect(await screen.findByText('No pending challenges.')).toBeVisible();
    // The Friends panel is hidden (not visible), not unmounted — its own
    // "No pending friend requests." text is still in the DOM.
    expect(screen.getByText('No pending friend requests.')).not.toBeVisible();
  });

  it('REQ-1403: selecting the "Matchmaking" tab shows its "Opt in" action', async () => {
    const user = userEvent.setup();
    renderFriendsScreen();
    await screen.findByText('No pending friend requests.');

    await user.click(screen.getByRole('tab', { name: 'Matchmaking' }));

    expect(screen.getByRole('tab', { name: 'Matchmaking' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('button', { name: 'Opt in' })).toBeVisible();
  });

  // S-218 (design-document.md SCREEN-16): the "Matches" tab is this
  // screen's own drill-down entry point — MatchesTab.test.tsx/
  // MatchScreen.test.tsx cover each sub-screen's own fetch/action behavior
  // in isolation; this covers only the container-level list<->detail
  // switch, mirroring how this file covers the other three tabs.
  it('S-218: selecting the "Matches" tab shows the matches list, and opening one shows its detail with a way back', async () => {
    const match = {
      matchId: 'match-1',
      opponentUserId: 'b2c3d4e5-0000-0000-0000-000000000000',
      status: 'AwaitingTargetPicks',
      createdAt: '2026-09-01T00:00:00Z',
      startedAt: null,
      deadlineUtc: null,
      resolvedAt: null,
      outcome: 'Pending',
      awaitingMyAction: true,
    };
    const detail = {
      status: 'AwaitingTargetPicks',
      createdAt: '2026-09-01T00:00:00Z',
      startedAt: null,
      deadlineUtc: null,
      resolvedAt: null,
      outcome: 'Pending',
      opponentUserId: 'b2c3d4e5-0000-0000-0000-000000000000',
      myTargetPick: null,
      opponentTargetPick: null,
      myChainSteps: [],
      myTerminalState: { busted: false, timedOut: false, completed: false },
      opponentTerminalState: { busted: false, timedOut: false, completed: false },
      myScore: null,
      opponentScore: null,
    };
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith('/matches')) return jsonResponse([match]);
      if (url.endsWith('/matches/match-1')) return jsonResponse(detail);
      if (url.endsWith('/matches/match-1/chat-messages')) return jsonResponse([]);
      return jsonResponse([]);
    });
    const user = userEvent.setup();
    renderFriendsScreen(fetchMock);
    await screen.findByText('No pending friend requests.');

    await user.click(screen.getByRole('tab', { name: 'Matches' }));
    expect(await screen.findByText(/Player B2C3D4E5/)).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'View match' }));
    expect(await screen.findByText('Opponent: Player B2C3D4E5')).toBeVisible();

    await user.click(screen.getByRole('button', { name: /Back to matches/ }));
    expect(await screen.findByText(/Player B2C3D4E5/)).toBeVisible();
    expect(screen.queryByText('Opponent: Player B2C3D4E5')).not.toBeInTheDocument();
  });

  // S-218 regression (design-document.md SCREEN-16's "deliberate exception"
  // note): this is the exact bug the play-connect.spec.ts E2E run caught —
  // MatchesTab was kept mounted-but-hidden like the other three tabs, so its
  // useAuthedFetch-on-mount GET /matches was captured once (often before any
  // match existed) and never refreshed when the user switched back to it
  // after accepting a challenge or a matchmaking pairing elsewhere on this
  // screen. Proves the fix by returning a DIFFERENT match list on the
  // second GET /matches and checking the second render actually reflects
  // it, rather than only re-asserting the first fetch (which already passed
  // before the fix, since first-mount fetching was never broken).
  it('S-218: switching away from and back to the "Matches" tab refetches, rather than reusing stale mount-once data', async () => {
    const matchV1 = {
      matchId: 'match-1',
      opponentUserId: 'b2c3d4e5-0000-0000-0000-000000000000',
      status: 'AwaitingTargetPicks',
      createdAt: '2026-09-01T00:00:00Z',
      startedAt: null,
      deadlineUtc: null,
      resolvedAt: null,
      outcome: 'Pending',
      awaitingMyAction: true,
    };
    const matchV2 = {
      matchId: 'match-2',
      opponentUserId: 'c3d4e5f6-0000-0000-0000-000000000000',
      status: 'Active',
      createdAt: '2026-09-02T00:00:00Z',
      startedAt: '2026-09-02T00:00:01Z',
      deadlineUtc: null,
      resolvedAt: null,
      outcome: 'Pending',
      awaitingMyAction: false,
    };
    let matchesCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith('/matches')) {
        matchesCallCount += 1;
        return jsonResponse(matchesCallCount === 1 ? [matchV1] : [matchV2]);
      }
      return jsonResponse([]);
    });
    const user = userEvent.setup();
    renderFriendsScreen(fetchMock);
    await screen.findByText('No pending friend requests.');

    await user.click(screen.getByRole('tab', { name: 'Matches' }));
    expect(await screen.findByText(/Player B2C3D4E5/)).toBeVisible();
    expect(matchesCallCount).toBe(1);

    // Switching to another tab unmounts the Matches panel (unlike the other
    // three, which stay mounted under `hidden`).
    await user.click(screen.getByRole('tab', { name: 'Friends' }));
    expect(screen.queryByText(/Player B2C3D4E5/)).not.toBeInTheDocument();

    // Switching back must remount MatchesTab and issue a fresh GET
    // /matches — the second (now-current) match should appear, and the
    // first call's now-stale match should not.
    await user.click(screen.getByRole('tab', { name: 'Matches' }));
    expect(await screen.findByText(/Player C3D4E5F6/)).toBeVisible();
    expect(screen.queryByText(/Player B2C3D4E5/)).not.toBeInTheDocument();
    expect(matchesCallCount).toBe(2);
  });
});

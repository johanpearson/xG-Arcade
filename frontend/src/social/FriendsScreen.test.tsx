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

function renderFriendsScreen(
  fetchMock = vi.fn().mockImplementation(() => jsonResponse([])),
  overrides: Partial<Parameters<typeof FriendsScreen>[0]> = {},
) {
  vi.stubGlobal('fetch', fetchMock);
  const onAuthError = vi.fn();
  render(<FriendsScreen accessToken="token" onAuthError={onAuthError} {...overrides} />);
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
    expect(tabs).toEqual(['Friends', 'Challenges', 'Matchmaking']);
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

  // REQ-1411 (design-document.md SCREEN-07's badge-redesign status note,
  // 2026-09-03): the notification badge's category links open this screen
  // already on the matching tab, not always "Friends".
  it('REQ-1411: initialTab="challenges" opens directly on the "Challenges" tab instead of the default "Friends" tab', async () => {
    renderFriendsScreen(vi.fn().mockImplementation(() => jsonResponse([])), { initialTab: 'challenges' });

    expect(screen.getByRole('tab', { name: 'Challenges' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'Friends' })).toHaveAttribute('aria-selected', 'false');
    expect(await screen.findByText('No pending challenges.')).toBeVisible();
  });
});

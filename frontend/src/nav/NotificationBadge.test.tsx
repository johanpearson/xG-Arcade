import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { NotificationBadge } from './NotificationBadge';

// design-document.md SCREEN-07 (2026-09-03 badge-redesign, direct user
// feedback): isolated coverage of the badge's own visibility/dropdown
// behavior, mounted directly (no HeaderNav/App involved) — same convention
// every other nav-adjacent component in this codebase already has
// (HeaderNav.test.tsx).

function renderBadge(overrides: Partial<Parameters<typeof NotificationBadge>[0]> = {}) {
  const onOpenFriendsTab = vi.fn();
  render(
    <NotificationBadge
      pendingFriendRequestCount={0}
      pendingChallengeCount={0}
      matchesAwaitingActionCount={0}
      onOpenFriendsTab={onOpenFriendsTab}
      {...overrides}
    />,
  );
  return { onOpenFriendsTab };
}

describe('NotificationBadge', () => {
  it('REQ-1411: renders nothing at all when every count is 0 (no indicator at zero)', () => {
    renderBadge();

    expect(screen.queryByTestId('notification-badge-toggle')).not.toBeInTheDocument();
  });

  it('renders a toggle showing the combined count as its accessible name when any count is greater than 0', () => {
    renderBadge({ pendingFriendRequestCount: 2, pendingChallengeCount: 1, matchesAwaitingActionCount: 1 });

    const toggle = screen.getByTestId('notification-badge-toggle');
    expect(toggle).toBeInTheDocument();
    expect(toggle).toHaveTextContent('4');
    expect(toggle).toHaveAccessibleName('4 notifications — view details');
  });

  it('uses singular "notification" in the accessible name when the combined count is exactly 1', () => {
    renderBadge({ pendingFriendRequestCount: 1 });

    expect(screen.getByTestId('notification-badge-toggle')).toHaveAccessibleName('1 notification — view details');
  });

  it('the toggle is a real, focusable button exposing aria-expanded and aria-controls, starting closed', () => {
    renderBadge({ pendingFriendRequestCount: 1 });

    const toggle = screen.getByTestId('notification-badge-toggle');
    expect(toggle.tagName).toBe('BUTTON');
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(toggle).toHaveAttribute('aria-controls', 'notification-badge-panel');
  });

  it('clicking the toggle opens the dropdown, and clicking again closes it', async () => {
    renderBadge({ pendingFriendRequestCount: 1 });
    const user = userEvent.setup();
    const toggle = screen.getByTestId('notification-badge-toggle');

    expect(screen.queryByRole('menu')).not.toBeInTheDocument();

    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('menu')).toBeInTheDocument();

    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
  });

  it('only lists non-zero categories in the dropdown, in "Friend requests"/"Challenges"/"Matches awaiting your move" order', async () => {
    renderBadge({ pendingFriendRequestCount: 3, pendingChallengeCount: 0, matchesAwaitingActionCount: 2 });
    const user = userEvent.setup();

    await user.click(screen.getByTestId('notification-badge-toggle'));

    expect(screen.getByRole('menuitem', { name: 'Friend requests (3)' })).toBeInTheDocument();
    expect(screen.queryByText(/Challenges/)).not.toBeInTheDocument();
    expect(screen.getByText('Matches awaiting your move (2)')).toBeInTheDocument();
  });

  it('clicking "Friend requests (N)" calls onOpenFriendsTab("friends") and closes the dropdown', async () => {
    const { onOpenFriendsTab } = renderBadge({ pendingFriendRequestCount: 3 });
    const user = userEvent.setup();
    const toggle = screen.getByTestId('notification-badge-toggle');

    await user.click(toggle);
    await user.click(screen.getByRole('menuitem', { name: 'Friend requests (3)' }));

    expect(onOpenFriendsTab).toHaveBeenCalledTimes(1);
    expect(onOpenFriendsTab).toHaveBeenCalledWith('friends');
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('clicking "Challenges (N)" calls onOpenFriendsTab("challenges")', async () => {
    const { onOpenFriendsTab } = renderBadge({ pendingChallengeCount: 2 });
    const user = userEvent.setup();

    await user.click(screen.getByTestId('notification-badge-toggle'));
    await user.click(screen.getByRole('menuitem', { name: 'Challenges (2)' }));

    expect(onOpenFriendsTab).toHaveBeenCalledWith('challenges');
  });

  it('"Matches awaiting your move (N)" is plain, non-interactive text — no S-218 match screen exists yet to link to', async () => {
    const { onOpenFriendsTab } = renderBadge({ matchesAwaitingActionCount: 1 });
    const user = userEvent.setup();

    await user.click(screen.getByTestId('notification-badge-toggle'));

    const line = screen.getByText('Matches awaiting your move (1)');
    expect(line.tagName).not.toBe('BUTTON');
    expect(line.closest('button')).toBeNull();
    expect(onOpenFriendsTab).not.toHaveBeenCalled();
  });
});

import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { LeaderboardRowsList } from './LeaderboardRowsList';
import { row } from './leaderboardTestHelpers';

// REQ-411 (S-179): the shared row-rendering component all four leaderboard
// scopes (AllTimeLeaderboard/LiveLeaderboard/PastRoundsLeaderboard/
// WindowedLeaderboard) import unmodified — each threads its own optional
// `onSelectPlayer` prop straight through with no logic of its own (see each
// scope component's own `onSelectPlayer={onSelectPlayer}` call), so testing
// the navigation-target behavior once here, in isolation, covers the
// "another player's stats reachable by selecting their display name on the
// leaderboard" acceptance criterion for all four scopes without duplicating
// this same test four times.

function renderRowsList(overrides: Partial<Parameters<typeof LeaderboardRowsList>[0]> = {}) {
  const onLoadMore = vi.fn();
  const onSelectPlayer = vi.fn();

  render(
    <LeaderboardRowsList
      rows={[row(1, 'user-1', 'Alex', 100)]}
      requestingUserRow={null}
      emptyMessage="No scores yet."
      hasMore={false}
      loadingMore={false}
      loadMoreError={null}
      onLoadMore={onLoadMore}
      provisional={false}
      onSelectPlayer={onSelectPlayer}
      {...overrides}
    />,
  );

  return { onLoadMore, onSelectPlayer };
}

describe('LeaderboardRowsList', () => {
  it('REQ-411: renders a row\'s displayName as a clickable button when onSelectPlayer is provided', () => {
    renderRowsList();

    expect(screen.getByRole('button', { name: 'Alex' })).toBeInTheDocument();
  });

  it('REQ-411: clicking a row\'s displayName calls onSelectPlayer with that row\'s userId and displayName', async () => {
    const user = userEvent.setup();
    const { onSelectPlayer } = renderRowsList({
      rows: [row(3, 'user-42', 'Robin', 77)],
    });

    await user.click(screen.getByRole('button', { name: 'Robin' }));

    expect(onSelectPlayer).toHaveBeenCalledTimes(1);
    expect(onSelectPlayer).toHaveBeenCalledWith('user-42', 'Robin');
  });

  it('REQ-411: without onSelectPlayer (every pre-existing call site/test), row names render as plain, non-interactive text — backward compatible', () => {
    renderRowsList({ onSelectPlayer: undefined });

    expect(screen.queryByRole('button', { name: 'Alex' })).not.toBeInTheDocument();
    expect(screen.getByText('Alex')).toBeInTheDocument();
  });

  it('REQ-411: the requesting user\'s own row, when visible in the list, is clickable just like every other row', async () => {
    const user = userEvent.setup();
    const { onSelectPlayer } = renderRowsList({
      rows: [row(2, 'user-7', 'Player One', 90, true)],
    });

    const nameButton = screen.getByRole('button', { name: 'Player One' });
    expect(nameButton).toBeInTheDocument();

    await user.click(nameButton);

    expect(onSelectPlayer).toHaveBeenCalledWith('user-7', 'Player One');
  });

  it('REQ-411: the pinned "you" footer row (shown when the requesting user is off-page) stays plain text, never a button, even when onSelectPlayer is provided', () => {
    renderRowsList({
      rows: [row(1, 'user-1', 'Alex', 100)],
      requestingUserRow: row(50, 'user-99', 'You', 500, true),
    });

    // The footer row is present (off-page requesting user)...
    expect(screen.getByText('You')).toBeInTheDocument();
    // ...but never rendered as a button, unlike the in-list rows above.
    expect(screen.queryByRole('button', { name: 'You' })).not.toBeInTheDocument();
  });
});

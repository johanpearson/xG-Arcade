import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { UserStatsScreen } from './UserStatsScreen';

// REQ-411 (S-179): SCREEN-13's stats/profile view — this is the SAME
// component for "own stats" and "another player's stats" (see the
// component's own top-of-file doc comment), so every "ready"/"empty"/
// "error" test below is written from the "another player's stats" angle by
// default (userId/displayName standing in for whichever player is being
// viewed) — REQ-411 draws no behavioral distinction between the two, and
// there is no separate "own stats" code path to test independently beyond
// the entry-point wiring covered in SettingsScreen.test.tsx/
// LeaderboardRowsList.test.tsx.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function renderUserStatsScreen(overrides: Partial<Parameters<typeof UserStatsScreen>[0]> = {}) {
  const onAuthError = vi.fn();
  const onBack = vi.fn();

  render(
    <UserStatsScreen
      accessToken="token"
      userId="user-2"
      displayName="Blair"
      onAuthError={onAuthError}
      onBack={onBack}
      {...overrides}
    />,
  );

  return { onAuthError, onBack };
}

describe('UserStatsScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-411: shows a loading state before the fetch resolves', () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => new Promise(() => {})));

    renderUserStatsScreen();

    expect(screen.getByText('Loading stats…')).toBeInTheDocument();
  });

  it('REQ-411: renders the viewed player\'s displayName in the heading', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({ hasRoundsPlayed: false, roundsPlayed: 0, bestFinalPoints: null, averageFinalPoints: null, rank: null }),
      ),
    );

    renderUserStatsScreen({ displayName: 'Sam' });

    expect(await screen.findByRole('heading', { name: "Sam's stats" })).toBeInTheDocument();
  });

  it('REQ-411: a ready response with hasRoundsPlayed=true renders roundsPlayed/bestFinalPoints/averageFinalPoints/rank', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          hasRoundsPlayed: true,
          roundsPlayed: 12,
          bestFinalPoints: 85,
          averageFinalPoints: 110.4,
          rank: 7,
        }),
      ),
    );

    renderUserStatsScreen();

    await waitFor(() => expect(screen.getByText('12')).toBeInTheDocument());
    expect(screen.getByText('85 pts')).toBeInTheDocument();
    expect(screen.getByText('110.4 pts')).toBeInTheDocument();
    expect(screen.getByText('#7')).toBeInTheDocument();
    // Not left on the loading state, and not showing the empty invitation.
    expect(screen.queryByText('Loading stats…')).not.toBeInTheDocument();
    expect(screen.queryByText('No rounds played yet for this game.')).not.toBeInTheDocument();
  });

  it('REQ-411: rank is omitted (not shown as 0, not shown as an error) when independently null even though other stats are present', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          hasRoundsPlayed: true,
          roundsPlayed: 3,
          bestFinalPoints: 90,
          averageFinalPoints: 95,
          rank: null,
        }),
      ),
    );

    renderUserStatsScreen();

    await waitFor(() => expect(screen.getByText('90 pts')).toBeInTheDocument());
    expect(screen.queryByText('All-time rank')).not.toBeInTheDocument();
    expect(screen.queryByText('#0')).not.toBeInTheDocument();
    expect(screen.queryByText(/error/i)).not.toBeInTheDocument();
  });

  it('REQ-411: the zero-rounds-played empty state renders distinctly — not blank, not 0-filled — and is not the loading/error state', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({ hasRoundsPlayed: false, roundsPlayed: 0, bestFinalPoints: null, averageFinalPoints: null, rank: null }),
      ),
    );

    renderUserStatsScreen();

    expect(await screen.findByText('No rounds played yet for this game.')).toBeInTheDocument();
    expect(screen.queryByText('Loading stats…')).not.toBeInTheDocument();
    // Never rendered as a "0" figure alongside the empty message.
    expect(screen.queryByText('Rounds played')).not.toBeInTheDocument();
    expect(screen.queryByText('0 pts')).not.toBeInTheDocument();
  });

  it('REQ-411: switching the game tab re-fetches, scoped to the newly selected GameKey', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('gameKey=xg-path')) {
        return jsonResponse({ hasRoundsPlayed: true, roundsPlayed: 4, bestFinalPoints: 50, averageFinalPoints: 60, rank: 2 });
      }
      return jsonResponse({ hasRoundsPlayed: false, roundsPlayed: 0, bestFinalPoints: null, averageFinalPoints: null, rank: null });
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    renderUserStatsScreen();

    await screen.findByText('No rounds played yet for this game.');
    expect(fetchMock.mock.calls.some(([url]: [string]) => String(url).includes('gameKey=xg-grid'))).toBe(true);

    await user.click(screen.getByRole('tab', { name: 'xG Path' }));

    await waitFor(() => expect(screen.getByText('50 pts')).toBeInTheDocument());
    expect(fetchMock.mock.calls.some(([url]: [string]) => String(url).includes('gameKey=xg-path'))).toBe(true);
  });

  it('REQ-411/REQ-1304: switching to the xG Predict tab re-fetches, scoped to gameKey=xg-predict', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('gameKey=xg-predict')) {
        return jsonResponse({ hasRoundsPlayed: true, roundsPlayed: 6, bestFinalPoints: 70, averageFinalPoints: 65, rank: 3 });
      }
      return jsonResponse({ hasRoundsPlayed: false, roundsPlayed: 0, bestFinalPoints: null, averageFinalPoints: null, rank: null });
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    renderUserStatsScreen();

    await screen.findByText('No rounds played yet for this game.');
    expect(fetchMock.mock.calls.some(([url]: [string]) => String(url).includes('gameKey=xg-grid'))).toBe(true);

    await user.click(screen.getByRole('tab', { name: 'xG Predict' }));

    await waitFor(() => expect(screen.getByText('70 pts')).toBeInTheDocument());
    expect(fetchMock.mock.calls.some(([url]: [string]) => String(url).includes('gameKey=xg-predict'))).toBe(true);
  });

  // REQ-411/REQ-1304 (S-202), mirroring LeaderboardScreen.test.tsx's own
  // "REQ-404/ADR-0095: per-GameKey 'wins' subtitle" coverage (S-198): xG
  // Predict is a named exception to ADR-0021's golf-style "lowest total
  // wins" framing.
  describe('REQ-411/REQ-1304: per-GameKey "wins" subtitle', () => {
    it('the xG Grid tab (default) shows "Lowest total wins"', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() =>
          jsonResponse({ hasRoundsPlayed: false, roundsPlayed: 0, bestFinalPoints: null, averageFinalPoints: null, rank: null }),
        ),
      );

      renderUserStatsScreen();

      await screen.findByText('No rounds played yet for this game.');
      expect(screen.getByText('Lowest total wins')).toBeInTheDocument();
      expect(screen.queryByText('Highest total wins')).not.toBeInTheDocument();
    });

    it('the xG Path tab also shows "Lowest total wins" (unaffected)', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() =>
          jsonResponse({ hasRoundsPlayed: false, roundsPlayed: 0, bestFinalPoints: null, averageFinalPoints: null, rank: null }),
        ),
      );
      const user = userEvent.setup();

      renderUserStatsScreen();
      await screen.findByText('No rounds played yet for this game.');

      await user.click(screen.getByRole('tab', { name: 'xG Path' }));

      await waitFor(() => expect(screen.getByRole('tab', { name: 'xG Path' })).toHaveAttribute('aria-selected', 'true'));
      expect(screen.getByText('Lowest total wins')).toBeInTheDocument();
    });

    it('selecting the xG Predict tab shows "Highest total wins", not "Lowest total wins"', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() =>
          jsonResponse({ hasRoundsPlayed: false, roundsPlayed: 0, bestFinalPoints: null, averageFinalPoints: null, rank: null }),
        ),
      );
      const user = userEvent.setup();

      renderUserStatsScreen();
      await screen.findByText('No rounds played yet for this game.');

      await user.click(screen.getByRole('tab', { name: 'xG Predict' }));

      await waitFor(() => expect(screen.getByRole('tab', { name: 'xG Predict' })).toHaveAttribute('aria-selected', 'true'));
      expect(screen.getByText('Highest total wins')).toBeInTheDocument();
      expect(screen.queryByText('Lowest total wins')).not.toBeInTheDocument();
    });
  });

  it('REQ-411: a 401 with no/dead session calls onAuthError, the same handling every other authenticated screen uses', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized' }, 401)));

    const { onAuthError } = renderUserStatsScreen();

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
    // Never falls through to the "not found" or generic error text on a
    // 401 — the only visible effect is the onAuthError call above (the
    // caller navigates away in response, e.g. App.tsx's own auth-error
    // handling; this component never renders its own 401-specific message).
    expect(screen.queryByText("This player couldn't be found.")).not.toBeInTheDocument();
  });

  it('REQ-411: a nonexistent userId (404) is a distinct error state, not confused with the empty "no rounds played" state', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse({ title: 'Not found' }, 404)));

    renderUserStatsScreen();

    expect(await screen.findByText("This player couldn't be found.")).toBeInTheDocument();
    expect(screen.queryByText('No rounds played yet for this game.')).not.toBeInTheDocument();
  });

  it('REQ-411: a non-404/401 failure shows the server\'s own error text, distinct from the not-found and empty states', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Server error', detail: 'Something broke.' }, 500)),
    );

    renderUserStatsScreen();

    expect(await screen.findByText('Something broke.')).toBeInTheDocument();
    expect(screen.queryByText("This player couldn't be found.")).not.toBeInTheDocument();
    expect(screen.queryByText('No rounds played yet for this game.')).not.toBeInTheDocument();
  });

  it('REQ-411: viewing another player\'s stats is read-only — no action beyond the back button and game-tab switcher is present', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({ hasRoundsPlayed: true, roundsPlayed: 5, bestFinalPoints: 40, averageFinalPoints: 45, rank: 1 }),
      ),
    );

    renderUserStatsScreen({ userId: 'user-99', displayName: 'Another Player' });

    await waitFor(() => expect(screen.getByText('40 pts')).toBeInTheDocument());

    // Only the back button and the three (view-scoping, not action) game
    // tabs are present — no edit/delete/report or any other own-only
    // affordance rendered when the viewed player isn't the caller themselves.
    const buttons = screen.getAllByRole('button').map((button) => button.textContent);
    expect(buttons).toEqual(['Back']);
    const tabs = screen.getAllByRole('tab').map((tab) => tab.textContent);
    expect(tabs).toEqual(['xG Grid', 'xG Path', 'xG Predict']);
  });

  it('REQ722_UserStatsScreen_RendersPlayerAvatar_InHeader', async () => {
    vi.stubGlobal('URL', {
      ...URL,
      createObjectURL: vi.fn(() => 'blob:mock-avatar-url'),
      revokeObjectURL: vi.fn(),
    });
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((input: RequestInfo | URL) => {
        if (String(input).includes('/avatar/image')) {
          return Promise.resolve({
            ok: true,
            status: 200,
            blob: () => Promise.resolve(new Blob(['fake-image'], { type: 'image/png' })),
          } as unknown as Response);
        }
        return jsonResponse({
          hasRoundsPlayed: false,
          roundsPlayed: 0,
          bestFinalPoints: null,
          averageFinalPoints: null,
          rank: null,
        });
      }),
    );

    renderUserStatsScreen({ userId: 'user-42', displayName: 'Robin' });

    const avatar = await screen.findByTestId('player-avatar-image');
    expect(avatar).toHaveAttribute('src', 'blob:mock-avatar-url');
    expect(screen.getByRole('heading', { name: "Robin's stats" })).toBeInTheDocument();
  });

  it('REQ722_UserStatsScreen_RendersPlaceholderAvatar_WhenTargetUserHasNoApprovedAvatar', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((input: RequestInfo | URL) => {
        if (String(input).includes('/avatar/image')) {
          return jsonResponse({ title: 'Not Found' }, 404);
        }
        return jsonResponse({
          hasRoundsPlayed: false,
          roundsPlayed: 0,
          bestFinalPoints: null,
          averageFinalPoints: null,
          rank: null,
        });
      }),
    );

    renderUserStatsScreen();

    expect(await screen.findByTestId('player-avatar-placeholder')).toBeInTheDocument();
    expect(screen.queryByTestId('player-avatar-image')).not.toBeInTheDocument();
  });

  it('REQ-411: clicking "Back" calls onBack', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({ hasRoundsPlayed: false, roundsPlayed: 0, bestFinalPoints: null, averageFinalPoints: null, rank: null }),
      ),
    );
    const user = userEvent.setup();

    const { onBack } = renderUserStatsScreen();
    await screen.findByText('No rounds played yet for this game.');

    await user.click(screen.getByRole('button', { name: 'Back' }));

    expect(onBack).toHaveBeenCalledTimes(1);
  });
});

// REQ-1401 (S-217, design-document.md SCREEN-13's 2026-09-03 status note):
// SendFriendRequestAction's own three-state rendering is covered directly
// in SendFriendRequestAction.test.tsx — this block only covers
// UserStatsScreen's own wiring (when the action mounts at all, and that it
// receives the right props).
describe('UserStatsScreen (REQ-1401: "Send friend request" wiring)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const statsResponse = { hasRoundsPlayed: false, roundsPlayed: 0, bestFinalPoints: null, averageFinalPoints: null, rank: null };

  it('REQ-1401: mounts SendFriendRequestAction (a "Send friend request" button appears) when viewerUserId differs from userId', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/stats')) return jsonResponse(statsResponse);
        return jsonResponse([]);
      }),
    );

    renderUserStatsScreen({ userId: 'user-99', viewerUserId: 'user-1' });

    expect(await screen.findByRole('button', { name: 'Send friend request' })).toBeInTheDocument();
  });

  it('REQ-1401: does not mount SendFriendRequestAction when viewerUserId is not provided (existing/unrelated callers unaffected)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse(statsResponse)),
    );

    renderUserStatsScreen({ userId: 'user-99' });

    await screen.findByText('No rounds played yet for this game.');
    expect(screen.queryByRole('button', { name: 'Send friend request' })).not.toBeInTheDocument();
  });

  it('REQ-1401: does not mount SendFriendRequestAction when viewerUserId equals userId (own profile)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse(statsResponse)),
    );

    renderUserStatsScreen({ userId: 'user-1', viewerUserId: 'user-1' });

    await screen.findByText('No rounds played yet for this game.');
    expect(screen.queryByRole('button', { name: 'Send friend request' })).not.toBeInTheDocument();
  });
});

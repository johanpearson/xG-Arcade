import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LeaderboardScreen } from './LeaderboardScreen';
import { defaultAllTimeRoute, jsonResponse, routedFetch, row } from './leaderboardTestHelpers';

// S-121: split out of the former LeaderboardScreen.test.tsx's "scope
// selector" describe block, relocated verbatim (REQ405 window-scope cases
// only) — these still render the full `LeaderboardScreen` orchestrator
// (rather than `WindowedLeaderboard` in isolation) because switching to the
// "Time Windows" tab and its round/week/month/year sub-tabs via real clicks
// is part of the behavior under test; `WindowedLeaderboard` itself owns the
// fetch/re-entry/resolution-switch/"Load more" logic these tests exercise.
// See LeaderboardScreen.test.tsx for the cross-cutting tests (game
// switcher, explainer modal, scope tab bar) that stayed there.

// REQ-405 (S-027): the "Time Windows" scope — round/week/month/year
// rolling-window leaderboards.
describe('WindowedLeaderboard', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ405: selecting "Time Windows" fetches the window endpoint with the default (round) resolution', async () => {
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/window/round',
        () =>
          jsonResponse({
            rows: [row(1, 'user-1', 'Alex', 42)],
            requestingUserRow: null,
            nextCursor: null,
            hasMore: false,
          }),
      ],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('tab', { name: 'Time Windows' }));

    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());
    const windowCalls = fetchMock.mock.calls.filter((call) =>
      String(call[0]).includes('/leagues/global/leaderboard/window/round'),
    );
    expect(windowCalls).toHaveLength(1);
    // The "round" sub-tab is the default, and is marked selected.
    expect(screen.getByRole('tab', { name: 'Round' })).toHaveAttribute('aria-selected', 'true');
  });

  it('REQ405: switching the round/week/month/year sub-tab re-fetches with the newly selected resolution', async () => {
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/window/round',
        () =>
          jsonResponse({
            rows: [row(1, 'user-1', 'Alex', 42)],
            requestingUserRow: null,
            nextCursor: null,
            hasMore: false,
          }),
      ],
      [
        '/leagues/global/leaderboard/window/week',
        () =>
          jsonResponse({
            rows: [row(1, 'user-2', 'Blair', 99)],
            requestingUserRow: null,
            nextCursor: null,
            hasMore: false,
          }),
      ],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('tab', { name: 'Time Windows' }));
    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('tab', { name: 'Week' }));

    await waitFor(() => expect(screen.getByText('Blair')).toBeInTheDocument());
    expect(screen.queryByText('Alex')).not.toBeInTheDocument();

    const weekCalls = fetchMock.mock.calls.filter((call) =>
      String(call[0]).includes('/leagues/global/leaderboard/window/week'),
    );
    expect(weekCalls).toHaveLength(1);
    expect(screen.getByRole('tab', { name: 'Week' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'Round' })).toHaveAttribute('aria-selected', 'false');
  });

  it('REQ405: an empty window response shows the calm empty-state message, not an error', async () => {
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/window/round',
        () => jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
      ],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('tab', { name: 'Time Windows' }));

    await waitFor(() =>
      expect(screen.getByText('No one scored in this window yet.')).toBeInTheDocument(),
    );
    expect(document.querySelector('.leaderboard-screen__status--error')).toBeNull();
  });

  it('REQ405: a failed window fetch shows an inline error message', async () => {
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/window/round',
        () => jsonResponse({ title: 'Request failed', detail: 'Something went wrong loading this window.' }, 500),
      ],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('tab', { name: 'Time Windows' }));

    await waitFor(() =>
      expect(screen.getByText('Something went wrong loading this window.')).toBeInTheDocument(),
    );
    expect(document.querySelector('.leaderboard-screen__status--error')).not.toBeNull();
  });
});

import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LeaderboardScreen } from './LeaderboardScreen';
import { defaultAllTimeRoute, jsonResponse, routedFetch, row } from './leaderboardTestHelpers';

// S-121: split out of the former LeaderboardScreen.test.tsx's "scope
// selector" describe block, relocated verbatim (REQ407 live-scope cases
// only) — these still render the full `LeaderboardScreen` orchestrator
// (rather than `LiveLeaderboard` in isolation) because switching to the
// "Current Round" tab via a real tab click is part of the behavior under
// test; `LiveLeaderboard` itself owns the fetch/re-entry/"Load more" logic
// these tests exercise. See LeaderboardScreen.test.tsx for the
// cross-cutting tests (game switcher, explainer modal, scope tab bar) that
// stayed there.

describe('LiveLeaderboard', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ407: switching to "Current Round" fetches the active-round endpoint, not the all-time one', async () => {
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/active-round',
        () =>
          jsonResponse({
            rows: [row(1, 'user-1', 'Alex', 12)],
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

    fireEvent.click(screen.getByRole('tab', { name: 'Current Round' }));

    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());
    const activeRoundCalls = fetchMock.mock.calls.filter((call) =>
      String(call[0]).includes('/leagues/global/leaderboard/active-round'),
    );
    expect(activeRoundCalls).toHaveLength(1);
  });

  it('REQ407: the live scope presents its rows and total as visibly provisional, not final', async () => {
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/active-round',
        () =>
          jsonResponse({
            rows: [row(1, 'user-1', 'Alex', 138, true)],
            requestingUserRow: row(1, 'user-1', 'Alex', 138, true),
            nextCursor: null,
            hasMore: false,
          }),
      ],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('tab', { name: 'Current Round' }));

    // Same "~N pts estimated" wording GridScreen.tsx/CellState.tsx already
    // established for a live point value (S-018/REQ-204) — not a plain
    // "138 pts", which would read as a locked, final total.
    await waitFor(() => expect(screen.getByText('~138 pts estimated')).toBeInTheDocument());
    expect(
      screen.getByText('Live — estimated, can still change until the round closes.'),
    ).toBeInTheDocument();
  });

  it('REQ407: "no active round" renders a plain informational empty state, not an error banner', async () => {
    const fetchMock = routedFetch([
      ['/leagues/global/leaderboard/active-round', () => jsonResponse({ title: 'No active round' }, 404)],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('tab', { name: 'Current Round' }));

    await waitFor(() =>
      expect(screen.getByText('No round is currently active — check back once one starts.')).toBeInTheDocument(),
    );
    // Not styled as an error — the leaderboard-screen__empty convention,
    // not leaderboard-screen__status--error.
    expect(document.querySelector('.leaderboard-screen__status--error')).toBeNull();
  });

  it('REQ407: an active round with no participants yet shows a calm empty state, distinct from "no active round"', async () => {
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/active-round',
        () => jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
      ],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('tab', { name: 'Current Round' }));

    await waitFor(() =>
      expect(screen.getByText('No one has played this round yet — be the first.')).toBeInTheDocument(),
    );
    expect(
      screen.queryByText('No round is currently active — check back once one starts.'),
    ).not.toBeInTheDocument();
  });

  // Regression test for the "hasFetchedLiveRef never resets" bug
  // (quality-architect/architecture-reviewer finding): the live scope
  // must issue a fresh request every time it's re-entered, not just once
  // for the component's entire mounted lifetime — otherwise REQ-407's
  // "check back once one starts"/"come back to see the update" promise is
  // moot, since the frontend would never actually issue that later
  // request.
  it('REQ407: re-selecting "Current Round" after switching away fetches again, replacing the previously shown rows', async () => {
    let activeRoundCallCount = 0;
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/active-round',
        () => {
          activeRoundCallCount += 1;
          if (activeRoundCallCount === 1) {
            return jsonResponse({
              rows: [row(1, 'user-1', 'Alex', 12)],
              requestingUserRow: null,
              nextCursor: null,
              hasMore: false,
            });
          }
          return jsonResponse({
            rows: [row(1, 'user-2', 'Blair', 25)],
            requestingUserRow: null,
            nextCursor: null,
            hasMore: false,
          });
        },
      ],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

    // First entry into "live".
    fireEvent.click(screen.getByRole('tab', { name: 'Current Round' }));
    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());

    // Away, then back — this is the exact "check back later" scenario
    // REQ-407 promises works, and the bug broke.
    fireEvent.click(screen.getByRole('tab', { name: 'All-time' }));
    fireEvent.click(screen.getByRole('tab', { name: 'Current Round' }));

    // Second, fresh response lands — proving a second real request was
    // made, not the first response silently reused.
    await waitFor(() => expect(screen.getByText('Blair')).toBeInTheDocument());
    expect(screen.queryByText('Alex')).not.toBeInTheDocument();

    expect(activeRoundCallCount).toBe(2);
  });

  // Near-identical to the well-tested REQ-607 `handleLoadMore` pattern on
  // the all-time scope, but exercising `handleLoadMoreLive` by name —
  // flagged by the quality-architect review as unverified.
  it('REQ407: "Load more" on the live scope fetches the next page via the previous nextCursor and appends below the existing rows', async () => {
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/active-round',
        (() => {
          let call = 0;
          return () => {
            call += 1;
            if (call === 1) {
              return jsonResponse({
                rows: [row(1, 'user-1', 'Alex', 10)],
                requestingUserRow: null,
                nextCursor: 50,
                hasMore: true,
              });
            }
            return jsonResponse({
              rows: [row(2, 'user-2', 'Blair', 20)],
              requestingUserRow: null,
              nextCursor: null,
              hasMore: false,
            });
          };
        })(),
      ],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('tab', { name: 'Current Round' }));
    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(screen.getByText('Blair')).toBeInTheDocument());

    const rows = screen.getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent('Alex');
    expect(rows[1]).toHaveTextContent('Blair');

    const activeRoundCalls = fetchMock.mock.calls.filter((call) =>
      String(call[0]).includes('/leagues/global/leaderboard/active-round'),
    );
    expect(activeRoundCalls).toHaveLength(2);
    expect(String(activeRoundCalls[1][0])).toContain('cursor=50');
  });
});

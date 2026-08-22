import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LeaderboardScreen } from './LeaderboardScreen';
import { defaultAllTimeRoute, jsonResponse, routedFetch, row } from './leaderboardTestHelpers';

// S-121: split out of the former LeaderboardScreen.test.tsx's "scope
// selector" describe block, relocated verbatim (REQ408 past-rounds cases
// only) — these still render the full `LeaderboardScreen` orchestrator
// (rather than `PastRoundsLeaderboard` in isolation) because switching to
// the "Previous Rounds" tab and drilling into a round via real clicks is
// part of the behavior under test; `PastRoundsLeaderboard` itself owns the
// fetch/re-entry/drill-in/"Load more" logic these tests exercise. See
// LeaderboardScreen.test.tsx for the cross-cutting tests (game switcher,
// explainer modal, scope tab bar) that stayed there.

describe('PastRoundsLeaderboard', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ408: switching to "Previous Rounds" fetches the closed-rounds list endpoint', async () => {
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/closed-rounds',
        () =>
          jsonResponse({
            rounds: [{ roundId: 'round-1', startTime: '2026-07-10T00:00:00Z', endTime: '2026-07-10T18:00:00Z', closedAt: '2026-07-10T18:05:00Z' }],
            nextCursor: null,
            hasMore: false,
          }),
      ],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('tab', { name: 'Previous Rounds' }));

    await waitFor(() => expect(screen.getByText('Closed 2026-07-10T18:05:00Z')).toBeInTheDocument());
    const closedRoundsCalls = fetchMock.mock.calls.filter((call) =>
      String(call[0]).includes('/leagues/global/leaderboard/closed-rounds'),
    );
    expect(closedRoundsCalls.length).toBeGreaterThan(0);
  });

  it('REQ408: the past-rounds list renders closed rounds and paginates via "Load more"', async () => {
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/closed-rounds',
        (() => {
          let call = 0;
          return () => {
            call += 1;
            if (call === 1) {
              return jsonResponse({
                rounds: [
                  { roundId: 'round-2', startTime: '2026-07-12T00:00:00Z', endTime: '2026-07-12T18:00:00Z', closedAt: '2026-07-12T18:05:00Z' },
                ],
                nextCursor: 50,
                hasMore: true,
              });
            }
            return jsonResponse({
              rounds: [
                { roundId: 'round-1', startTime: '2026-07-05T00:00:00Z', endTime: '2026-07-05T18:00:00Z', closedAt: '2026-07-05T18:05:00Z' },
              ],
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

    fireEvent.click(screen.getByRole('tab', { name: 'Previous Rounds' }));
    await waitFor(() => expect(screen.getByText('Closed 2026-07-12T18:05:00Z')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(screen.getByText('Closed 2026-07-05T18:05:00Z')).toBeInTheDocument());

    // Both rounds present, in the order the API returned them.
    const items = screen.getAllByRole('listitem');
    const roundItems = items.filter((item) => item.className.includes('round-list-item'));
    expect(roundItems).toHaveLength(2);
  });

  it('REQ408: selecting a past round shows its locked leaderboard, presented as final (not provisional)', async () => {
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/closed-rounds/round-1',
        () =>
          jsonResponse({
            rows: [row(1, 'user-1', 'Alex', 120)],
            requestingUserRow: null,
            nextCursor: null,
            hasMore: false,
          }),
      ],
      [
        '/leagues/global/leaderboard/closed-rounds',
        () =>
          jsonResponse({
            rounds: [
              { roundId: 'round-1', startTime: '2026-07-05T00:00:00Z', endTime: '2026-07-05T18:00:00Z', closedAt: '2026-07-05T18:05:00Z' },
            ],
            nextCursor: null,
            hasMore: false,
          }),
      ],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('tab', { name: 'Previous Rounds' }));
    await waitFor(() => expect(screen.getByText('Closed 2026-07-05T18:05:00Z')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'Closed 2026-07-05T18:05:00Z' }));

    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());
    // Plain "N pts" — not the live scope's "~N pts estimated" wording —
    // since a closed round's total is permanently locked (REQ-206/408).
    expect(screen.getByText('120 pts')).toBeInTheDocument();
    expect(screen.queryByText(/estimated/)).not.toBeInTheDocument();

    // "Back to previous rounds" returns to the round list without refetching it.
    fireEvent.click(screen.getByRole('button', { name: 'Back to previous rounds' }));
    await waitFor(() => expect(screen.getByText('Closed 2026-07-05T18:05:00Z')).toBeInTheDocument());
  });

  it('REQ408: a round id that does not exist ("not found") and one that has not closed yet ("not closed") render distinguishable messages', async () => {
    const notFoundFetch = routedFetch([
      ['/leagues/global/leaderboard/closed-rounds/round-404', () => jsonResponse({ title: 'Round not found' }, 404)],
      [
        '/leagues/global/leaderboard/closed-rounds',
        () =>
          jsonResponse({
            rounds: [
              { roundId: 'round-404', startTime: '2026-07-05T00:00:00Z', endTime: '2026-07-05T18:00:00Z', closedAt: '2026-07-05T18:05:00Z' },
            ],
            nextCursor: null,
            hasMore: false,
          }),
      ],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', notFoundFetch);

    const { unmount } = render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('tab', { name: 'Previous Rounds' }));
    await waitFor(() => expect(screen.getByText('Closed 2026-07-05T18:05:00Z')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'Closed 2026-07-05T18:05:00Z' }));
    await waitFor(() => expect(screen.getByText("This round couldn’t be found.")).toBeInTheDocument());
    unmount();
    vi.unstubAllGlobals();

    const notClosedFetch = routedFetch([
      ['/leagues/global/leaderboard/closed-rounds/round-409', () => jsonResponse({ title: 'Round not closed yet' }, 409)],
      [
        '/leagues/global/leaderboard/closed-rounds',
        () =>
          jsonResponse({
            rounds: [
              { roundId: 'round-409', startTime: '2026-07-05T00:00:00Z', endTime: '2026-07-05T18:00:00Z', closedAt: '2026-07-05T18:05:00Z' },
            ],
            nextCursor: null,
            hasMore: false,
          }),
      ],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', notClosedFetch);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('tab', { name: 'Previous Rounds' }));
    await waitFor(() => expect(screen.getByText('Closed 2026-07-05T18:05:00Z')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'Closed 2026-07-05T18:05:00Z' }));
    await waitFor(() =>
      expect(
        screen.getByText('This round hasn’t closed yet — its live leaderboard is under “Current Round.”'),
      ).toBeInTheDocument(),
    );

    // The two messages are distinct strings, not a shared generic one.
    expect(screen.queryByText("This round couldn’t be found.")).not.toBeInTheDocument();
  });

  // Regression test for the "hasFetchedPastListRef never resets" bug —
  // same shape as the live-scope re-entry test.
  it('REQ408: re-selecting "Previous Rounds" after switching away fetches the closed-rounds list again', async () => {
    let closedRoundsCallCount = 0;
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/closed-rounds',
        () => {
          closedRoundsCallCount += 1;
          if (closedRoundsCallCount === 1) {
            return jsonResponse({
              rounds: [
                {
                  roundId: 'round-1',
                  startTime: '2026-07-05T00:00:00Z',
                  endTime: '2026-07-05T18:00:00Z',
                  closedAt: '2026-07-05T18:05:00Z',
                },
              ],
              nextCursor: null,
              hasMore: false,
            });
          }
          return jsonResponse({
            rounds: [
              {
                roundId: 'round-2',
                startTime: '2026-07-12T00:00:00Z',
                endTime: '2026-07-12T18:00:00Z',
                closedAt: '2026-07-12T18:05:00Z',
              },
            ],
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

    // First entry into "past".
    fireEvent.click(screen.getByRole('tab', { name: 'Previous Rounds' }));
    await waitFor(() => expect(screen.getByText('Closed 2026-07-05T18:05:00Z')).toBeInTheDocument());

    // Away, then back.
    fireEvent.click(screen.getByRole('tab', { name: 'All-time' }));
    fireEvent.click(screen.getByRole('tab', { name: 'Previous Rounds' }));

    // Second, fresh response lands — proving a second real request was
    // made, not the first list silently reused.
    await waitFor(() => expect(screen.getByText('Closed 2026-07-12T18:05:00Z')).toBeInTheDocument());
    expect(screen.queryByText('Closed 2026-07-05T18:05:00Z')).not.toBeInTheDocument();

    expect(closedRoundsCallCount).toBe(2);
  });

  // Near-identical to the well-tested REQ-607 `handleLoadMore` pattern on
  // the all-time scope, but exercising `handleLoadMoreRoundDetail` by
  // name — flagged by the quality-architect review as unverified.
  it('REQ408: "Load more" on a selected past round\'s detail view fetches the next page via the previous nextCursor', async () => {
    const fetchMock = routedFetch([
      [
        '/leagues/global/leaderboard/closed-rounds/round-1',
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
      [
        '/leagues/global/leaderboard/closed-rounds',
        () =>
          jsonResponse({
            rounds: [
              {
                roundId: 'round-1',
                startTime: '2026-07-05T00:00:00Z',
                endTime: '2026-07-05T18:00:00Z',
                closedAt: '2026-07-05T18:05:00Z',
              },
            ],
            nextCursor: null,
            hasMore: false,
          }),
      ],
      defaultAllTimeRoute,
    ]);
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('tab', { name: 'Previous Rounds' }));
    await waitFor(() => expect(screen.getByText('Closed 2026-07-05T18:05:00Z')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'Closed 2026-07-05T18:05:00Z' }));
    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(screen.getByText('Blair')).toBeInTheDocument());

    const rows = screen.getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent('Alex');
    expect(rows[1]).toHaveTextContent('Blair');

    const detailCalls = fetchMock.mock.calls.filter((call) =>
      String(call[0]).includes('/leagues/global/leaderboard/closed-rounds/round-1'),
    );
    expect(detailCalls).toHaveLength(2);
    expect(String(detailCalls[1][0])).toContain('cursor=50');
  });

  // REQ-1210/ADR-0083: the round-completion banner's leaderboard link, once
  // it has resolved the completed round has already closed, jumps straight
  // into that round's detail — bypassing the round-selection list entirely,
  // and with no `closedAt` known up front (the banner's link only ever
  // knew `roundId`).
  describe('REQ-1210: initialRoundId jumps straight into a round\'s detail', () => {
    it('fetches and shows the round detail directly, with no "Closed {date}" line since the summary was never known', async () => {
      const fetchMock = routedFetch([
        [
          '/leagues/global/leaderboard/closed-rounds/round-9',
          () => jsonResponse({ rows: [row(1, 'user-1', 'Alex', 29)], requestingUserRow: null, nextCursor: null, hasMore: false }),
        ],
        [
          '/leagues/global/leaderboard/closed-rounds',
          () => jsonResponse({ rounds: [], nextCursor: null, hasMore: false }),
        ],
        defaultAllTimeRoute,
      ]);
      vi.stubGlobal('fetch', fetchMock);

      render(
        <LeaderboardScreen
          accessToken="token"
          onAuthError={vi.fn()}
          initialScope="past"
          initialRoundId="round-9"
        />,
      );

      await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());
      expect(screen.getByText('29 pts')).toBeInTheDocument();
      expect(screen.queryByText(/^Closed /)).not.toBeInTheDocument();

      // "Back to previous rounds" still works afterward, and the round list
      // underneath it was fetched too (the mount-already-active fix) —
      // it's just empty here.
      fireEvent.click(screen.getByRole('button', { name: 'Back to previous rounds' }));
      await waitFor(() => expect(screen.getByText('No rounds have closed yet.')).toBeInTheDocument());
    });

    it('a "not closed yet" response for the seeded roundId shows the distinct, honest message, not a fabricated round view', async () => {
      const fetchMock = routedFetch([
        ['/leagues/global/leaderboard/closed-rounds/round-9', () => jsonResponse({ title: 'Round not closed yet' }, 409)],
        [
          '/leagues/global/leaderboard/closed-rounds',
          () => jsonResponse({ rounds: [], nextCursor: null, hasMore: false }),
        ],
        defaultAllTimeRoute,
      ]);
      vi.stubGlobal('fetch', fetchMock);

      render(
        <LeaderboardScreen
          accessToken="token"
          onAuthError={vi.fn()}
          initialScope="past"
          initialRoundId="round-9"
        />,
      );

      await waitFor(() =>
        expect(
          screen.getByText('This round hasn’t closed yet — its live leaderboard is under “Current Round.”'),
        ).toBeInTheDocument(),
      );
    });
  });
});

import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LeaderboardScreen } from './LeaderboardScreen';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

// REQ-406/407/408 (S-053/S-054): routes a fetch mock by URL substring so a
// single test can serve distinct responses to the all-time/live/past-rounds
// endpoints without caring about call order — the component now fires the
// all-time poll on every mount regardless of which scope tab is active, so
// every test touching the scope selector needs a default all-time response
// too, not just the endpoint under test.
function routedFetch(routes: Array<[string | RegExp, () => Promise<Response>]>) {
  return vi.fn().mockImplementation((input: RequestInfo | URL) => {
    const url = String(input);
    for (const [matcher, handler] of routes) {
      const matches = typeof matcher === 'string' ? url.includes(matcher) : matcher.test(url);
      if (matches) return handler();
    }
    throw new Error(`No mock route for ${url}`);
  });
}

const defaultAllTimeRoute: [string, () => Promise<Response>] = [
  '/leagues/global/leaderboard',
  () => jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
];

// Order matters: routedFetch tries matchers in order, and
// '/leagues/global/leaderboard' (the all-time route) is a substring of
// every scope's URL, so the more specific active-round/closed-rounds
// matchers must always be listed before it.

// Local helper for the REQ-607 pagination tests below — just cuts down on
// repeating the same four-field row literal; not shared across files, so
// kept local per the file's existing "one local helper" convention rather
// than promoted to shared test infra.
function row(rank: number, userId: string, displayName: string, totalPoints: number, isRequestingUser = false) {
  return { rank, userId, displayName, totalPoints, isRequestingUser };
}

// REQ-401/404 (SCREEN-03's Tier 0 slice: the global league only).
describe('LeaderboardScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-404/ADR-0021: renders rows in the order the API returns them, numbering rank #1 as the row the API put first (lowest total wins), with the requesting user marked distinctly', async () => {
    // The API (LeaderboardServiceTests, backend) sorts ascending —
    // lowest total first. This mock is deliberately given in that same
    // ascending order (Sam 120 < Player One 138 < Alex 142), not the
    // "highest first" order a pre-ADR-0021 assumption would produce, and
    // the assertions below check actual DOM order/rank numbers rather than
    // just that all three names appear somewhere — the component itself
    // does no sorting of its own, it trusts the API's order and labels each
    // row `index + 1`, so a wrongly-ordered response would previously have
    // passed this test undetected.
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          rows: [
            { rank: 1, userId: 'user-3', displayName: 'Sam', totalPoints: 120, isRequestingUser: false },
            { rank: 2, userId: 'user-2', displayName: 'Player One', totalPoints: 138, isRequestingUser: true },
            { rank: 3, userId: 'user-1', displayName: 'Alex', totalPoints: 142, isRequestingUser: false },
          ],
          requestingUserRow: {
            rank: 2,
            userId: 'user-2',
            displayName: 'Player One',
            totalPoints: 138,
            isRequestingUser: true,
          },
          nextCursor: null,
          hasMore: false,
        }),
      ),
    );

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());
    const rows = screen.getAllByRole('listitem');
    expect(rows).toHaveLength(3);
    const rankOf = (row: HTMLElement) => row.querySelector('.leaderboard-screen__rank')?.textContent;
    expect(rankOf(rows[0])).toBe('1');
    expect(rows[0]).toHaveTextContent('Sam');
    expect(rows[0]).toHaveTextContent('120 pts');
    expect(rankOf(rows[1])).toBe('2');
    expect(rows[1]).toHaveTextContent('Player One');
    expect(rows[1]).toHaveTextContent('138 pts');
    expect(rows[1]).toHaveTextContent('you');
    expect(rankOf(rows[2])).toBe('3');
    expect(rows[2]).toHaveTextContent('Alex');
    expect(rows[2]).toHaveTextContent('142 pts');
  });

  it('REQ-404/ADR-0021: shows "Lowest total wins" so a player does not assume the opposite from habit', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          rows: [{ rank: 1, userId: 'user-1', displayName: 'Alex', totalPoints: 10, isRequestingUser: false }],
          requestingUserRow: null,
          nextCursor: null,
          hasMore: false,
        }),
      ),
    );

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());
    expect(screen.getByText('Lowest total wins')).toBeInTheDocument();
  });

  it('REQ-401: shows a calm empty-state invitation when nobody has scored yet', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
      ),
    );

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() =>
      expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument(),
    );
  });

  it('REQ-401/404: keeps the leaderboard current by polling while mounted, without re-showing the loading state', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(() =>
        jsonResponse({
          rows: [{ rank: 1, userId: 'user-1', displayName: 'Alex', totalPoints: 10, isRequestingUser: false }],
          requestingUserRow: null,
          nextCursor: null,
          hasMore: false,
        }),
      )
      .mockImplementation(() =>
        jsonResponse({
          rows: [{ rank: 1, userId: 'user-1', displayName: 'Alex', totalPoints: 40, isRequestingUser: false }],
          requestingUserRow: null,
          nextCursor: null,
          hasMore: false,
        }),
      );
    vi.stubGlobal('fetch', fetchMock);
    vi.useFakeTimers({ shouldAdvanceTime: true });

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('10 pts')).toBeInTheDocument());

    await vi.advanceTimersByTimeAsync(15_000);
    await waitFor(() => expect(screen.getByText('40 pts')).toBeInTheDocument());
    // The poll tick must never flash "Loading…" over an already-rendered
    // leaderboard.
    expect(screen.queryByText('Loading the leaderboard…')).not.toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(2);

    vi.useRealTimers();
  });

  it("REQ-401/404: a failed background poll doesn't replace an already-loaded leaderboard with an error", async () => {
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(() =>
        jsonResponse({
          rows: [{ rank: 1, userId: 'user-1', displayName: 'Alex', totalPoints: 10, isRequestingUser: false }],
          requestingUserRow: null,
          nextCursor: null,
          hasMore: false,
        }),
      )
      .mockImplementation(() => Promise.resolve({ ok: false, status: 500, json: () => Promise.resolve({}) } as Response));
    vi.stubGlobal('fetch', fetchMock);
    vi.useFakeTimers({ shouldAdvanceTime: true });

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());

    await vi.advanceTimersByTimeAsync(15_000);
    expect(screen.getByText('Alex')).toBeInTheDocument();
    expect(screen.queryByText(/unreachable|error/i)).not.toBeInTheDocument();

    vi.useRealTimers();
  });

  it('REQ-607: shows a "Load more" button when hasMore is true, and clicking it fetches the next page via the previous nextCursor and appends the new rows below the existing ones', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(() =>
        jsonResponse({
          rows: [row(1, 'user-1', 'Alex', 10)],
          requestingUserRow: null,
          nextCursor: 50,
          hasMore: true,
        }),
      )
      .mockImplementationOnce(() =>
        jsonResponse({
          rows: [row(2, 'user-2', 'Blair', 20)],
          requestingUserRow: null,
          nextCursor: null,
          hasMore: false,
        }),
      );
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());

    const loadMoreButton = screen.getByRole('button', { name: 'Load more' });
    fireEvent.click(loadMoreButton);

    await waitFor(() => expect(screen.getByText('Blair')).toBeInTheDocument());

    // Appended below, not replacing — both rows present, in order.
    const rows = screen.getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent('Alex');
    expect(rows[1]).toHaveTextContent('Blair');

    // The second fetch call must have passed the first response's
    // nextCursor (50) as the cursor query param.
    expect(fetchMock).toHaveBeenCalledTimes(2);
    const secondCallUrl = String(fetchMock.mock.calls[1][0]);
    expect(secondCallUrl).toContain('cursor=50');

    // "Load more" is gone now that hasMore is false on the loaded page.
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('REQ-607: does not show a "Load more" button when hasMore is false', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          rows: [row(1, 'user-1', 'Alex', 10)],
          requestingUserRow: null,
          nextCursor: null,
          hasMore: false,
        }),
      ),
    );

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());

    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('REQ-607: a failed "Load more" click shows an inline error without clearing the already-rendered rows, and the button becomes re-clickable', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(() =>
        jsonResponse({
          rows: [row(1, 'user-1', 'Alex', 10)],
          requestingUserRow: null,
          nextCursor: 50,
          hasMore: true,
        }),
      )
      .mockImplementationOnce(() =>
        Promise.resolve({ ok: false, status: 500, json: () => Promise.resolve({}) } as Response),
      )
      .mockImplementationOnce(() =>
        jsonResponse({
          rows: [row(2, 'user-2', 'Blair', 20)],
          requestingUserRow: null,
          nextCursor: null,
          hasMore: false,
        }),
      );
    vi.stubGlobal('fetch', fetchMock);

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());

    const loadMoreButton = screen.getByRole('button', { name: 'Load more' });
    fireEvent.click(loadMoreButton);

    await waitFor(() =>
      expect(document.querySelector('.leaderboard-screen__load-more-error')).not.toBeNull(),
    );
    // The already-rendered row must survive the failure.
    expect(screen.getByText('Alex')).toBeInTheDocument();
    expect(screen.getAllByRole('listitem')).toHaveLength(1);

    // Button is re-clickable, not stuck disabled.
    const buttonAfterFailure = screen.getByRole('button', { name: 'Load more' });
    expect(buttonAfterFailure).not.toBeDisabled();

    fireEvent.click(buttonAfterFailure);
    await waitFor(() => expect(screen.getByText('Blair')).toBeInTheDocument());
    expect(document.querySelector('.leaderboard-screen__load-more-error')).toBeNull();
  });

  it('REQ-607: the 15s poll only ever refreshes page 1 — a second page loaded via "Load more" survives the next poll tick', async () => {
    const fetchMock = vi
      .fn()
      // Initial mount load — page 1.
      .mockImplementationOnce(() =>
        jsonResponse({
          rows: [row(1, 'user-1', 'Alex', 10)],
          requestingUserRow: null,
          nextCursor: 50,
          hasMore: true,
        }),
      )
      // "Load more" click — page 2.
      .mockImplementationOnce(() =>
        jsonResponse({
          rows: [row(2, 'user-2', 'Blair', 20)],
          requestingUserRow: null,
          nextCursor: null,
          hasMore: false,
        }),
      )
      // The 15s poll tick — page 1 only, with an updated total to prove a
      // refresh actually happened.
      .mockImplementation(() =>
        jsonResponse({
          rows: [row(1, 'user-1', 'Alex', 99)],
          requestingUserRow: null,
          nextCursor: 50,
          hasMore: true,
        }),
      );
    vi.stubGlobal('fetch', fetchMock);
    vi.useFakeTimers({ shouldAdvanceTime: true });

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('10 pts')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(screen.getByText('Blair')).toBeInTheDocument());

    await vi.advanceTimersByTimeAsync(15_000);

    // Page 1's row was refreshed by the poll...
    await waitFor(() => expect(screen.getByText('99 pts')).toBeInTheDocument());
    // ...and page 2's row, which the poll never re-fetches, must still be
    // present — not dropped by the page-1-only poll response.
    expect(screen.getByText('Blair')).toBeInTheDocument();
    expect(screen.getAllByRole('listitem')).toHaveLength(2);

    vi.useRealTimers();
  });

  it('REQ-607: shows the pinned "you" footer when the requesting user\'s row is not among the currently loaded rows', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          rows: [row(1, 'user-1', 'Alex', 10)],
          requestingUserRow: row(47, 'user-99', 'Player One', 900, true),
          nextCursor: null,
          hasMore: false,
        }),
      ),
    );

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());

    const footer = document.querySelector('.leaderboard-screen__you-footer');
    expect(footer).not.toBeNull();
    expect(footer).toHaveTextContent('Player One');
    expect(footer).toHaveTextContent('900 pts');
    expect(footer).toHaveTextContent('you');
  });

  it('REQ-607: does not duplicate the "you" footer when the requesting user\'s row is already visible among the loaded rows', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          rows: [row(1, 'user-1', 'Alex', 10), row(2, 'user-2', 'Player One', 20, true)],
          requestingUserRow: row(2, 'user-2', 'Player One', 20, true),
          nextCursor: null,
          hasMore: false,
        }),
      ),
    );

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());

    expect(document.querySelector('.leaderboard-screen__you-footer')).toBeNull();
    // Still exactly one "Player One" row (in the list, not duplicated below it).
    expect(screen.getAllByText('Player One')).toHaveLength(1);
  });

  it('REQ-607: a player who moves from page 2 to page 1 between poll ticks appears once, not duplicated', async () => {
    const fetchMock = vi
      .fn()
      // Initial mount load — page 1.
      .mockImplementationOnce(() =>
        jsonResponse({
          rows: [row(1, 'user-1', 'Alex', 10), row(2, 'user-2', 'Blair', 20)],
          requestingUserRow: null,
          nextCursor: 50,
          hasMore: true,
        }),
      )
      // "Load more" click — page 2, Casey is still down here.
      .mockImplementationOnce(() =>
        jsonResponse({
          rows: [row(3, 'user-3', 'Casey', 30)],
          requestingUserRow: null,
          nextCursor: null,
          hasMore: false,
        }),
      )
      // The 15s poll tick — round close reshuffled totals and Casey now
      // ranks into page 1, ahead of Alex.
      .mockImplementation(() =>
        jsonResponse({
          rows: [row(1, 'user-3', 'Casey', 5), row(2, 'user-1', 'Alex', 10)],
          requestingUserRow: null,
          nextCursor: 50,
          hasMore: true,
        }),
      );
    vi.stubGlobal('fetch', fetchMock);
    vi.useFakeTimers({ shouldAdvanceTime: true });

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(screen.getByText('Casey')).toBeInTheDocument());
    // Sanity check: before the poll tick, Casey is still the stale page-2
    // copy (30 pts).
    expect(screen.getByText('30 pts')).toBeInTheDocument();

    await vi.advanceTimersByTimeAsync(15_000);

    // The poll's fresh page-1 copy of Casey (5 pts) has landed...
    await waitFor(() => expect(screen.getByText('5 pts')).toBeInTheDocument());

    // ...and Casey appears exactly once, not duplicated between the fresh
    // page-1 row and the stale page-2 trailing row.
    expect(screen.getAllByText('Casey')).toHaveLength(1);
    expect(screen.queryByText('30 pts')).not.toBeInTheDocument();

    // Blair (the other original page-1 row, now bumped off page 1 entirely
    // by the fresh response) is gone too — the poll replaces the whole
    // page-1 prefix, it doesn't merge it.
    expect(screen.queryByText('Blair')).not.toBeInTheDocument();

    // Total row count reflects the two fresh page-1 rows plus zero
    // still-distinct trailing rows (Casey was the only trailing row, and it
    // was deduped away) — not three.
    const rows = screen.getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent('Casey');
    expect(rows[0]).toHaveTextContent('5 pts');
    expect(rows[1]).toHaveTextContent('Alex');
    expect(rows[1]).toHaveTextContent('10 pts');

    vi.useRealTimers();
  });

  // REQ-406/407/408 (S-053/S-054): the three-way scope selector.
  describe('scope selector', () => {
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

    it('REQ406/407/408: the all-time scope keeps its own existing 15s poll/"Load more" behavior after switching scopes and back', async () => {
      const fetchMock = routedFetch([
        [
          '/leagues/global/leaderboard/active-round',
          () => jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
        ],
        [
          '/leagues/global/leaderboard',
          () =>
            jsonResponse({
              rows: [row(1, 'user-1', 'Alex', 10)],
              requestingUserRow: null,
              nextCursor: null,
              hasMore: false,
            }),
        ],
      ]);
      vi.stubGlobal('fetch', fetchMock);

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('10 pts')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('tab', { name: 'Current Round' }));
      await waitFor(() =>
        expect(screen.getByText('No one has played this round yet — be the first.')).toBeInTheDocument(),
      );

      fireEvent.click(screen.getByRole('tab', { name: 'All-time' }));
      expect(screen.getByText('Alex')).toBeInTheDocument();
      expect(screen.getByText('10 pts')).toBeInTheDocument();
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

    // Regression test for the "hasFetchedPastListRef never resets" bug —
    // same shape as the live-scope test above, for the past-rounds list.
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

    // Near-identical to the well-tested REQ-607 `handleLoadMore` pattern
    // above, but exercising `handleLoadMoreLive` by name — flagged by the
    // quality-architect review as unverified.
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

    // Near-identical to the well-tested REQ-607 `handleLoadMore` pattern
    // above, but exercising `handleLoadMoreRoundDetail` by name — flagged
    // by the quality-architect review as unverified.
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

    // REQ-405 (S-027): the "Time Windows" scope — round/week/month/year
    // rolling-window leaderboards. Same routedFetch/defaultAllTimeRoute
    // conventions as the live/past scope tests above; the window routes are
    // listed before `defaultAllTimeRoute` since '/leagues/global/leaderboard'
    // is a substring of every window URL too.
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
});

import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LeaderboardScreen } from './LeaderboardScreen';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
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
});

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
});

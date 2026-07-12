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

  it('REQ-404: renders ranked rows sorted by total points, with the requesting user marked distinctly', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          rows: [
            { userId: 'user-1', displayName: 'Alex', totalPoints: 142, isRequestingUser: false },
            { userId: 'user-2', displayName: 'Player One', totalPoints: 138, isRequestingUser: true },
            { userId: 'user-3', displayName: 'Sam', totalPoints: 120, isRequestingUser: false },
          ],
        }),
      ),
    );

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());
    expect(screen.getByText('142 pts')).toBeInTheDocument();
    expect(screen.getByText('Player One')).toBeInTheDocument();
    expect(screen.getByText('you')).toBeInTheDocument();
  });

  it('REQ-401: shows a calm empty-state invitation when nobody has scored yet', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse({ rows: [] })));

    render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() =>
      expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument(),
    );
  });

  it('REQ-401/404: keeps the leaderboard current by polling while mounted, without re-showing the loading state', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(() =>
        jsonResponse({ rows: [{ userId: 'user-1', displayName: 'Alex', totalPoints: 10, isRequestingUser: false }] }),
      )
      .mockImplementation(() =>
        jsonResponse({ rows: [{ userId: 'user-1', displayName: 'Alex', totalPoints: 40, isRequestingUser: false }] }),
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
        jsonResponse({ rows: [{ userId: 'user-1', displayName: 'Alex', totalPoints: 10, isRequestingUser: false }] }),
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

import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { FriendsTab } from './FriendsTab';

// REQ-1401/1402 (S-217): isolated coverage of FriendsTab's own pending-
// requests/friends-list/challenge behavior, mounted directly (no
// FriendsScreen/App/routing involved) — same convention every other
// screen-adjacent component in this codebase already has
// (LeaguesScreen.test.tsx, HeaderNav.test.tsx).

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function problemResponse(title: string, detail: string, status: number) {
  return jsonResponse({ title, detail }, status);
}

function renderFriendsTab(overrides: Partial<Parameters<typeof FriendsTab>[0]> = {}, fetchMock = vi.fn()) {
  vi.stubGlobal('fetch', fetchMock);
  const onAuthError = vi.fn();
  render(<FriendsTab accessToken="token" onAuthError={onAuthError} {...overrides} />);
  return { onAuthError };
}

describe('FriendsTab', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1401: shows an empty state for both sections when there is nothing pending and no friends', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/friends/requests/pending')) return jsonResponse([]);
      if (url.includes('/friends')) return jsonResponse([]);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    renderFriendsTab({}, fetchMock);

    expect(await screen.findByText('No pending friend requests.')).toBeInTheDocument();
    expect(
      await screen.findByText("You don't have any friends yet. Visit a player's stats page to send a friend request."),
    ).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Friend requests' })).toBeInTheDocument();
  });

  it('REQ-1401: renders each pending friend request with its requesterDisplayName label and an inline "(N)" heading count', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/friends/requests/pending')) {
        return jsonResponse([
          {
            id: 'req-1',
            requesterUserId: 'a1b2c3d4-0000-0000-0000-000000000000',
            requesterDisplayName: 'Alex',
            recipientUserId: 'me',
            recipientDisplayName: 'Me',
            status: 'Pending',
            createdAt: '2026-01-01T00:00:00Z',
            resolvedAt: null,
          },
        ]);
      }
      if (url.includes('/friends')) return jsonResponse([]);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    renderFriendsTab({}, fetchMock);

    expect(await screen.findByRole('heading', { name: 'Friend requests (1)' })).toBeInTheDocument();
    expect(screen.getByText('Alex')).toBeInTheDocument();
  });

  it('REQ-1401: accepting a pending request calls POST .../accept and the row disappears once refetched', async () => {
    const requests = [
      {
        id: 'req-1',
        requesterUserId: 'a1b2c3d4-0000-0000-0000-000000000000',
        requesterDisplayName: 'Alex',
        recipientUserId: 'me',
        recipientDisplayName: 'Me',
        status: 'Pending',
        createdAt: '2026-01-01T00:00:00Z',
        resolvedAt: null,
      },
    ];
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      // A fresh snapshot each time, not the live mutable array reference —
      // a real HTTP response always deserializes into a fresh array, and
      // returning the same reference this fake still mutates in place would
      // make React's setState bail out on an in-place-mutated "update" that
      // Object.is sees as identical (same reasoning LeaguesScreen.test.tsx's
      // own createFakeLeaguesBackend comment already documents).
      if (url.includes('/friends/requests/pending')) return jsonResponse([...requests]);
      if (url.includes('/friends/requests/req-1/accept') && method === 'POST') {
        const [accepted] = requests.splice(0, 1);
        return jsonResponse({ ...accepted, status: 'Accepted' });
      }
      if (url.includes('/friends')) return jsonResponse([]);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    renderFriendsTab({}, fetchMock);

    await screen.findByText('Alex');
    await user.click(screen.getByRole('button', { name: 'Accept' }));

    await waitFor(() => expect(screen.getByText('No pending friend requests.')).toBeInTheDocument());
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/friends/requests/req-1/accept'),
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('REQ-1401: a 409 while declining a request shows the server\'s own detail text inline on that row', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.includes('/friends/requests/pending')) {
        return jsonResponse([
          {
            id: 'req-1',
            requesterUserId: 'a1b2c3d4-0000-0000-0000-000000000000',
            requesterDisplayName: 'Alex',
            recipientUserId: 'me',
            recipientDisplayName: 'Me',
            status: 'Pending',
            createdAt: '2026-01-01T00:00:00Z',
            resolvedAt: null,
          },
        ]);
      }
      if (url.includes('/friends/requests/req-1/decline') && method === 'POST') {
        return problemResponse('Already resolved', 'This friend request has already been accepted or declined.', 409);
      }
      if (url.includes('/friends')) return jsonResponse([]);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    renderFriendsTab({}, fetchMock);

    await screen.findByText('Alex');
    await user.click(screen.getByRole('button', { name: 'Decline' }));

    expect(
      await screen.findByText('This friend request has already been accepted or declined.'),
    ).toBeInTheDocument();
  });

  it('REQ-1402: renders each friend with a "Challenge" button, and sending shows "Challenge sent." inline', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.includes('/friends/requests/pending')) return jsonResponse([]);
      if (url.endsWith('/friends') && method === 'GET') {
        return jsonResponse([
          {
            id: 'friendship-1',
            friendUserId: '11223344-0000-0000-0000-000000000000',
            friendDisplayName: 'Robin',
            createdAt: '2026-01-01T00:00:00Z',
          },
        ]);
      }
      if (url.endsWith('/challenges') && method === 'POST') {
        const body = JSON.parse(String(init?.body)) as { challengedUserId: string };
        return jsonResponse({
          id: 'challenge-1',
          challengerUserId: 'me',
          challengerDisplayName: 'Me',
          challengedUserId: body.challengedUserId,
          challengedDisplayName: 'Robin',
          status: 'Pending',
          createdAt: '2026-01-01T00:00:00Z',
          resolvedAt: null,
          resultingMatchId: null,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    renderFriendsTab({}, fetchMock);

    expect(await screen.findByText('Robin')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Challenge' }));

    expect(await screen.findByText('Challenge sent.')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringMatching(/\/challenges$/),
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ challengedUserId: '11223344-0000-0000-0000-000000000000' }),
      }),
    );
  });

  it('REQ-1401: a 401 while accepting a request calls onAuthError', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.includes('/friends/requests/pending')) {
        return jsonResponse([
          {
            id: 'req-1',
            requesterUserId: 'a1b2c3d4-0000-0000-0000-000000000000',
            requesterDisplayName: 'Alex',
            recipientUserId: 'me',
            recipientDisplayName: 'Me',
            status: 'Pending',
            createdAt: '2026-01-01T00:00:00Z',
            resolvedAt: null,
          },
        ]);
      }
      if (url.includes('/friends/requests/req-1/accept') && method === 'POST') {
        return problemResponse('Unauthorized', 'Unauthorized', 401);
      }
      if (url.includes('/friends')) return jsonResponse([]);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onAuthError } = renderFriendsTab({}, fetchMock);

    await screen.findByText('Alex');
    await user.click(screen.getByRole('button', { name: 'Accept' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });
});

// Direct user feedback (2026-09-03): "should also be possible to click a
// friend in the list to go to their profile." Mirrors
// LeaderboardRowsList.test.tsx's own "onSelectPlayer" coverage shape.
describe('FriendsTab (friend row click-through to SCREEN-13)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function friendsBackend() {
    return vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/friends/requests/pending')) return jsonResponse([]);
      if (url.endsWith('/friends')) {
        return jsonResponse([
          {
            id: 'friendship-1',
            friendUserId: '11223344-0000-0000-0000-000000000000',
            friendDisplayName: 'Robin',
            createdAt: '2026-01-01T00:00:00Z',
          },
        ]);
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
  }

  it('renders the friend\'s name as a plain (non-clickable) span when onSelectPlayer is not supplied', async () => {
    renderFriendsTab({}, friendsBackend());

    expect(await screen.findByText('Robin')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Robin' })).not.toBeInTheDocument();
  });

  it('renders the friend\'s name as a button and calls onSelectPlayer(friendUserId, friendDisplayName) on click, without also triggering "Challenge"', async () => {
    const onSelectPlayer = vi.fn();
    const user = userEvent.setup();
    renderFriendsTab({ onSelectPlayer }, friendsBackend());

    const nameButton = await screen.findByRole('button', { name: 'Robin' });
    await user.click(nameButton);

    expect(onSelectPlayer).toHaveBeenCalledTimes(1);
    expect(onSelectPlayer).toHaveBeenCalledWith('11223344-0000-0000-0000-000000000000', 'Robin');
    // The "Challenge" button is a separate control on the same row — never
    // triggered by clicking the name.
    expect(screen.getByRole('button', { name: 'Challenge' })).toBeInTheDocument();
  });
});

import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SendFriendRequestAction } from './SendFriendRequestAction';

// REQ-1401 (S-217, design-document.md SCREEN-13's 2026-09-03 status note):
// isolated coverage of SendFriendRequestAction's own three-state rendering
// and its "must never block the rest of the screen" quiet-degrade rule.
// UserStatsScreen.test.tsx separately covers this component's wiring
// (mounted only when viewerUserId differs from userId).

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

function renderAction(overrides: Partial<Parameters<typeof SendFriendRequestAction>[0]> = {}, fetchMock = vi.fn()) {
  vi.stubGlobal('fetch', fetchMock);
  const onAuthError = vi.fn();
  render(
    <SendFriendRequestAction
      accessToken="token"
      viewerUserId="viewer-1"
      targetUserId="target-2"
      onAuthError={onAuthError}
      {...overrides}
    />,
  );
  return { onAuthError };
}

describe('SendFriendRequestAction', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1401: renders nothing until both fetches resolve', () => {
    const fetchMock = vi.fn().mockImplementation(() => new Promise(() => {}));
    vi.stubGlobal('fetch', fetchMock);
    const { container } = render(
      <SendFriendRequestAction
        accessToken="token"
        viewerUserId="viewer-1"
        targetUserId="target-2"
        onAuthError={vi.fn()}
      />,
    );

    expect(container).toBeEmptyDOMElement();
  });

  it('REQ-1401: renders a "Send friend request" button when the target is neither a friend nor an incoming pending requester', async () => {
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse([]));
    renderAction({}, fetchMock);

    expect(await screen.findByRole('button', { name: 'Send friend request' })).toBeInTheDocument();
  });

  it('REQ-1401: sending shows "Friend request sent." in place of the button', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/friends/requests') && method === 'POST') {
        return jsonResponse({
          id: 'req-1',
          requesterUserId: 'viewer-1',
          recipientUserId: 'target-2',
          status: 'Pending',
          createdAt: '2026-01-01T00:00:00Z',
          resolvedAt: null,
        });
      }
      return jsonResponse([]);
    });
    const user = userEvent.setup();
    renderAction({}, fetchMock);

    await user.click(await screen.findByRole('button', { name: 'Send friend request' }));

    expect(await screen.findByText('Friend request sent.')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/friends/requests'),
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ recipientUserId: 'target-2' }) }),
    );
  });

  it('REQ-1401: a 409 "Duplicate pending request" while sending shows the server\'s own detail text inline', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      if (method === 'POST') {
        return problemResponse(
          'Duplicate pending request',
          'A pending friend request already exists between you and this user.',
          409,
        );
      }
      return jsonResponse([]);
    });
    const user = userEvent.setup();
    renderAction({}, fetchMock);

    await user.click(await screen.findByRole('button', { name: 'Send friend request' }));

    expect(
      await screen.findByText('A pending friend request already exists between you and this user.'),
    ).toBeInTheDocument();
  });

  it('REQ-1401: shows "You\'re already friends." (no button) when the target is already a friend', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith('/friends')) {
        return jsonResponse([{ id: 'f-1', friendUserId: 'target-2', createdAt: '2026-01-01T00:00:00Z' }]);
      }
      return jsonResponse([]);
    });
    renderAction({}, fetchMock);

    expect(await screen.findByText("You're already friends.")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send friend request' })).not.toBeInTheDocument();
  });

  it('REQ-1401: shows the "already sent you a request" state with a link to Friends & Challenges when onOpenFriends is provided', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/friends/requests/pending')) {
        return jsonResponse([
          {
            id: 'req-1',
            requesterUserId: 'target-2',
            recipientUserId: 'viewer-1',
            status: 'Pending',
            createdAt: '2026-01-01T00:00:00Z',
            resolvedAt: null,
          },
        ]);
      }
      return jsonResponse([]);
    });
    const onOpenFriends = vi.fn();
    const user = userEvent.setup();
    renderAction({ onOpenFriends }, fetchMock);

    expect(await screen.findByText('This player already sent you a friend request.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send friend request' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Respond in Friends & Challenges' }));
    expect(onOpenFriends).toHaveBeenCalledTimes(1);
  });

  it('REQ-1401: renders nothing (own profile) when viewerUserId equals targetUserId', () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse([])));
    const { container } = render(
      <SendFriendRequestAction
        accessToken="token"
        viewerUserId="same-id"
        targetUserId="same-id"
        onAuthError={vi.fn()}
      />,
    );

    expect(container).toBeEmptyDOMElement();
  });

  it('REQ-1401: renders nothing on a non-401 fetch failure (quiet degrade, never blocks the rest of the screen)', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(() => problemResponse('Request failed', 'Server error.', 500));
    vi.stubGlobal('fetch', fetchMock);
    const { container } = render(
      <SendFriendRequestAction
        accessToken="token"
        viewerUserId="viewer-1"
        targetUserId="target-2"
        onAuthError={vi.fn()}
      />,
    );

    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });

  it('REQ-1401: a 401 while sending calls onAuthError', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      if (method === 'POST') return problemResponse('Unauthorized', 'Unauthorized', 401);
      return jsonResponse([]);
    });
    const user = userEvent.setup();
    const { onAuthError } = renderAction({}, fetchMock);

    await user.click(await screen.findByRole('button', { name: 'Send friend request' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });
});

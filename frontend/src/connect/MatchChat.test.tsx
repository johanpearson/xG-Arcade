import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MatchChat } from './MatchChat';

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

function renderChat(overrides: Partial<Parameters<typeof MatchChat>[0]> = {}, fetchMock = vi.fn()) {
  vi.stubGlobal('fetch', fetchMock);
  const onAuthError = vi.fn();
  render(<MatchChat matchId="match-1" accessToken="token" viewerUserId="me-1" onAuthError={onAuthError} {...overrides} />);
  return { onAuthError };
}

const message = {
  id: 'msg-1',
  senderUserId: 'opponent-1',
  messageText: 'gg',
  sentAt: '2026-09-03T00:00:00Z',
};

// REQ-1410 (design-document.md SCREEN-16's "In-match chat") — visible
// regardless of match phase, polled rather than live-pushed.
describe('MatchChat', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('REQ-1410: shows an empty state when there are no messages yet', async () => {
    renderChat({}, vi.fn().mockImplementation(() => jsonResponse([])));

    expect(await screen.findByText('No messages yet — say hello.')).toBeInTheDocument();
  });

  it('REQ-1410: renders each message, labeling the viewer\'s own messages "You" and the opponent by shortUserId', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse([message, { ...message, id: 'msg-2', senderUserId: 'me-1', messageText: 'gg to you too' }]),
    );
    renderChat({}, fetchMock);

    expect(await screen.findByText('gg')).toBeInTheDocument();
    expect(screen.getByText('Player OPPONENT')).toBeInTheDocument();
    expect(screen.getByText('gg to you too')).toBeInTheDocument();
    expect(screen.getByText('You')).toBeInTheDocument();
  });

  it('REQ-1410: a null senderUserId (REQ-710 anonymization) renders "Deleted account"', async () => {
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse([{ ...message, senderUserId: null }]));
    renderChat({}, fetchMock);

    expect(await screen.findByText('Deleted account')).toBeInTheDocument();
  });

  it('REQ-1410: sending a message posts it and refetches the list', async () => {
    let sent = false;
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/matches/match-1/chat-messages') && method === 'GET') {
        return jsonResponse(sent ? [message] : []);
      }
      if (url.endsWith('/matches/match-1/chat-messages') && method === 'POST') {
        sent = true;
        expect(JSON.parse(init!.body as string)).toEqual({ messageText: 'gg' });
        return jsonResponse(message);
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    renderChat({}, fetchMock);

    await screen.findByText('No messages yet — say hello.');
    await user.type(screen.getByLabelText('Chat message'), 'gg');
    await user.click(screen.getByRole('button', { name: 'Send message' }));

    expect(await screen.findByText('gg')).toBeInTheDocument();
    expect((screen.getByLabelText('Chat message') as HTMLTextAreaElement).value).toBe('');
  });

  it('REQ-1410: a 400 (e.g. too-long message) shows the server\'s own detail text', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/matches/match-1/chat-messages') && method === 'GET') return jsonResponse([]);
      if (url.endsWith('/matches/match-1/chat-messages') && method === 'POST') {
        return problemResponse('Message is too long', 'messageText must be 1000 characters or fewer.', 400);
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    renderChat({}, fetchMock);

    await screen.findByText('No messages yet — say hello.');
    await user.type(screen.getByLabelText('Chat message'), 'too long');
    await user.click(screen.getByRole('button', { name: 'Send message' }));

    expect(await screen.findByText('messageText must be 1000 characters or fewer.')).toBeInTheDocument();
  });

  it('REQ-1410: a 401 while sending calls onAuthError', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/matches/match-1/chat-messages') && method === 'GET') return jsonResponse([]);
      if (url.endsWith('/matches/match-1/chat-messages') && method === 'POST') {
        return problemResponse('Unauthorized', 'Unauthorized', 401);
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onAuthError } = renderChat({}, fetchMock);

    await screen.findByText('No messages yet — say hello.');
    await user.type(screen.getByLabelText('Chat message'), 'gg');
    await user.click(screen.getByRole('button', { name: 'Send message' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-1410: polls for new messages on a 15s interval', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse([]));
    renderChat({}, fetchMock);

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    await vi.advanceTimersByTimeAsync(15_000);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
  });
});

import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MatchmakingTab } from './MatchmakingTab';

// REQ-1403 (S-217): isolated coverage of MatchmakingTab's own one-shot
// opt-in action and its deliberately session-local-only status display.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function renderMatchmakingTab(overrides: Partial<Parameters<typeof MatchmakingTab>[0]> = {}, fetchMock = vi.fn()) {
  vi.stubGlobal('fetch', fetchMock);
  const onAuthError = vi.fn();
  const onViewMatches = vi.fn();
  render(<MatchmakingTab accessToken="token" onAuthError={onAuthError} onViewMatches={onViewMatches} {...overrides} />);
  return { onAuthError, onViewMatches };
}

describe('MatchmakingTab', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1403: shows an "Opt in" button before any action is taken', () => {
    renderMatchmakingTab();

    expect(screen.getByRole('button', { name: 'Opt in' })).toBeInTheDocument();
  });

  it('REQ-1403: opting in calls POST /matchmaking/opt-in and replaces the button with a status line plus the session-local-only disclosure', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/matchmaking/opt-in') && method === 'POST') {
        return jsonResponse({
          id: 'optin-1',
          optedInAt: '2026-09-03T00:00:00Z',
          expiresAt: '2026-09-03T12:00:00Z',
          status: 'Waiting',
          resultingMatchId: null,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onViewMatches } = renderMatchmakingTab({}, fetchMock);

    await user.click(screen.getByRole('button', { name: 'Opt in' }));

    expect(await screen.findByText(/You're in the matchmaking pool until/)).toBeInTheDocument();
    expect(screen.getByText("This won't be visible after you leave this screen.")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Opt in' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'View your matches' }));
    expect(onViewMatches).toHaveBeenCalledTimes(1);
  });

  it('REQ-1403: a non-401 failure shows the server\'s own error text and leaves the "Opt in" button in place', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(() => jsonResponse({ title: 'Request failed', detail: 'Server error.' }, 500));
    const user = userEvent.setup();
    renderMatchmakingTab({}, fetchMock);

    await user.click(screen.getByRole('button', { name: 'Opt in' }));

    expect(await screen.findByText('Server error.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Opt in' })).toBeInTheDocument();
  });

  it('REQ-1403: a 401 calls onAuthError', async () => {
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized' }, 401));
    const user = userEvent.setup();
    const { onAuthError } = renderMatchmakingTab({}, fetchMock);

    await user.click(screen.getByRole('button', { name: 'Opt in' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });
});

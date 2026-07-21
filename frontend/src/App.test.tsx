import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from './App';

// These must stay in sync with App.tsx's own (unexported) constants — there
// is no shared module to import them from, same trade-off every other
// "localStorage key" test in a codebase like this accepts.
const ACCESS_TOKEN_STORAGE_KEY = 'xg-arcade-access-token';
const REFRESH_TOKEN_STORAGE_KEY = 'xg-arcade-refresh-token';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function authHeader(init?: RequestInit): string | undefined {
  const headers = init?.headers as Record<string, string> | undefined;
  return headers?.Authorization;
}

const meResponse = {
  id: 'user-1',
  email: 'player@example.com',
  displayName: 'Player One',
  emailConfirmed: true,
  isAdmin: false,
};

// REQ-715 (ADR-0033): App.tsx is the only place the refresh-token flow
// lives, so this is its dedicated suite — every other screen's own
// test file (GridScreen.test.tsx, LeaderboardScreen.test.tsx, etc.) mounts
// its component directly and is unaffected by any of this.
describe('App (REQ-715: persistent login via refresh token)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  it('a stale stored access token that 401s, with a valid stored refresh token, silently recovers the session instead of showing the login screen', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'expired-token');
    window.localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'refresh-abc');

    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/refresh')) {
        return jsonResponse({ accessToken: 'new-token', refreshToken: 'new-refresh' });
      }
      if (url.includes('/auth/me')) {
        if (authHeader(init) === 'Bearer new-token') return jsonResponse(meResponse);
        return jsonResponse({ title: 'Unauthorized' }, 401);
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);

    // Never shown a login prompt at any point (REQ-715: "the person is not
    // shown a login prompt or otherwise interrupted").
    expect(screen.queryByRole('tab', { name: 'Log in' })).not.toBeInTheDocument();
    expect(screen.getByText('Choose a game')).toBeInTheDocument();

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/auth/refresh'),
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({ refreshToken: 'refresh-abc' }),
        }),
      ),
    );

    // The new tokens replace the stale ones in storage.
    await waitFor(() => expect(window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBe('new-token'));
    expect(window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBe('new-refresh');

    // The retried GET /auth/me (with the new token) eventually succeeds too
    // — confirmed indirectly via the login prompt never appearing and the
    // app staying on game-select throughout.
    expect(screen.queryByRole('tab', { name: 'Log in' })).not.toBeInTheDocument();
    expect(screen.getByText('Choose a game')).toBeInTheDocument();
  });

  it('an access token missing entirely, but a valid stored refresh token, restores the session on load without the person logging in again', async () => {
    window.localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'refresh-abc');

    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/refresh')) return jsonResponse({ accessToken: 'new-token', refreshToken: null });
      if (url.includes('/auth/me')) {
        if (authHeader(init) === 'Bearer new-token') return jsonResponse(meResponse);
        return jsonResponse({ title: 'Unauthorized' }, 401);
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);

    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());
    expect(screen.queryByRole('tab', { name: 'Log in' })).not.toBeInTheDocument();
    // No new refresh token was returned, so the existing one stays in
    // storage rather than being treated as dead.
    expect(window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBe('refresh-abc');
  });

  it('an invalid/expired/revoked stored refresh token fails the silent refresh and falls through to the existing login screen — clearing both stored tokens, never retrying indefinitely', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'expired-token');
    window.localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'dead-refresh');

    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/refresh')) {
        return jsonResponse(
          { title: 'Refresh failed', detail: 'Refresh token is invalid, expired, or revoked.' },
          401,
        );
      }
      if (url.includes('/auth/me')) return jsonResponse({ title: 'Unauthorized' }, 401);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);

    expect(await screen.findByRole('tab', { name: 'Log in' })).toBeInTheDocument();
    expect(window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBeNull();
    expect(window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBeNull();

    // Never an infinite retry loop: exactly one refresh attempt for this
    // single failed session-restore.
    const refreshCalls = fetchMock.mock.calls.filter(([input]) => String(input).includes('/auth/refresh'));
    expect(refreshCalls).toHaveLength(1);
  });

  it('logging in stores both the access token and the refresh token', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/login')) return jsonResponse({ accessToken: 'token-abc', refreshToken: 'refresh-abc' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.click(screen.getByRole('button', { name: 'Log in' }));

    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());
    expect(window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBe('token-abc');
    expect(window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBe('refresh-abc');
  });

  it('logging out clears both the access token and the refresh token, not only the access token', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    window.localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'refresh-abc');

    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Log out' }));

    expect(await screen.findByRole('tab', { name: 'Log in' })).toBeInTheDocument();
    expect(window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBeNull();
    expect(window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBeNull();
  });

  it('deleting the account (Settings → Delete my account permanently) clears both the access token and the refresh token', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    window.localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, 'refresh-abc');

    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      if (url.includes('/auth/account') && init?.method === 'DELETE') {
        return Promise.resolve({ ok: true, status: 204, json: () => Promise.resolve(null) } as Response);
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Settings' }));
    await user.type(screen.getByLabelText('Current password'), 'correct-password');
    await user.click(screen.getByRole('button', { name: 'Delete my account permanently' }));

    expect(await screen.findByRole('tab', { name: 'Log in' })).toBeInTheDocument();
    expect(window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBeNull();
    expect(window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBeNull();
  });
});

// REQ-717/ADR-0036: the guest banner — a header-level nudge rendered only
// while the account is a guest (App.tsx's own `isGuest` derived from
// `currentUser.isGuest`, MeResponse's first-class field). Mounted here
// (not SettingsScreen.test.tsx) since the banner itself, and the App-level
// state flow that makes it disappear after a claim, are this file's own
// responsibility — SettingsScreen.test.tsx already covers the claim form's
// own validation/submission behavior in isolation.
describe('App (REQ-717: guest banner)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  const guestMeResponse = {
    id: 'guest-1',
    email: null,
    displayName: 'Guest8317',
    emailConfirmed: false,
    isAdmin: false,
    isGuest: true,
  };

  it('REQ-717: shows the guest banner and "Save your progress" nudge once signed in as a guest', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/guest')) return jsonResponse({ accessToken: 'guest-token', refreshToken: 'guest-refresh' });
      if (url.includes('/auth/me')) return jsonResponse(guestMeResponse);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await user.click(screen.getByRole('button', { name: 'Play as guest' }));

    expect(await screen.findByText('Playing as Guest8317.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save your progress' })).toBeInTheDocument();
  });

  it('REQ-717: renders no guest banner at all for a normal (non-guest) account', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/login')) return jsonResponse({ accessToken: 'token-abc', refreshToken: 'refresh-abc' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.click(screen.getByRole('button', { name: 'Log in' }));

    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());
    expect(screen.queryByText(/Playing as/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save your progress' })).not.toBeInTheDocument();
  });

  it('REQ-717: the guest banner disappears immediately after a successful claim, without a page reload', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/guest')) return jsonResponse({ accessToken: 'guest-token', refreshToken: 'guest-refresh' });
      if (url.includes('/auth/me')) return jsonResponse(guestMeResponse);
      if (url.includes('/auth/claim') && init?.method === 'POST') {
        return jsonResponse({
          id: 'guest-1',
          email: 'claimed@example.com',
          displayName: 'Guest8317',
          emailConfirmed: true,
          isAdmin: false,
          isGuest: false,
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await user.click(screen.getByRole('button', { name: 'Play as guest' }));
    expect(await screen.findByText('Playing as Guest8317.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Settings' }));
    await user.type(screen.getByLabelText('Email'), 'claimed@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.type(screen.getByLabelText('Confirm password'), 'password123');
    await user.click(screen.getByRole('button', { name: 'Save my progress' }));

    await waitFor(() => expect(screen.queryByText('Playing as Guest8317.')).not.toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Save your progress' })).not.toBeInTheDocument();
    // The claim section itself (SettingsScreen's own isGuest-gated form)
    // also disappears once currentUser.isGuest flips to false — same
    // App-level state flowing through onAccountClaimed.
    expect(screen.queryByText('Save your progress')).not.toBeInTheDocument();
  });
});

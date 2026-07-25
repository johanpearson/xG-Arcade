import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from './App';

// REQ-717's 2026-07-21 "Bot-check (captcha)" addition / ADR-0037: no live
// Cloudflare site key exists in this sandbox — AuthScreen.tsx's "Play as
// guest" now obtains a Turnstile token before calling POST /auth/guest, so
// every test below that clicks that button needs this mocked, the same way
// AuthScreen.test.tsx mocks it directly. This file only exercises the
// resulting guest-banner/claim behavior, not the token-acquisition/reset
// mechanics themselves (covered in AuthScreen.test.tsx/turnstile.test.ts).
vi.mock('./lib/turnstile', () => ({
  getTurnstileToken: () => Promise.resolve('turnstile-token-stub'),
  resetTurnstileWidget: () => {},
  // Sign-in latency fix (2026-07-25): AuthScreen.tsx/DeleteAccountScreen.tsx
  // now also call preloadTurnstileScript() from a mount-only effect.
  preloadTurnstileScript: () => {},
}));

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

// REQ-719: every test in this file that needs to reach AuthScreen's actual
// form now has to get there via the splash screen's own call-to-action
// first — App.tsx no longer renders AuthScreen directly the moment there's
// no accessToken. Centralized here rather than repeated per test.
async function goToAuthScreen(user: ReturnType<typeof userEvent.setup>): Promise<void> {
  await user.click(await screen.findByRole('button', { name: 'Log in or sign up' }));
}

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

  it('an invalid/expired/revoked stored refresh token fails the silent refresh and falls through to the splash screen (REQ-719), not directly to AuthScreen — clearing both stored tokens, never retrying indefinitely', async () => {
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

    // REQ-719: the splash screen, not AuthScreen directly.
    expect(await screen.findByTestId('splash-screen')).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Log in' })).not.toBeInTheDocument();
    expect(window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBeNull();
    expect(window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBeNull();

    // Never an infinite retry loop: exactly one refresh attempt for this
    // single failed session-restore.
    const refreshCalls = fetchMock.mock.calls.filter(([input]) => String(input).includes('/auth/refresh'));
    expect(refreshCalls).toHaveLength(1);

    // AuthScreen is still reachable from here via the splash screen's CTA.
    const user = userEvent.setup();
    await goToAuthScreen(user);
    expect(await screen.findByRole('tab', { name: 'Log in' })).toBeInTheDocument();
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
    await goToAuthScreen(user);
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

    // REQ-719: logout returns to the splash screen, not directly to
    // AuthScreen — the same single unauthenticated entry point a
    // first-time visitor sees.
    expect(await screen.findByTestId('splash-screen')).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Log in' })).not.toBeInTheDocument();
    expect(window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBeNull();
    expect(window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBeNull();

    // And AuthScreen is still reachable from there, not a dead end.
    await goToAuthScreen(user);
    expect(await screen.findByRole('tab', { name: 'Log in' })).toBeInTheDocument();
  });

  it('deleting the account (Settings → Delete my account permanently) clears both the access token and the refresh token, and returns to the splash screen (REQ-719), not directly to AuthScreen', async () => {
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

    // REQ-719: back to the splash screen, not directly to AuthScreen.
    expect(await screen.findByTestId('splash-screen')).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Log in' })).not.toBeInTheDocument();
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
    await goToAuthScreen(user);
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
    await goToAuthScreen(user);
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
    await goToAuthScreen(user);
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

// REQ-719: the unauthenticated splash/landing screen shown before
// AuthScreen. The individual logout/account-deletion/failed-refresh
// "returns to splash, not AuthScreen" assertions live alongside their own
// existing tests above (REQ-715 describe block); this block covers the
// splash screen's own two remaining "Test level" claims — that it renders
// instead of AuthScreen with no session at all, and that its
// call-to-action actually reaches AuthScreen.
describe('App (REQ-719: unauthenticated splash screen)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  it('REQ-719: renders the splash screen, not AuthScreen, when there is no session at all', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);

    expect(await screen.findByTestId('splash-screen')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Log in or sign up' })).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Log in' })).not.toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Sign up' })).not.toBeInTheDocument();
  });

  it('REQ-719: activating the splash screen\'s call-to-action navigates to AuthScreen', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await goToAuthScreen(user);

    expect(await screen.findByRole('tab', { name: 'Log in' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Sign up' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Log in or sign up' })).not.toBeInTheDocument();
  });
});

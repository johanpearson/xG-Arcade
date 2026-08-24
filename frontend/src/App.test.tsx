import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import { GUEST_EXPIRY_COPY } from './lib/guestExpiryCopy';

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

// REQ-511 ("Test level: ... UI (an active banner is visible to a logged-in
// user, a guest, and a fully logged-out visitor...")): AnnouncementBanner.
// test.tsx already covers the component's own fetch/render logic (including
// the no-Authorization-header proxy for "requires no authentication") in
// isolation; this block proves the actual mounting point in App.tsx — above
// every auth-gated branch — really does put the banner on screen across all
// three real render paths (splash/logged-out, guest session, normal
// logged-in account), the same shape of assertion as the REQ-717 guest
// banner and REQ-903 footer-button blocks above/below this one.
describe('App (REQ-511: announcement banner)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  const activeBanner = { active: true, message: 'Scheduled maintenance tonight at 10pm UTC.' };

  it('REQ-511: shows the active banner on the splash screen for a fully logged-out visitor', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/announcement-banner')) return jsonResponse(activeBanner);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);

    expect(await screen.findByTestId('splash-screen')).toBeInTheDocument();
    expect(await screen.findByText('Scheduled maintenance tonight at 10pm UTC.')).toBeInTheDocument();
  });

  it('REQ-511: shows the active banner for a guest session', async () => {
    const guestMeResponse = {
      id: 'guest-1',
      email: null,
      displayName: 'Guest8317',
      emailConfirmed: false,
      isAdmin: false,
      isGuest: true,
    };
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/announcement-banner')) return jsonResponse(activeBanner);
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
    expect(await screen.findByText('Scheduled maintenance tonight at 10pm UTC.')).toBeInTheDocument();
  });

  it('REQ-511: shows the active banner for a normal logged-in (non-guest) account', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/announcement-banner')) return jsonResponse(activeBanner);
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
    expect(await screen.findByText('Scheduled maintenance tonight at 10pm UTC.')).toBeInTheDocument();
  });
});

// REQ-718 UI addendum (rule 4/5, 2026-08-01): the guest-only logout
// confirmation dialog gating handleLogoutClick, and the guest-expiry copy
// rendered in the banner/Settings. This describe block covers the dialog's
// App-level wiring (GuestLogoutConfirm.tsx has its own accessibility/focus
// comments but no dedicated unit suite — the "when does it open, and what
// does each button actually do to session state" behavior is App.tsx's own
// responsibility, exercised here) and the expiry copy's presence/absence in
// the banner. SettingsScreen.test.tsx covers the same copy's presence/
// absence in Settings in isolation.
describe('App (REQ-718: guest logout confirmation and guest-expiry copy)', () => {
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

  function stubFetchForGuestLogin(extra: (url: string, init?: RequestInit) => Promise<Response> | undefined = () => undefined) {
    return vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const extraResult = extra(url, init);
      if (extraResult) return extraResult;
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/guest')) return jsonResponse({ accessToken: 'guest-token', refreshToken: 'guest-refresh' });
      if (url.includes('/auth/me')) return jsonResponse(guestMeResponse);
      throw new Error(`Unexpected fetch: ${url}`);
    });
  }

  async function signInAsGuest(user: ReturnType<typeof userEvent.setup>): Promise<void> {
    await goToAuthScreen(user);
    await user.click(screen.getByRole('button', { name: 'Play as guest' }));
    await screen.findByText('Playing as Guest8317.');
  }

  it('REQ-718: a guest clicking "Log out" sees the confirmation dialog instead of being logged out immediately', async () => {
    vi.stubGlobal('fetch', stubFetchForGuestLogin());
    const user = userEvent.setup();

    render(<App />);
    await signInAsGuest(user);

    await user.click(screen.getByRole('button', { name: 'Log out' }));

    const dialog = await screen.findByTestId('guest-logout-confirm');
    expect(dialog).toBeInTheDocument();
    expect(within(dialog).getByRole('heading', { name: 'Log out and delete guest account?' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Log out and delete account' })).toBeInTheDocument();
    // Still signed in and on the same screen — nothing has happened yet.
    expect(screen.getByText('Choose a game')).toBeInTheDocument();
  });

  it('REQ-718: cancelling the dialog closes it and leaves the session, stored tokens, and screen exactly as they were, with no POST /auth/logout call', async () => {
    const fetchMock = stubFetchForGuestLogin();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await signInAsGuest(user);

    await user.click(screen.getByRole('button', { name: 'Log out' }));
    await screen.findByTestId('guest-logout-confirm');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByTestId('guest-logout-confirm')).not.toBeInTheDocument();
    // Session/tokens untouched.
    expect(window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBe('guest-token');
    expect(window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBe('guest-refresh');
    // Still on the same screen, still shown as signed in.
    expect(screen.getByText('Playing as Guest8317.')).toBeInTheDocument();
    expect(screen.queryByTestId('splash-screen')).not.toBeInTheDocument();
    // No backend logout call was made at all.
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/auth/logout'))).toBe(false);
  });

  it('REQ-718: confirming the dialog closes it and runs the existing, unmodified handleLogout flow (local clear-and-reset, plus the best-effort POST /auth/logout)', async () => {
    const fetchMock = stubFetchForGuestLogin((url, init) => {
      if (url.includes('/auth/logout') && init?.method === 'POST') {
        return Promise.resolve({ ok: true, status: 204, json: () => Promise.resolve(null) } as Response);
      }
      return undefined;
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await signInAsGuest(user);

    await user.click(screen.getByRole('button', { name: 'Log out' }));
    await screen.findByTestId('guest-logout-confirm');

    await user.click(screen.getByRole('button', { name: 'Log out and delete account' }));

    expect(screen.queryByTestId('guest-logout-confirm')).not.toBeInTheDocument();
    // REQ-719: back to the splash screen, same as any other logout.
    expect(await screen.findByTestId('splash-screen')).toBeInTheDocument();
    expect(window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBeNull();
    expect(window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)).toBeNull();

    // The existing best-effort POST /auth/logout still fires, with the
    // guest's own access token, exactly as handleLogout already did before
    // this addition.
    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/auth/logout'),
        expect.objectContaining({
          method: 'POST',
          headers: expect.objectContaining({ Authorization: 'Bearer guest-token' }),
        }),
      ),
    );
  });

  it('REQ-718: a non-guest account clicking "Log out" never renders any confirmation dialog, and logs out immediately as before', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Log out' }));

    // No dialog ever mounts for a non-guest — logout proceeds straight
    // through, same as REQ-715's own existing "logging out clears both the
    // access token and the refresh token" test above.
    expect(screen.queryByTestId('guest-logout-confirm')).not.toBeInTheDocument();
    expect(await screen.findByTestId('splash-screen')).toBeInTheDocument();
    expect(window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)).toBeNull();
  });

  it('REQ-718: the guest-expiry copy renders in the guest banner for a guest account and states the actual 7-day/30-day policy', async () => {
    vi.stubGlobal('fetch', stubFetchForGuestLogin());
    const user = userEvent.setup();

    render(<App />);
    await signInAsGuest(user);

    const expiryCopy = screen.getByTestId('guest-expiry-copy');
    expect(expiryCopy).toBeInTheDocument();
    expect(expiryCopy).toHaveTextContent(GUEST_EXPIRY_COPY);
  });

  it('REQ-718: the guest-expiry copy is absent from the banner (and Settings) for a non-guest account', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    expect(screen.queryByTestId('guest-expiry-copy')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Settings' }));
    await screen.findByRole('heading', { name: 'Settings' });
    expect(screen.queryByTestId('guest-expiry-copy-settings')).not.toBeInTheDocument();
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

// REQ-720: the header nav's "Games" entry, wired into the real app —
// HeaderNav.test.tsx already covers the component's own toggle/aria/
// non-navigating behavior in isolation; this covers it reaching the actual
// grid screen and the "xG Arcade" title still working unchanged alongside
// it.
describe('App (REQ-720: "Games" nav entry)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  function stubFetchForGrid() {
    return vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      if (url.includes('/rounds/current')) return jsonResponse(null);
      throw new Error(`Unexpected fetch: ${url}`);
    });
  }

  it('REQ-720: Games → xG Grid reaches the grid screen, and the "xG Arcade" title still reaches GameSelectScreen unchanged', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    vi.stubGlobal('fetch', stubFetchForGrid());
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    // Scoped to the nav: GameSelectScreen's own "xG Grid" tile also renders
    // "xG Grid" text while game-select is showing, so an unscoped query here
    // would match two elements.
    const nav = screen.getByRole('navigation');
    await user.click(within(nav).getByRole('button', { name: 'Games' }));
    await user.click(within(nav).getByRole('button', { name: 'xG Grid' }));

    expect(await screen.findByText('No round to play right now')).toBeInTheDocument();
    expect(screen.queryByText('Choose a game')).not.toBeInTheDocument();

    // REQ-720: the title is unchanged — still routes back to
    // GameSelectScreen from anywhere, including from inside the grid
    // screen this "Games" shortcut just reached.
    await user.click(screen.getByRole('button', { name: 'xG Arcade' }));
    expect(await screen.findByText('Choose a game')).toBeInTheDocument();
  });

  it('REQ-720: the "xG Grid" entry gets aria-current="page" while the grid screen is showing', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    vi.stubGlobal('fetch', stubFetchForGrid());
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    // Scoped to the nav: GameSelectScreen's own "xG Grid" tile also renders
    // "xG Grid" text while game-select is showing, so an unscoped query here
    // would match two elements.
    const nav = screen.getByRole('navigation');
    await user.click(within(nav).getByRole('button', { name: 'Games' }));
    expect(within(nav).getByRole('button', { name: 'xG Grid' })).not.toHaveAttribute('aria-current');

    await user.click(within(nav).getByRole('button', { name: 'xG Grid' }));
    await screen.findByText('No round to play right now');

    await user.click(within(nav).getByRole('button', { name: 'Games' }));
    expect(within(nav).getByRole('button', { name: 'xG Grid' })).toHaveAttribute('aria-current', 'page');
  });
});

// REQ-721/ADR-0039: hash-based URL-per-screen support. E2E
// (tests/e2e/url-routing.spec.ts) covers the full real-browser reload
// round trip; this covers the ordering constraints that must hold
// regardless of browser behavior — a fresh login always lands on
// game-select ignoring any prior hash, and a reload with no valid session
// never restores an authenticated screen from the URL.
describe('App (REQ-721: URL reflects current screen)', () => {
  afterEach(() => {
    // location.hash itself is reset globally (tests/unit/setup.ts) since
    // jsdom's window persists across tests within a file.
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  it('REQ-721: navigating via the header nav updates location.hash to match the screen shown', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
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
    expect(window.location.hash).toBe('#/game-select');

    await user.click(screen.getByRole('button', { name: 'Settings' }));
    expect(window.location.hash).toBe('#/settings');

    await user.click(screen.getByRole('button', { name: 'xG Arcade' }));
    expect(window.location.hash).toBe('#/game-select');
  });

  it('REQ-721: reloading (remounting) with a valid stored session restores the screen the hash denotes, not the game-select default', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    window.location.hash = '#/settings';

    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);

    expect(await screen.findByRole('heading', { name: 'Settings' })).toBeInTheDocument();
    expect(screen.queryByText('Choose a game')).not.toBeInTheDocument();
  });

  it('REQ-721: reloading (remounting) with no valid session shows the splash screen regardless of the hash present', async () => {
    window.location.hash = '#/settings';

    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);

    expect(await screen.findByTestId('splash-screen')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Settings' })).not.toBeInTheDocument();
  });

  it('REQ-721: a fresh login always lands on game-select regardless of whatever hash was present before submitting', async () => {
    window.location.hash = '#/settings';

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
    expect(window.location.hash).toBe('#/game-select');
  });

  it('REQ-721: logging out clears the hash so a stale authenticated-screen hash never lingers', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    window.location.hash = '#/settings';

    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await screen.findByRole('heading', { name: 'Settings' });

    await user.click(screen.getByRole('button', { name: 'Log out' }));

    expect(await screen.findByTestId('splash-screen')).toBeInTheDocument();
    expect(window.location.hash).toBe('');
  });

  // Quality-architect (2026-07-25) flagged that only game-select, settings,
  // and leagues (the latter in tests/e2e/url-routing.spec.ts) were ever
  // actually asserted against SCREEN_HASHES, despite REQ-721 requiring all
  // six Screen values to get a distinct URL with working reload-restore.
  // The six tests below close that gap for the remaining three
  // (grid/leaderboard/admin) as unit/component tests here rather than new
  // E2E — reaching AdminScreen only needs an admin `meResponse` and the
  // existing `navigateTo('admin')` path already exercised elsewhere in this
  // file, not a real backend admin fixture.
  it('REQ-721: navigating to the grid screen updates location.hash to #/grid', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      if (url.includes('/rounds/current')) return jsonResponse(null);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    // "xG Grid" also appears as GameSelectScreen's own tile while
    // game-select is showing, so this is scoped to the nav, matching
    // REQ-720's own test above.
    const nav = screen.getByRole('navigation');
    await user.click(within(nav).getByRole('button', { name: 'Games' }));
    await user.click(within(nav).getByRole('button', { name: 'xG Grid' }));

    await screen.findByText('No round to play right now');
    expect(window.location.hash).toBe('#/grid');
  });

  it('REQ-721: navigating to the leaderboard screen updates location.hash to #/leaderboard', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      if (url.includes('/leagues/global/leaderboard')) {
        return jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Leaderboard' }));

    expect(await screen.findByRole('heading', { name: 'Global leaderboard' })).toBeInTheDocument();
    expect(window.location.hash).toBe('#/leaderboard');
  });

  it('REQ-721: navigating to the admin screen (as an admin user) updates location.hash to #/admin', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    const adminMeResponse = { ...meResponse, isAdmin: true };
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(adminMeResponse);
      if (url.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (url.includes('/admin/rounds/xg-grid/active')) return jsonResponse(null);
      // /admin/accounts/metrics is fetched by AdminScreen's own
      // AccountMetricsSection on mount but isn't needed to reach the
      // "Admin" heading below — left unmocked deliberately, the same way
      // AdminScreen.test.tsx's simplest render test does, since that
      // component's own try/catch contains the resulting rejection.
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Settings' }));
    await screen.findByRole('heading', { name: 'Settings' });
    await user.click(screen.getByRole('button', { name: 'Admin' }));

    expect(await screen.findByRole('heading', { name: 'Admin' })).toBeInTheDocument();
    expect(window.location.hash).toBe('#/admin');
  });

  it('REQ-721: reloading (remounting) with location.hash already #/grid and a valid stored session restores the grid screen', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    window.location.hash = '#/grid';

    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      if (url.includes('/rounds/current')) return jsonResponse(null);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);

    expect(await screen.findByText('No round to play right now')).toBeInTheDocument();
    expect(screen.queryByText('Choose a game')).not.toBeInTheDocument();
  });

  it('REQ-721: reloading (remounting) with location.hash already #/leaderboard and a valid stored session restores the leaderboard screen', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    window.location.hash = '#/leaderboard';

    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      if (url.includes('/leagues/global/leaderboard')) {
        return jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);

    expect(await screen.findByRole('heading', { name: 'Global leaderboard' })).toBeInTheDocument();
    expect(screen.queryByText('Choose a game')).not.toBeInTheDocument();
  });

  it('REQ-721: reloading (remounting) with location.hash already #/admin, a valid stored session, and an admin user restores the admin screen', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    window.location.hash = '#/admin';
    const adminMeResponse = { ...meResponse, isAdmin: true };

    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(adminMeResponse);
      if (url.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (url.includes('/admin/rounds/xg-grid/active')) return jsonResponse(null);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);

    expect(await screen.findByRole('heading', { name: 'Admin' })).toBeInTheDocument();
    expect(screen.queryByText('Choose a game')).not.toBeInTheDocument();
  });

  // Quality-architect (2026-08-08) flagged that 'admin-suggestions'
  // (REQ-509/REQ-510, S-090, ADR-0053) — added to Screen/SCREEN_HASHES as
  // a seventh value after the six tests above closed the original gap for
  // the first six — reopened the same gap with no matching test. This one
  // mirrors the 'admin navigating' test above, one hop further via
  // AdminScreen's "Player suggestions" link (SuggestionsScreen's only
  // entry point, per ADR-0053), matching this describe block's existing
  // pattern rather than adding a separate reload-restore test (no
  // reload-restore counterpart exists for 'admin' either, immediately
  // above, since ADR-0053 gives this screen no independent top-level nav
  // entry to remount straight back into).
  it('REQ-721: navigating to the admin suggestions screen updates location.hash to #/admin/suggestions', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    const adminMeResponse = { ...meResponse, isAdmin: true };
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(adminMeResponse);
      if (url.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (url.includes('/admin/rounds/xg-grid/active')) return jsonResponse(null);
      if (url.includes('/admin/suggestions')) return jsonResponse([]);
      // /admin/accounts/metrics is fetched by AdminScreen's own
      // AccountMetricsSection on mount but isn't needed to reach the
      // "Player suggestions" link below — left unmocked deliberately, the
      // same way the 'admin' navigation test above does.
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Settings' }));
    await screen.findByRole('heading', { name: 'Settings' });
    await user.click(screen.getByRole('button', { name: 'Admin' }));
    await screen.findByRole('heading', { name: 'Admin' });
    // REQ-516: PlayerSuggestionsEntry now lives in the "Grid" nav group,
    // which isn't the default ("Users") — select it before interacting.
    await user.click(screen.getByRole('tab', { name: 'Grid' }));
    await user.click(screen.getByRole('button', { name: 'Player suggestions' }));

    expect(await screen.findByRole('heading', { name: 'Player suggestions' })).toBeInTheDocument();
    expect(window.location.hash).toBe('#/admin/suggestions');
  });

  // REQ-512: PlayerSuggestionsEntry's own code comment in AdminScreen.tsx
  // claims this screen's remount (App.tsx's ternary unmounts AdminScreen
  // while SuggestionsScreen is open and remounts it on the way back) is
  // what naturally re-triggers the badge's fetch, with no extra refresh
  // plumbing. AdminScreen.test.tsx's own REQ-512 tests each render
  // AdminScreen once in isolation and can't exercise that remount — this
  // test goes through the real App.tsx navigation round trip to prove the
  // claim rather than leaving it as an untested comment.
  it('REQ-512: navigating back to the admin screen from suggestions after resolving one refreshes the pending-suggestion badge', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    const adminMeResponse = { ...meResponse, isAdmin: true };
    const suggestion = (id: string) => ({
      id,
      playerName: 'Someone Player',
      assertedClubs: ['Some Club'],
      assertedNationality: 'Some Country',
      submittingUserId: 'user-1',
      submittingUserDisplayName: 'Player One',
      rowCategoryType: 'Nationality',
      colCategoryType: 'Club',
      createdAt: '2026-08-01T00:00:00Z',
    });
    // First call (AdminScreen's initial mount) returns 2 pending; every call
    // after that (i.e. the remount on the way back from SuggestionsScreen)
    // returns 1, simulating one having been resolved in the meantime.
    let suggestionsCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(adminMeResponse);
      if (url.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (url.includes('/admin/rounds/xg-grid/active')) return jsonResponse(null);
      if (url.includes('/admin/suggestions')) {
        suggestionsCallCount += 1;
        return jsonResponse(
          suggestionsCallCount === 1 ? [suggestion('s-1'), suggestion('s-2')] : [suggestion('s-1')],
        );
      }
      // /admin/accounts/metrics is fetched by AdminScreen's own
      // AccountMetricsSection on mount but isn't needed to reach the
      // "Player suggestions" badge below — left unmocked deliberately, the
      // same way the other admin navigation tests in this block do.
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Settings' }));
    await screen.findByRole('heading', { name: 'Settings' });
    await user.click(screen.getByRole('button', { name: 'Admin' }));
    await screen.findByRole('heading', { name: 'Admin' });
    // REQ-516: PlayerSuggestionsEntry now lives in the "Grid" nav group,
    // which isn't the default ("Users") — select it before interacting.
    await user.click(screen.getByRole('tab', { name: 'Grid' }));

    expect(await screen.findByRole('button', { name: 'Player suggestions (2)' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Player suggestions (2)' }));
    await screen.findByRole('heading', { name: 'Player suggestions' });

    await user.click(screen.getByRole('button', { name: 'Back to admin' }));

    expect(await screen.findByRole('heading', { name: 'Admin' })).toBeInTheDocument();
    // REQ-512/REQ-516: AdminScreen remounts on the way back (App.tsx's
    // ternary), which resets `activeGroup` to its "Users" default — select
    // "Grid" again before asserting on the refreshed badge.
    await user.click(screen.getByRole('tab', { name: 'Grid' }));
    expect(await screen.findByRole('button', { name: 'Player suggestions (1)' })).toBeInTheDocument();
  });
});

// REQ-903/ADR-0064: the footer's "Report a problem" entry point — moved
// out of SettingsScreen.tsx (2026-08-10, same day as the original build)
// so it's reachable from whatever screen a player is actually looking at.
// IncidentReportDialog.test.tsx covers the dialog's own self-contained
// behavior in isolation; this describe block covers only App.tsx's own
// wiring (when the button appears, and that it opens the right dialog with
// the right route/isGuest values).
describe('App (REQ-903: footer incident-report entry point)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  it('REQ-903: no "Report a problem" button renders while logged out (splash/auth screen)', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await goToAuthScreen(user);

    expect(screen.queryByRole('button', { name: 'Report a problem' })).not.toBeInTheDocument();
  });

  it('REQ-903: a logged-in (non-guest) account sees the footer button on every screen, and it opens the dialog', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
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

    const reportButton = screen.getByRole('button', { name: 'Report a problem' });
    await user.click(reportButton);

    expect(await screen.findByRole('dialog', { name: 'Report a problem' })).toBeInTheDocument();
    expect(screen.queryByTestId('incident-report-guest-locked-copy')).not.toBeInTheDocument();
  });

  it('REQ-903: a guest account still sees the footer button (advertised, not hidden), and the dialog shows the guest-locked copy', async () => {
    const guestMeResponse = {
      id: 'guest-1',
      email: null,
      displayName: 'Guest8317',
      emailConfirmed: false,
      isAdmin: false,
      isGuest: true,
    };
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
    await screen.findByText('Playing as Guest8317.');

    await user.click(screen.getByRole('button', { name: 'Report a problem' }));

    expect(await screen.findByTestId('incident-report-guest-locked-copy')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send report' })).toBeDisabled();
  });

  it('REQ-903: submitting a report defaults the Screen dropdown to whatever screen the button was clicked from, and sends the current origin as environment', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      if (url.includes('/leagues/global/leaderboard')) return jsonResponse({ rows: [], nextCursor: null });
      if (url.includes('/incidents') && init?.method === 'POST') {
        return jsonResponse({ issueUrl: 'https://github.com/johanpearson/xg-arcade/issues/9' });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: 'Leaderboard' }));
    await screen.findByRole('button', { name: 'Report a problem' });

    await user.click(screen.getByRole('button', { name: 'Report a problem' }));
    expect((screen.getByLabelText('Screen') as HTMLSelectElement).value).toBe('leaderboard');
    await user.type(screen.getByLabelText('Title'), 'Something broke');
    await user.type(screen.getByLabelText('What went wrong?'), 'Something broke.');
    await user.click(screen.getByRole('button', { name: 'Send report' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/incidents'),
        expect.objectContaining({
          body: JSON.stringify({
            title: 'Something broke',
            description: 'Something broke.',
            screen: 'leaderboard',
            environment: window.location.origin,
          }),
        }),
      ),
    );
  });
});

// REQ-1210/ADR-0083: App.tsx's own wiring for the round-completion banner's
// "View leaderboard" link — handleViewRoundLeaderboard (seeds
// leaderboardInitial + navigates) and the header nav's onSelectLeaderboard
// (explicitly clears leaderboardInitial first). GridScreen.test.tsx/
// PathScreen.test.tsx already prove each screen calls onViewRoundLeaderboard
// with the right LeaderboardRoundTarget; LeaderboardScreen.test.tsx already
// proves initialGameKey/initialScope/initialRoundId are consumed correctly
// when passed directly. Neither covers what App.tsx does with the callback
// in between — that's this describe block's only job. Goes through xG
// Path (not xG Grid) so the seeded gameKey is provably different from
// LeaderboardScreen's own xG Grid default, and forces the 'past' scope (the
// round has since closed) so initialRoundId's "drill straight into round
// detail" behavior is exercised too, not just the 'live' scope GridScreen's
// own REQ-1210 tests already cover more directly.
describe('App (REQ-1210: round-completion banner "View leaderboard" wiring)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  const pathBasePuzzle = {
    puzzleId: 'puzzle-1',
    clues: [
      {
        turnNumber: 1,
        kind: 'ClubReveal',
        clubs: [{ clubName: 'Ajax', appearanceCount: 74 }],
        yearRanges: null,
        textValue: null,
      },
    ],
    guess: null,
  };

  function pathRoundResponse(puzzles: unknown[] = [pathBasePuzzle]) {
    return {
      roundId: 'round-1',
      startTime: '2026-07-10T00:00:00Z',
      endTime: '2026-07-11T00:00:00Z',
      allowGuessChange: false,
      puzzles,
    };
  }

  const solvedPathPuzzle = {
    ...pathBasePuzzle,
    guess: {
      isCorrect: true,
      attemptCount: 1,
      locked: true,
      submittedName: 'Zlatan Ibrahimović',
      resolvedPlayerName: 'Zlatan Ibrahimović',
      resolvedPlayerPhotoUrl: null,
      points: 14,
    },
  };

  // Mirrors GridScreen.test.tsx's "reports the 'past' scope..." fixture:
  // call 1 (mount) unsolved, call 2 (post-guess refetch) solved, call 3+
  // (the "View leaderboard" live-vs-past re-check, PathScreen.tsx's own
  // handleViewCompletedRoundLeaderboard) reports no active round at all —
  // i.e. this round has since closed, forcing the 'past' scope.
  function stubFetchForPathCompletion() {
    let pathFetchCount = 0;
    return vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      if (url.endsWith('/path/current')) {
        pathFetchCount += 1;
        if (pathFetchCount === 1) return jsonResponse(pathRoundResponse());
        if (pathFetchCount === 2) return jsonResponse(pathRoundResponse([solvedPathPuzzle]));
        return jsonResponse({ title: 'No active round' }, 404);
      }
      if (url.includes('/guesses') && init?.method === 'POST') {
        return jsonResponse({
          isCorrect: true,
          attemptCount: 1,
          locked: true,
          resolvedPlayerName: 'Zlatan Ibrahimović',
          resolvedPlayerPhotoUrl: null,
          candidates: null,
        });
      }
      // LeaderboardScreen's four always-mounted scope components — only the
      // ones this test's target scope ('past') actually activates fetch for
      // real data; the rest (all-time's background poll) just need a benign
      // empty response, same shape the existing REQ-721/REQ-903 leaderboard
      // tests above already use.
      if (url.includes('/leagues/global/leaderboard/closed-rounds/round-1')) {
        return jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false });
      }
      if (url.includes('/leagues/global/leaderboard/closed-rounds')) {
        return jsonResponse({ rounds: [], nextCursor: null, hasMore: false });
      }
      if (url.includes('/leagues/global/leaderboard')) {
        return jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
  }

  async function completeXgPathRoundAndViewLeaderboard(user: ReturnType<typeof userEvent.setup>): Promise<void> {
    const nav = screen.getByRole('navigation');
    await user.click(within(nav).getByRole('button', { name: 'Games' }));
    await user.click(within(nav).getByRole('button', { name: 'xG Path' }));

    await user.type(await screen.findByLabelText('Player name'), 'Zlatan Ibrahimović');
    await user.click(screen.getByRole('button', { name: 'Guess' }));

    await user.click(await screen.findByRole('button', { name: 'View leaderboard' }));
  }

  it('REQ-1210: activating "View leaderboard" mounts LeaderboardScreen seeded with that round\'s game, scope, and round id', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    vi.stubGlobal('fetch', stubFetchForPathCompletion());
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    await completeXgPathRoundAndViewLeaderboard(user);

    expect(await screen.findByRole('heading', { name: 'Global leaderboard' })).toBeInTheDocument();
    expect(window.location.hash).toBe('#/leaderboard');

    // initialGameKey: 'xg-path' — provably not LeaderboardScreen's own
    // xG Grid default.
    expect(screen.getByRole('tab', { name: 'xG Path' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'xG Grid' })).toHaveAttribute('aria-selected', 'false');
    // initialScope: 'past' — provably not LeaderboardScreen's own 'all-time'
    // default.
    expect(screen.getByRole('tab', { name: 'Previous Rounds' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'All-time' })).toHaveAttribute('aria-selected', 'false');
    // initialRoundId: 'round-1' — drilled straight into that round's own
    // detail view (PastRoundsLeaderboard's own consumption of the prop),
    // bypassing the closed-round list entirely.
    expect(await screen.findByRole('button', { name: 'Back to previous rounds' })).toBeInTheDocument();
  });

  it('REQ-1210: revisiting the leaderboard via the header nav\'s own entry point after a completion-banner visit clears the seeded round', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    vi.stubGlobal('fetch', stubFetchForPathCompletion());
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    await completeXgPathRoundAndViewLeaderboard(user);
    await screen.findByRole('button', { name: 'Back to previous rounds' });

    // Navigate away from 'leaderboard' entirely first — the header nav's
    // "Leaderboard" click below must be a genuine fresh mount of
    // LeaderboardScreen, not a same-instance re-render. leaderboardInitial's
    // own doc comment in App.tsx: initial* props are read once via
    // LeaderboardScreen's useState lazy initializer, so a prop change on an
    // already-mounted instance (which is what re-clicking "Leaderboard"
    // while already on that screen would be) has no effect either way and
    // wouldn't actually exercise onSelectLeaderboard's setLeaderboardInitial
    // (null) clear.
    await user.click(screen.getByRole('button', { name: 'xG Arcade' }));
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Leaderboard' }));

    expect(await screen.findByRole('heading', { name: 'Global leaderboard' })).toBeInTheDocument();
    // Plain defaults, not the stale xg-path/past/round-1 target seeded by
    // the earlier completion-banner visit — proving onSelectLeaderboard's
    // setLeaderboardInitial(null) actually took effect before this mount.
    expect(screen.getByRole('tab', { name: 'xG Grid' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'All-time' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByRole('button', { name: 'Back to previous rounds' })).not.toBeInTheDocument();
  });
});

// REQ-411 (S-179): App.tsx's own routing/seeding wiring for SCREEN-13's
// stats/profile view — statsTarget/statsReturnScreen state,
// handleOpenOwnStats/handleSelectPlayerStats, and the 'stats' screen render
// branch (including its `statsTarget?.userId ?? currentUser?.id ?? ''`
// reload-restore fallback). UserStatsScreen.test.tsx already covers that
// component's own rendering/fetch behavior given a userId/displayName; this
// describe block covers only what App.tsx does to seed and route to it —
// same split as the REQ-1210 describe block above for LeaderboardScreen's
// own initial* props. Mirrors this file's existing 'admin'-screen coverage
// (navigate-and-verify-hash, reload-restore) one section up.
describe('App (REQ-411: stats/profile screen navigation)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  const ownStatsResponse = {
    hasRoundsPlayed: true,
    roundsPlayed: 6,
    bestFinalPoints: 8,
    averageFinalPoints: 14.5,
    rank: 3,
  };

  const otherPlayerStatsResponse = {
    hasRoundsPlayed: true,
    roundsPlayed: 2,
    bestFinalPoints: 20,
    averageFinalPoints: 22,
    rank: null,
  };

  it('REQ-411: opening "My stats" from Settings shows the current account\'s own stats, and "Back" returns to Settings', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      if (url.includes('/users/user-1/stats')) return jsonResponse(ownStatsResponse);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Settings' }));
    await screen.findByRole('heading', { name: 'Settings' });
    await user.click(screen.getByRole('button', { name: 'My stats' }));

    // meResponse's own id/displayName — 'Player One'/'user-1' — is the only
    // source App.tsx's handleOpenOwnStats has for "own stats".
    expect(await screen.findByRole('heading', { name: "Player One's stats" })).toBeInTheDocument();
    expect(window.location.hash).toBe('#/stats');
    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/users/user-1/stats?gameKey=xg-grid'),
        expect.anything(),
      ),
    );

    await user.click(screen.getByRole('button', { name: 'Back' }));

    // statsReturnScreen was seeded 'settings' by handleOpenOwnStats — "Back"
    // must land there, not somewhere else (e.g. game-select).
    expect(await screen.findByRole('heading', { name: 'Settings' })).toBeInTheDocument();
    expect(window.location.hash).toBe('#/settings');
  });

  it('REQ-411: selecting a leaderboard row\'s display name shows that player\'s stats (not the logged-in account\'s own), and "Back" returns to the Leaderboard', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    const otherPlayerRow = {
      rank: 5,
      userId: 'user-42',
      displayName: 'Robin',
      totalPoints: 77,
      isRequestingUser: false,
    };
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      if (url.includes('/leagues/global/leaderboard')) {
        return jsonResponse({ rows: [otherPlayerRow], requestingUserRow: null, nextCursor: null, hasMore: false });
      }
      if (url.includes('/users/user-42/stats')) return jsonResponse(otherPlayerStatsResponse);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<App />);
    await waitFor(() => expect(screen.getByText('Choose a game')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Leaderboard' }));
    await screen.findByRole('heading', { name: 'Global leaderboard' });
    await user.click(screen.getByRole('button', { name: 'Robin' }));

    expect(await screen.findByRole('heading', { name: "Robin's stats" })).toBeInTheDocument();
    expect(window.location.hash).toBe('#/stats');
    // Scoped to the clicked row's userId, not the logged-in account's own id
    // (meResponse's 'user-1') — this is the one thing distinguishing "own
    // stats" from "another player's" per UserStatsScreen's own doc comment.
    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/users/user-42/stats?gameKey=xg-grid'),
        expect.anything(),
      ),
    );
    expect(fetchMock).not.toHaveBeenCalledWith(
      expect.stringContaining('/users/user-1/stats'),
      expect.anything(),
    );

    await user.click(screen.getByRole('button', { name: 'Back' }));

    // statsReturnScreen was seeded 'leaderboard' by handleSelectPlayerStats
    // — "Back" must land there, not 'settings' (the other entry point's own
    // return screen, exercised by the test above).
    expect(await screen.findByRole('heading', { name: 'Global leaderboard' })).toBeInTheDocument();
    expect(window.location.hash).toBe('#/leaderboard');
  });

  it('REQ-411: reloading (remounting) with location.hash already #/stats and a valid stored session, with no prior in-session navigation, falls back to the current account\'s own stats rather than an empty/broken userId', async () => {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, 'token-abc');
    window.location.hash = '#/stats';

    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/health')) return jsonResponse({ status: 'ok' });
      if (url.includes('/auth/me')) return jsonResponse(meResponse);
      // `statsTarget` is null here (no prior handleOpenOwnStats/
      // handleSelectPlayerStats call happened this session) — App.tsx's own
      // `?? currentUser?.id ?? ''` fallback is what's under test, so this
      // matches any /users/*/stats request (including a transient
      // '/users//stats' fired before currentUser resolves from GET
      // /auth/me) so that transient call doesn't itself fail the test; the
      // assertions below confirm the fallback's *final* state is the real
      // account, not the broken empty-id request.
      if (url.includes('/stats')) return jsonResponse(ownStatsResponse);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<App />);

    expect(await screen.findByRole('heading', { name: "Player One's stats" })).toBeInTheDocument();
    expect(screen.queryByText('Choose a game')).not.toBeInTheDocument();
    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/users/user-1/stats?gameKey=xg-grid'),
        expect.anything(),
      ),
    );
  });
});

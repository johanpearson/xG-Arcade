import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuthScreen } from './AuthScreen';

// REQ-717's 2026-07-21 "Bot-check (captcha)" addition / ADR-0037: no live
// Cloudflare site key exists in this sandbox, so the token-acquisition step
// is mocked wholesale here rather than attempting to load the real script —
// these tests assert the rest of the flow (token sent in the request body,
// distinct-rejection handling resets the widget, generic rejections don't).
const getTurnstileTokenMock = vi.fn();
const resetTurnstileWidgetMock = vi.fn();
// Sign-in latency fix (2026-07-25): AuthScreen.tsx now also calls
// preloadTurnstileScript() from a mount-only effect -- stubbed here as a
// no-op so mounting the component under test doesn't throw on an
// undefined import from this wholesale module mock.
const preloadTurnstileScriptMock = vi.fn();
vi.mock('../lib/turnstile', () => ({
  getTurnstileToken: (...args: unknown[]) => getTurnstileTokenMock(...args),
  resetTurnstileWidget: (...args: unknown[]) => resetTurnstileWidgetMock(...args),
  preloadTurnstileScript: (...args: unknown[]) => preloadTurnstileScriptMock(...args),
}));

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

describe('AuthScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    getTurnstileTokenMock.mockReset();
    resetTurnstileWidgetMock.mockReset();
    preloadTurnstileScriptMock.mockReset();
  });

  // Sign-in latency fix (2026-07-25): the whole point of preloading is that
  // it happens before any button click, not in response to one.
  it('preloads the Turnstile script on mount, before any submit/guest click', () => {
    render(<AuthScreen onAuthenticated={vi.fn()} />);

    expect(preloadTurnstileScriptMock).toHaveBeenCalledTimes(1);
    expect(getTurnstileTokenMock).not.toHaveBeenCalled();
  });

  it('REQ-701: blocks signup client-side when the age checkbox is unchecked, without calling the API', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('tab', { name: 'Sign up' }));
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.type(screen.getByLabelText('Confirm password'), 'password123');
    await user.type(screen.getByLabelText('Display name'), 'Player One');
    await user.click(screen.getByRole('button', { name: 'Create account' }));

    expect(
      await screen.findByText('Confirm you are at least 16 years old to create an account.'),
    ).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(onAuthenticated).not.toHaveBeenCalled();
  });

  it('REQ-701: blocks signup client-side when the password is under 8 characters, without calling the API', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('tab', { name: 'Sign up' }));
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'short12');
    await user.type(screen.getByLabelText('Confirm password'), 'short12');
    await user.type(screen.getByLabelText('Display name'), 'Player One');
    await user.click(screen.getByLabelText(/at least 16/));
    await user.click(screen.getByRole('button', { name: 'Create account' }));

    expect(await screen.findByText('Password must be at least 8 characters.')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(onAuthenticated).not.toHaveBeenCalled();
  });

  it('REQ-701: blocks signup client-side when confirm password does not match, without calling the API', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('tab', { name: 'Sign up' }));
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.type(screen.getByLabelText('Confirm password'), 'password456');
    await user.type(screen.getByLabelText('Display name'), 'Player One');
    await user.click(screen.getByLabelText(/at least 16/));
    await user.click(screen.getByRole('button', { name: 'Create account' }));

    expect(await screen.findByText('Passwords do not match.')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(onAuthenticated).not.toHaveBeenCalled();
  });

  it('REQ-401/404: blocks signup client-side without a display name, without calling the API', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('tab', { name: 'Sign up' }));
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.type(screen.getByLabelText('Confirm password'), 'password123');
    await user.click(screen.getByLabelText(/at least 16/));
    await user.click(screen.getByRole('button', { name: 'Create account' }));

    expect(await screen.findByText('Choose a display name.')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(onAuthenticated).not.toHaveBeenCalled();
  });

  it('REQ-701: signs up, then auto-logs-in with the same credentials', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      if (String(url).endsWith('/auth/signup')) {
        return jsonResponse({ id: 'user-1', email: 'player@example.com', displayName: 'Player One' }, 201);
      }
      if (String(url).endsWith('/auth/login')) {
        return jsonResponse({ accessToken: 'token-abc', refreshToken: null });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('tab', { name: 'Sign up' }));
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.type(screen.getByLabelText('Confirm password'), 'password123');
    await user.type(screen.getByLabelText('Display name'), 'Player One');
    await user.click(screen.getByLabelText(/at least 16/));
    await user.click(screen.getByRole('button', { name: 'Create account' }));

    await waitFor(() => expect(onAuthenticated).toHaveBeenCalledWith('token-abc', null));
  });

  it('REQ-715: logging in passes the returned refreshToken through to onAuthenticated, not just the accessToken', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(() => jsonResponse({ accessToken: 'token-abc', refreshToken: 'refresh-abc' }));
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.click(screen.getByRole('button', { name: 'Log in' }));

    await waitFor(() => expect(onAuthenticated).toHaveBeenCalledWith('token-abc', 'refresh-abc'));
  });

  it('REQ-701: shows the server error detail on a failed login rather than a generic message', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse({ title: 'Login failed', detail: 'Invalid email or password.' }, 401),
    );
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AuthScreen onAuthenticated={vi.fn()} />);
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'wrongpassword');
    await user.click(screen.getByRole('button', { name: 'Log in' }));

    expect(await screen.findByText('Invalid email or password.')).toBeInTheDocument();
  });

  // REQ-606: a 429 from the backend's rate limiter (Program.cs's
  // OnRejected — {title: "Too many attempts", detail: "..."}) renders
  // through the exact same describeError path as any other ApiError, no
  // special-casing needed in AuthScreen.tsx.
  it('REQ-606: shows a clear message when the login attempt is rate-limited (429)', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse(
        { title: 'Too many attempts', detail: 'Too many attempts. Please wait a minute and try again.' },
        429,
      ),
    );
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AuthScreen onAuthenticated={vi.fn()} />);
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.click(screen.getByRole('button', { name: 'Log in' }));

    expect(
      await screen.findByText('Too many attempts. Please wait a minute and try again.'),
    ).toBeInTheDocument();
  });

  // REQ-701: the account-enumeration-safe error (AuthController.Signup's
  // generic detail when Supabase rejects the signup) renders exactly as
  // returned — this test's real purpose is documenting that the UI never
  // adds its own "this email is already registered"-style text on top of
  // whatever the server sends.
  it('REQ-701: shows the generic, enumeration-safe error on a failed signup rather than a specific one', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      if (String(url).endsWith('/auth/signup')) {
        return jsonResponse(
          {
            title: 'Signup could not be completed',
            detail: 'Check your email to confirm your account, or reset your password if you already have one.',
          },
          400,
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('tab', { name: 'Sign up' }));
    await user.type(screen.getByLabelText('Email'), 'already-has-an-account@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.type(screen.getByLabelText('Confirm password'), 'password123');
    await user.type(screen.getByLabelText('Display name'), 'Player One');
    await user.click(screen.getByLabelText(/at least 16/));
    await user.click(screen.getByRole('button', { name: 'Create account' }));

    expect(
      await screen.findByText('Check your email to confirm your account, or reset your password if you already have one.'),
    ).toBeInTheDocument();
    // The important assertion: nothing in the UI adds an enumeration-leaking
    // message (e.g. "already registered"/"already exists") on top of it.
    expect(screen.queryByText(/already registered/i)).not.toBeInTheDocument();
    expect(onAuthenticated).not.toHaveBeenCalled();
  });

  // REQ-717/ADR-0036: the guest entry point.
  it('REQ-717/ADR-0037: clicking "Play as guest" obtains a Turnstile token first, sends it as the request body, and routes through onAuthenticated exactly like a normal login', async () => {
    getTurnstileTokenMock.mockResolvedValue('turnstile-token-abc');
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      if (String(url).endsWith('/auth/guest')) {
        return jsonResponse({ accessToken: 'guest-token', refreshToken: 'guest-refresh' });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('button', { name: 'Play as guest' }));

    await waitFor(() => expect(onAuthenticated).toHaveBeenCalledWith('guest-token', 'guest-refresh'));
    expect(getTurnstileTokenMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/auth/guest'),
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ captchaToken: 'turnstile-token-abc' }),
      }),
    );
    expect(resetTurnstileWidgetMock).not.toHaveBeenCalled();
  });

  // Gap check (test-writer): guestSubmitting is set true before
  // getTurnstileToken() is ever awaited (AuthScreen.tsx's handlePlayAsGuest),
  // so the button must already show its loading/disabled state while a
  // real (potentially slow) script load/token acquisition is still in
  // flight -- not only once playAsGuest() itself starts. A never-resolving
  // getTurnstileToken() mock forces the assertion to happen strictly inside
  // that window.
  it('REQ-717: disables "Play as guest" and shows a loading label while still awaiting the Turnstile token, before calling POST /auth/guest at all', async () => {
    getTurnstileTokenMock.mockImplementation(() => new Promise<string>(() => {}));
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('button', { name: 'Play as guest' }));

    expect(await screen.findByRole('button', { name: 'Starting…' })).toBeDisabled();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(onAuthenticated).not.toHaveBeenCalled();
  });

  it('REQ-717: shows the server error detail when guest sign-in fails for a non-captcha reason, and does not reset the Turnstile widget', async () => {
    getTurnstileTokenMock.mockResolvedValue('turnstile-token-abc');
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse(
        { title: 'Guest sign-in failed', detail: 'Could not start a guest session. Please try again.' },
        500,
      ),
    );
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('button', { name: 'Play as guest' }));

    expect(
      await screen.findByText('Could not start a guest session. Please try again.'),
    ).toBeInTheDocument();
    expect(onAuthenticated).not.toHaveBeenCalled();
    expect(resetTurnstileWidgetMock).not.toHaveBeenCalled();
  });

  // REQ-717's 2026-07-21 "Bot-check (captcha)" addition / ADR-0037: the
  // backend's distinct captcha-rejection response (400, title "Captcha
  // verification failed") must reset the widget rather than being treated
  // like any other guest-sign-in failure — this is the behavior that
  // actually distinguishes the two failure modes, so it's the one worth a
  // dedicated test here even though exhaustive REQ-717 coverage is
  // test-writer's job next.
  it('REQ-717/ADR-0037: resets the Turnstile widget on the distinct captcha-rejection response, and shows its detail text', async () => {
    getTurnstileTokenMock.mockResolvedValue('turnstile-token-abc');
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse(
        {
          title: 'Captcha verification failed',
          detail: "Could not verify you're not a bot. Please try again.",
        },
        400,
      ),
    );
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('button', { name: 'Play as guest' }));

    expect(
      await screen.findByText("Could not verify you're not a bot. Please try again."),
    ).toBeInTheDocument();
    expect(onAuthenticated).not.toHaveBeenCalled();
    expect(resetTurnstileWidgetMock).toHaveBeenCalledTimes(1);
  });

  it('REQ-717/ADR-0037: never calls POST /auth/guest at all if obtaining a Turnstile token fails', async () => {
    getTurnstileTokenMock.mockRejectedValue(new Error('Failed to load the Turnstile verification script.'));
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('button', { name: 'Play as guest' }));

    expect(
      await screen.findByText('Failed to load the Turnstile verification script.'),
    ).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(onAuthenticated).not.toHaveBeenCalled();
    expect(resetTurnstileWidgetMock).not.toHaveBeenCalled();
  });

  // ---- REQ-701/REQ-717's 2026-07-25 scope-correction addition / ADR-0037's
  // amendment: captcha now covers login and signup too, mirroring the
  // guest-flow mechanism exactly. ----

  it('REQ-701: login obtains a Turnstile token once before calling login(), and sends it as the request body', async () => {
    getTurnstileTokenMock.mockResolvedValue('turnstile-token-login');
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse({ accessToken: 'token-abc', refreshToken: null }));
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.click(screen.getByRole('button', { name: 'Log in' }));

    await waitFor(() => expect(onAuthenticated).toHaveBeenCalledWith('token-abc', null));
    expect(getTurnstileTokenMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/auth/login'),
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ email: 'player@example.com', password: 'password123', captchaToken: 'turnstile-token-login' }),
      }),
    );
  });

  // AuthScreen.tsx's handleSubmit calls getTurnstileToken() twice for
  // signup -- once before signup() itself, and a fresh second call before
  // the follow-up auto-login's login() call (a Turnstile token is
  // single-use against Supabase's own verification, so the second call must
  // be a genuinely new invocation, not a reused value). This test pins that
  // down by resolving the mock with two distinct, ordered values.
  it('REQ-701: signup obtains two distinct Turnstile tokens -- one for signup(), a fresh one for the follow-up login()', async () => {
    getTurnstileTokenMock
      .mockResolvedValueOnce('turnstile-token-signup')
      .mockResolvedValueOnce('turnstile-token-followup-login');
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      if (String(url).endsWith('/auth/signup')) {
        return jsonResponse({ id: 'user-1', email: 'player@example.com', displayName: 'Player One' }, 201);
      }
      if (String(url).endsWith('/auth/login')) {
        return jsonResponse({ accessToken: 'token-abc', refreshToken: null });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('tab', { name: 'Sign up' }));
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.type(screen.getByLabelText('Confirm password'), 'password123');
    await user.type(screen.getByLabelText('Display name'), 'Player One');
    await user.click(screen.getByLabelText(/at least 16/));
    await user.click(screen.getByRole('button', { name: 'Create account' }));

    await waitFor(() => expect(onAuthenticated).toHaveBeenCalledWith('token-abc', null));
    // Two distinct calls, not one reused value.
    expect(getTurnstileTokenMock).toHaveBeenCalledTimes(2);
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/auth/signup'),
      expect.objectContaining({
        body: JSON.stringify({
          email: 'player@example.com',
          password: 'password123',
          confirmPassword: 'password123',
          displayName: 'Player One',
          ageConfirmed: true,
          captchaToken: 'turnstile-token-signup',
        }),
      }),
    );
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/auth/login'),
      expect.objectContaining({
        body: JSON.stringify({ email: 'player@example.com', password: 'password123', captchaToken: 'turnstile-token-followup-login' }),
      }),
    );
  });

  // REQ-701's 2026-07-25 addition: the single most important assertion for
  // this widened scope -- a captcha rejection (title === 'Captcha
  // verification failed') on login must reset the widget, exactly like the
  // pre-existing guest-flow behavior above.
  it('REQ-701: resets the Turnstile widget on a login captcha-rejection response, and shows its detail text', async () => {
    getTurnstileTokenMock.mockResolvedValue('turnstile-token-login');
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse(
        { title: 'Captcha verification failed', detail: "Could not verify you're not a bot. Please try again." },
        400,
      ),
    );
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.click(screen.getByRole('button', { name: 'Log in' }));

    expect(
      await screen.findByText("Could not verify you're not a bot. Please try again."),
    ).toBeInTheDocument();
    expect(onAuthenticated).not.toHaveBeenCalled();
    expect(resetTurnstileWidgetMock).toHaveBeenCalledTimes(1);
  });

  // Same assertion for signup -- any other login error (e.g. wrong
  // password) must NOT reset the widget, same as the pre-existing
  // "REQ-701: shows the server error detail on a failed login" case implies
  // but never asserted on resetTurnstileWidgetMock directly; this test
  // makes that explicit for the negative case.
  it('REQ-701: does not reset the Turnstile widget on an ordinary (non-captcha) login failure', async () => {
    getTurnstileTokenMock.mockResolvedValue('turnstile-token-login');
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse({ title: 'Login failed', detail: 'Invalid email or password.' }, 401),
    );
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AuthScreen onAuthenticated={vi.fn()} />);
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'wrongpassword');
    await user.click(screen.getByRole('button', { name: 'Log in' }));

    expect(await screen.findByText('Invalid email or password.')).toBeInTheDocument();
    expect(resetTurnstileWidgetMock).not.toHaveBeenCalled();
  });

  // REQ-701's 2026-07-25 addition, signup side: a captcha rejection must
  // reset the widget AND must never be swallowed by REQ-701's own
  // account-enumeration-safe generic fallback message -- the single most
  // important assertion in this whole fix, per the task's own framing,
  // exercised end-to-end through the real UI here (the backend-level
  // equivalent lives in AuthEndpointTests.cs).
  it('REQ-701: resets the Turnstile widget on a signup captcha-rejection response, and never shows the generic enumeration-safe fallback text', async () => {
    getTurnstileTokenMock.mockResolvedValue('turnstile-token-signup');
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      if (String(url).endsWith('/auth/signup')) {
        return jsonResponse(
          { title: 'Captcha verification failed', detail: "Could not verify you're not a bot. Please try again." },
          400,
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('tab', { name: 'Sign up' }));
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.type(screen.getByLabelText('Confirm password'), 'password123');
    await user.type(screen.getByLabelText('Display name'), 'Player One');
    await user.click(screen.getByLabelText(/at least 16/));
    await user.click(screen.getByRole('button', { name: 'Create account' }));

    expect(
      await screen.findByText("Could not verify you're not a bot. Please try again."),
    ).toBeInTheDocument();
    expect(
      screen.queryByText('Check your email to confirm your account, or reset your password if you already have one.'),
    ).not.toBeInTheDocument();
    expect(onAuthenticated).not.toHaveBeenCalled();
    expect(resetTurnstileWidgetMock).toHaveBeenCalledTimes(1);
    // Only the first (signup) token was ever obtained -- the rejection
    // short-circuits before the follow-up login's own getTurnstileToken()
    // call.
    expect(getTurnstileTokenMock).toHaveBeenCalledTimes(1);
  });

  // Sign-in latency fix (2026-07-25): the signup flow's second, follow-up-
  // login getTurnstileToken() call now shows an explicit status line while
  // it's in flight -- this is the "don't leave the second visible-checkbox
  // render looking glitchy/unexplained" requirement, asserted directly.
  it('REQ-701: shows "Verifying again to log you in…" only during signup\'s second, follow-up-login Turnstile render', async () => {
    let resolveSecondToken: (token: string) => void = () => {};
    getTurnstileTokenMock
      .mockResolvedValueOnce('turnstile-token-signup')
      .mockImplementationOnce(() => new Promise<string>((resolve) => (resolveSecondToken = resolve)));
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      if (String(url).endsWith('/auth/signup')) {
        return jsonResponse({ id: 'user-1', email: 'player@example.com', displayName: 'Player One' }, 201);
      }
      if (String(url).endsWith('/auth/login')) {
        return jsonResponse({ accessToken: 'token-abc', refreshToken: null });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('tab', { name: 'Sign up' }));
    await user.type(screen.getByLabelText('Email'), 'player@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.type(screen.getByLabelText('Confirm password'), 'password123');
    await user.type(screen.getByLabelText('Display name'), 'Player One');
    await user.click(screen.getByLabelText(/at least 16/));

    expect(screen.queryByText('Verifying again to log you in…')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Create account' }));

    expect(await screen.findByText('Verifying again to log you in…')).toBeInTheDocument();

    resolveSecondToken('turnstile-token-followup-login');
    await waitFor(() => expect(onAuthenticated).toHaveBeenCalledWith('token-abc', null));
    expect(screen.queryByText('Verifying again to log you in…')).not.toBeInTheDocument();
  });

  // Same negative case as the login one above, signup side: an ordinary
  // (non-captcha) signup rejection must not reset the widget.
  it('REQ-701: does not reset the Turnstile widget on an ordinary (non-captcha) signup failure', async () => {
    getTurnstileTokenMock.mockResolvedValue('turnstile-token-signup');
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      if (String(url).endsWith('/auth/signup')) {
        return jsonResponse(
          {
            title: 'Signup could not be completed',
            detail: 'Check your email to confirm your account, or reset your password if you already have one.',
          },
          400,
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAuthenticated = vi.fn();

    render(<AuthScreen onAuthenticated={onAuthenticated} />);
    await user.click(screen.getByRole('tab', { name: 'Sign up' }));
    await user.type(screen.getByLabelText('Email'), 'already-has-an-account@example.com');
    await user.type(screen.getByLabelText('Password'), 'password123');
    await user.type(screen.getByLabelText('Confirm password'), 'password123');
    await user.type(screen.getByLabelText('Display name'), 'Player One');
    await user.click(screen.getByLabelText(/at least 16/));
    await user.click(screen.getByRole('button', { name: 'Create account' }));

    expect(
      await screen.findByText('Check your email to confirm your account, or reset your password if you already have one.'),
    ).toBeInTheDocument();
    expect(onAuthenticated).not.toHaveBeenCalled();
    expect(resetTurnstileWidgetMock).not.toHaveBeenCalled();
  });
});

import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuthScreen } from './AuthScreen';

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
  it('REQ-717: clicking "Play as guest" calls POST /auth/guest with no body, and routes through onAuthenticated exactly like a normal login', async () => {
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
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/auth/guest'),
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('REQ-717: shows the server error detail when guest sign-in fails, rather than a generic message', async () => {
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
  });
});

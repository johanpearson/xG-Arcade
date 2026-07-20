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
});

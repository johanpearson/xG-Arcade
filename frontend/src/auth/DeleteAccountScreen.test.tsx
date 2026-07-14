import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DeleteAccountScreen } from './DeleteAccountScreen';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

// REQ-710 (S-039): the frontend confirmation step for account deletion —
// the actual re-verification happens server-side (DELETE /auth/account),
// this screen just carries the password through and reacts to the result.
describe('DeleteAccountScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-710: shows the irreversibility warning as an alert', () => {
    vi.stubGlobal('fetch', vi.fn());

    render(
      <DeleteAccountScreen
        accessToken="token"
        onAccountDeleted={vi.fn()}
        onCancel={vi.fn()}
        onAuthError={vi.fn()}
      />,
    );

    const alerts = screen.getAllByRole('alert');
    expect(
      alerts.some((alert) =>
        alert.textContent?.includes('This permanently deletes your account. It cannot be undone.'),
      ),
    ).toBe(true);
  });

  it('REQ-710: clicking Cancel calls onCancel without calling deleteAccount or fetch at all', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onCancel = vi.fn();

    render(
      <DeleteAccountScreen
        accessToken="token"
        onAccountDeleted={vi.fn()}
        onCancel={onCancel}
        onAuthError={vi.fn()}
      />,
    );
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(onCancel).toHaveBeenCalledTimes(1);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('REQ-710: submitting the correct password deletes the account and calls onAccountDeleted, not onAuthError', async () => {
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse(null, 204));
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAccountDeleted = vi.fn();
    const onAuthError = vi.fn();

    render(
      <DeleteAccountScreen
        accessToken="token-abc"
        onAccountDeleted={onAccountDeleted}
        onCancel={vi.fn()}
        onAuthError={onAuthError}
      />,
    );
    await user.type(screen.getByLabelText('Current password'), 'correct-password');
    await user.click(screen.getByRole('button', { name: 'Delete my account permanently' }));

    await waitFor(() => expect(onAccountDeleted).toHaveBeenCalledTimes(1));
    expect(onAuthError).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/auth/account'),
      expect.objectContaining({
        method: 'DELETE',
        headers: expect.objectContaining({ Authorization: 'Bearer token-abc' }),
        body: JSON.stringify({ password: 'correct-password' }),
      }),
    );
  });

  it('REQ-710: a wrong password (401 "Incorrect password") shows the inline error, deletes nothing, and leaves the form usable again', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(() =>
        jsonResponse({ title: 'Incorrect password', detail: 'The password you entered is incorrect.' }, 401),
      );
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAccountDeleted = vi.fn();
    const onAuthError = vi.fn();

    render(
      <DeleteAccountScreen
        accessToken="token"
        onAccountDeleted={onAccountDeleted}
        onCancel={vi.fn()}
        onAuthError={onAuthError}
      />,
    );
    await user.type(screen.getByLabelText('Current password'), 'wrong-password');
    const submitButton = screen.getByRole('button', { name: 'Delete my account permanently' });
    await user.click(submitButton);

    expect(await screen.findByText('The password you entered is incorrect.')).toBeInTheDocument();
    expect(onAccountDeleted).not.toHaveBeenCalled();
    expect(onAuthError).not.toHaveBeenCalled();
    // The form flips back to usable — not stuck showing "Deleting…".
    expect(screen.getByRole('button', { name: 'Delete my account permanently' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Delete my account permanently' })).not.toBeDisabled();
  });

  it('REQ-710: a 401 that is not "Incorrect password" (expired/invalid session) calls onAuthError instead of showing a password error', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401));
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAccountDeleted = vi.fn();
    const onAuthError = vi.fn();

    render(
      <DeleteAccountScreen
        accessToken="stale-token"
        onAccountDeleted={onAccountDeleted}
        onCancel={vi.fn()}
        onAuthError={onAuthError}
      />,
    );
    await user.type(screen.getByLabelText('Current password'), 'whatever-password');
    await user.click(screen.getByRole('button', { name: 'Delete my account permanently' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
    expect(onAccountDeleted).not.toHaveBeenCalled();
    expect(screen.queryByText('Session expired.')).not.toBeInTheDocument();
  });

  it('REQ-710: a 401 with no JSON body at all (title defaults to "Request failed") calls onAuthError, not the inline password error', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      Promise.resolve({
        ok: false,
        status: 401,
        json: () => Promise.reject(new Error('no body')),
      } as unknown as Response),
    );
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const onAccountDeleted = vi.fn();
    const onAuthError = vi.fn();

    render(
      <DeleteAccountScreen
        accessToken="stale-token"
        onAccountDeleted={onAccountDeleted}
        onCancel={vi.fn()}
        onAuthError={onAuthError}
      />,
    );
    await user.type(screen.getByLabelText('Current password'), 'whatever-password');
    await user.click(screen.getByRole('button', { name: 'Delete my account permanently' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
    expect(onAccountDeleted).not.toHaveBeenCalled();
  });

  it('REQ-710: shows "Deleting…" on the submit button while the request is in flight', async () => {
    let resolveFetch: (value: Response) => void = () => {};
    const fetchMock = vi.fn().mockImplementation(
      () =>
        new Promise<Response>((resolve) => {
          resolveFetch = resolve;
        }),
    );
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(
      <DeleteAccountScreen
        accessToken="token"
        onAccountDeleted={vi.fn()}
        onCancel={vi.fn()}
        onAuthError={vi.fn()}
      />,
    );
    await user.type(screen.getByLabelText('Current password'), 'correct-password');
    await user.click(screen.getByRole('button', { name: 'Delete my account permanently' }));

    expect(await screen.findByRole('button', { name: 'Deleting…' })).toBeInTheDocument();

    resolveFetch({ ok: true, status: 204, json: () => Promise.resolve(null) } as Response);
  });
});

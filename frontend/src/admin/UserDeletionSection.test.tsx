import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { UserDeletionSection } from './UserDeletionSection';

// S-156 (docs/backlog.md): dedicated isolation coverage for
// UserDeletionSection, extracted from AdminScreen.tsx by S-103 with no
// behavior change. Mirrors AdminScreen.test.tsx's former REQ-506 assertions
// (now removed there as redundant) — renders the component directly, only
// /admin/users needs stubbing here.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function bareNotFound() {
  return Promise.resolve({
    ok: false,
    status: 404,
    json: () => Promise.reject(new Error('no body')),
  } as unknown as Response);
}

describe('UserDeletionSection', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-506: renders "Delete a user" with an Email field, and the delete button starts disabled', () => {
    render(<UserDeletionSection accessToken="token" onAuthError={vi.fn()} />);

    expect(screen.getByText('Delete a user')).toBeInTheDocument();
    expect(screen.getByLabelText('Email')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Delete user' })).toBeDisabled();
  });

  it('REQ-506: typing an email enables "Delete user"', async () => {
    const user = userEvent.setup();
    render(<UserDeletionSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Email'), 'test@example.com');

    expect(screen.getByRole('button', { name: 'Delete user' })).toBeEnabled();
  });

  it('REQ-506: "Delete user" requires a second, explicit confirm click before calling the delete endpoint', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      Promise.resolve({ ok: true, status: 204, json: () => Promise.resolve(null) } as Response),
    );
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<UserDeletionSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Email'), 'test@example.com');
    await user.click(screen.getByRole('button', { name: 'Delete user' }));
    expect(fetchMock).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Yes, delete this user permanently' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Yes, delete this user permanently' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/admin/users?email=test%40example.com'),
        expect.objectContaining({ method: 'DELETE' }),
      ),
    );
    expect(await screen.findByText('Deleted.')).toBeInTheDocument();
  });

  it('REQ-506: "Cancel" during the confirm step returns to idle without calling the delete endpoint', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<UserDeletionSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Email'), 'test@example.com');
    await user.click(screen.getByRole('button', { name: 'Delete user' }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.getByRole('button', { name: 'Delete user' })).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('REQ-506: deleting a user with no match shows "No user found with that email." inline', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => bareNotFound()));
    const user = userEvent.setup();

    render(<UserDeletionSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Email'), 'nobody@example.com');
    await user.click(screen.getByRole('button', { name: 'Delete user' }));
    await user.click(screen.getByRole('button', { name: 'Yes, delete this user permanently' }));

    expect(await screen.findByText('No user found with that email.')).toBeInTheDocument();
    expect(screen.queryByText('Deleted.')).not.toBeInTheDocument();
  });

  it('REQ-506: a 401 while deleting calls onAuthError', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );
    const user = userEvent.setup();

    render(<UserDeletionSection accessToken="token" onAuthError={onAuthError} />);

    await user.type(screen.getByLabelText('Email'), 'test@example.com');
    await user.click(screen.getByRole('button', { name: 'Delete user' }));
    await user.click(screen.getByRole('button', { name: 'Yes, delete this user permanently' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-506: a non-401/404 error (e.g. 500) shows an inline error message', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Server error', detail: 'Delete failed unexpectedly.' }, 500)),
    );
    const user = userEvent.setup();

    render(<UserDeletionSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Email'), 'test@example.com');
    await user.click(screen.getByRole('button', { name: 'Delete user' }));
    await user.click(screen.getByRole('button', { name: 'Yes, delete this user permanently' }));

    expect(await screen.findByText('Delete failed unexpectedly.')).toBeInTheDocument();
  });

  it('REQ-506: editing the email after a result clears both the message and the error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => bareNotFound()));
    const user = userEvent.setup();

    render(<UserDeletionSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Email'), 'nobody@example.com');
    await user.click(screen.getByRole('button', { name: 'Delete user' }));
    await user.click(screen.getByRole('button', { name: 'Yes, delete this user permanently' }));
    await screen.findByText('No user found with that email.');

    await user.type(screen.getByLabelText('Email'), 'x');

    expect(screen.queryByText('No user found with that email.')).not.toBeInTheDocument();
  });
});

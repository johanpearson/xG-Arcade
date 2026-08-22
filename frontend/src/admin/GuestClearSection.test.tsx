import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { GuestClearSection } from './GuestClearSection';

// S-156 (docs/backlog.md): dedicated isolation coverage for GuestClearSection,
// extracted from AdminScreen.tsx by S-103 with no behavior change and
// composed by AccountMetricsSection (S-108) as a real child ever since —
// this file renders it directly with its own props (accessToken/onAuthError/
// onCleared) rather than through either parent, mirroring
// AdminScreen.test.tsx's former REQ-508 assertions (now removed there as
// redundant). Only /admin/accounts/guests/* routes need stubbing here.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

describe('GuestClearSection', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-508: renders the "Guest accounts" heading and the "Force clear guests" button with no fetch on mount', () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);

    render(<GuestClearSection accessToken="token" onAuthError={vi.fn()} onCleared={vi.fn()} />);

    expect(screen.getByText('Guest accounts')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Force clear guests' })).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('REQ-508: "Force clear guests" shows the dry-run count in the confirm prompt, and only calls the clear endpoint after confirming', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/accounts/guests/count')) return jsonResponse({ count: 5 });
      if (path.includes('/admin/accounts/guests/clear')) {
        return jsonResponse({ results: [{ userId: 'guest-1', outcome: 'Succeeded', errorMessage: null }] });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<GuestClearSection accessToken="token" onAuthError={vi.fn()} onCleared={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));
    expect(await screen.findByRole('button', { name: 'Yes, delete all 5 guest accounts' })).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalledWith(
      expect.stringContaining('/admin/accounts/guests/clear'),
      expect.anything(),
    );

    await user.click(screen.getByRole('button', { name: 'Yes, delete all 5 guest accounts' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/admin/accounts/guests/clear'),
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    expect(await screen.findByText('guest-1 — Cleared.')).toBeInTheDocument();
  });

  it('REQ-508: singular phrasing ("1 guest account") when the dry-run count is exactly 1', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse({ count: 1 })));
    const user = userEvent.setup();

    render(<GuestClearSection accessToken="token" onAuthError={vi.fn()} onCleared={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));

    expect(await screen.findByRole('button', { name: 'Yes, delete all 1 guest account' })).toBeInTheDocument();
  });

  it('REQ-508: "Cancel" during the confirm step returns to idle without calling the clear endpoint', async () => {
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse({ count: 5 }));
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<GuestClearSection accessToken="token" onAuthError={vi.fn()} onCleared={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));
    await screen.findByRole('button', { name: 'Yes, delete all 5 guest accounts' });
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.getByRole('button', { name: 'Force clear guests' })).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalledWith(
      expect.stringContaining('/admin/accounts/guests/clear'),
      expect.anything(),
    );
  });

  it('REQ-508: a zero dry-run count shows an inline message instead of a confirm prompt', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse({ count: 0 })));
    const user = userEvent.setup();

    render(<GuestClearSection accessToken="token" onAuthError={vi.fn()} onCleared={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));

    expect(await screen.findByText('No guest accounts to clear right now.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Yes, delete all/ })).not.toBeInTheDocument();
  });

  it('REQ-508: a partial-outcome clear shows Succeeded/NotFound/Failed distinctly, using the server error message when Failed', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/accounts/guests/count')) return jsonResponse({ count: 3 });
      if (path.includes('/admin/accounts/guests/clear')) {
        return jsonResponse({
          results: [
            { userId: 'guest-1', outcome: 'Succeeded', errorMessage: null },
            { userId: 'guest-2', outcome: 'NotFound', errorMessage: null },
            { userId: 'guest-3', outcome: 'Failed', errorMessage: 'Supabase delete failed.' },
          ],
        });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<GuestClearSection accessToken="token" onAuthError={vi.fn()} onCleared={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));
    await user.click(await screen.findByRole('button', { name: 'Yes, delete all 3 guest accounts' }));

    expect(await screen.findByText('guest-1 — Cleared.')).toBeInTheDocument();
    expect(screen.getByText('guest-2 — Not cleared — this account no longer exists.')).toBeInTheDocument();
    expect(screen.getByText('guest-3 — Not cleared — Supabase delete failed.')).toBeInTheDocument();
  });

  it('REQ-508: a Failed outcome with no errorMessage falls back to a generic "Not cleared."', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url: string) => {
        const path = String(url);
        if (path.includes('/admin/accounts/guests/count')) return jsonResponse({ count: 1 });
        if (path.includes('/admin/accounts/guests/clear')) {
          return jsonResponse({ results: [{ userId: 'guest-1', outcome: 'Failed', errorMessage: null }] });
        }
        throw new Error(`Unexpected fetch: ${path}`);
      }),
    );
    const user = userEvent.setup();

    render(<GuestClearSection accessToken="token" onAuthError={vi.fn()} onCleared={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));
    await user.click(await screen.findByRole('button', { name: 'Yes, delete all 1 guest account' }));

    expect(await screen.findByText('guest-1 — Not cleared.')).toBeInTheDocument();
  });

  it('REQ-508: a successful clear calls onCleared exactly once', async () => {
    const onCleared = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url: string) => {
        const path = String(url);
        if (path.includes('/admin/accounts/guests/count')) return jsonResponse({ count: 1 });
        if (path.includes('/admin/accounts/guests/clear')) {
          return jsonResponse({ results: [{ userId: 'guest-1', outcome: 'Succeeded', errorMessage: null }] });
        }
        throw new Error(`Unexpected fetch: ${path}`);
      }),
    );
    const user = userEvent.setup();

    render(<GuestClearSection accessToken="token" onAuthError={vi.fn()} onCleared={onCleared} />);

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));
    await user.click(await screen.findByRole('button', { name: 'Yes, delete all 1 guest account' }));

    await waitFor(() => expect(onCleared).toHaveBeenCalledTimes(1));
  });

  it('REQ-508: "Dismiss" clears the results list', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url: string) => {
        const path = String(url);
        if (path.includes('/admin/accounts/guests/count')) return jsonResponse({ count: 1 });
        if (path.includes('/admin/accounts/guests/clear')) {
          return jsonResponse({ results: [{ userId: 'guest-1', outcome: 'Succeeded', errorMessage: null }] });
        }
        throw new Error(`Unexpected fetch: ${path}`);
      }),
    );
    const user = userEvent.setup();

    render(<GuestClearSection accessToken="token" onAuthError={vi.fn()} onCleared={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));
    await user.click(await screen.findByRole('button', { name: 'Yes, delete all 1 guest account' }));
    await screen.findByText('guest-1 — Cleared.');

    await user.click(screen.getByRole('button', { name: 'Dismiss' }));

    expect(screen.queryByText('guest-1 — Cleared.')).not.toBeInTheDocument();
  });

  it('REQ-508: a 401 while checking the dry-run count calls onAuthError', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );

    const user = userEvent.setup();
    render(<GuestClearSection accessToken="token" onAuthError={onAuthError} onCleared={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-508: a non-401 error checking the dry-run count shows an inline error without leaving the counting state stuck', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Server error', detail: 'Something broke.' }, 500)),
    );
    const user = userEvent.setup();

    render(<GuestClearSection accessToken="token" onAuthError={vi.fn()} onCleared={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));

    expect(await screen.findByText('Something broke.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Force clear guests' })).toBeInTheDocument();
  });

  it('REQ-508: a 401 while confirming the clear itself calls onAuthError', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url: string) => {
        const path = String(url);
        if (path.includes('/admin/accounts/guests/count')) return jsonResponse({ count: 2 });
        if (path.includes('/admin/accounts/guests/clear')) {
          return jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401);
        }
        throw new Error(`Unexpected fetch: ${path}`);
      }),
    );
    const user = userEvent.setup();

    render(<GuestClearSection accessToken="token" onAuthError={onAuthError} onCleared={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));
    await user.click(await screen.findByRole('button', { name: 'Yes, delete all 2 guest accounts' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-508: a non-401 error confirming the clear shows an inline error and keeps the confirm prompt (rather than reverting to idle)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url: string) => {
        const path = String(url);
        if (path.includes('/admin/accounts/guests/count')) return jsonResponse({ count: 2 });
        if (path.includes('/admin/accounts/guests/clear')) {
          return jsonResponse({ title: 'Server error', detail: 'Clear failed unexpectedly.' }, 500);
        }
        throw new Error(`Unexpected fetch: ${path}`);
      }),
    );
    const user = userEvent.setup();

    render(<GuestClearSection accessToken="token" onAuthError={vi.fn()} onCleared={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));
    await user.click(await screen.findByRole('button', { name: 'Yes, delete all 2 guest accounts' }));

    expect(await screen.findByText('Clear failed unexpectedly.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Yes, delete all 2 guest accounts' })).toBeInTheDocument();
  });
});

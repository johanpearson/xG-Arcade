import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AccountMetricsSection } from './AccountMetricsSection';

// S-108 (docs/backlog.md): dedicated isolation coverage for
// AccountMetricsSection, extracted from AdminScreen.tsx by S-103 with no
// behavior change. Mirrors AdminScreen.test.tsx's REQ-507/REQ-508
// assertions, but renders the component directly. AccountMetricsSection
// composes GuestClearSection as a real child (not mocked, matching how
// AdminScreen itself renders it), so its own /admin/accounts/guests/* routes
// are stubbed here too wherever a test exercises that flow.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function stubFetch(routes: Record<string, () => Promise<Response>>) {
  vi.stubGlobal(
    'fetch',
    vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      const match = Object.entries(routes).find(([suffix]) => path.includes(suffix));
      if (match) return match[1]();
      throw new Error(`Unexpected fetch: ${path}`);
    }),
  );
}

describe('AccountMetricsSection', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-507: renders total/current/claimed guest counts from the metrics endpoint', async () => {
    stubFetch({
      '/admin/accounts/metrics': () =>
        jsonResponse({ totalUserCount: 42, currentGuestCount: 7, claimedGuestCount: 3 }),
    });

    render(<AccountMetricsSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Accounts')).toBeInTheDocument();
    expect(await screen.findByText('Total users')).toBeInTheDocument();
    expect(screen.getByText('42')).toBeInTheDocument();
    expect(screen.getByText('Current guests')).toBeInTheDocument();
    expect(screen.getByText('7')).toBeInTheDocument();
    expect(screen.getByText('Claimed guests')).toBeInTheDocument();
    expect(screen.getByText('3')).toBeInTheDocument();
  });

  it('REQ-507: shows a loading message before the metrics fetch resolves', () => {
    stubFetch({
      '/admin/accounts/metrics': () => new Promise(() => {}),
    });

    render(<AccountMetricsSection accessToken="token" onAuthError={vi.fn()} />);

    expect(screen.getByText('Loading account metrics…')).toBeInTheDocument();
  });

  it('REQ-507: a 403 from the metrics endpoint hides both the Accounts and Guest accounts sections', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/accounts/metrics': () => jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403),
    });

    const { container } = render(<AccountMetricsSection accessToken="token" onAuthError={onAuthError} />);

    await waitFor(() => expect(container).toBeEmptyDOMElement());
    expect(onAuthError).not.toHaveBeenCalled();
  });

  it('REQ-507: a 401 from the metrics endpoint calls onAuthError', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/accounts/metrics': () => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401),
    });

    render(<AccountMetricsSection accessToken="token" onAuthError={onAuthError} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-507: a non-401/403 error shows an inline error message rather than the metrics list', async () => {
    stubFetch({
      '/admin/accounts/metrics': () => jsonResponse({ title: 'Server error', detail: 'Something broke.' }, 500),
    });

    render(<AccountMetricsSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Something broke.')).toBeInTheDocument();
    expect(screen.queryByText('Total users')).not.toBeInTheDocument();
  });

  it('REQ-508: renders the "Guest accounts" section alongside the metrics section', async () => {
    stubFetch({
      '/admin/accounts/metrics': () =>
        jsonResponse({ totalUserCount: 10, currentGuestCount: 5, claimedGuestCount: 1 }),
    });

    render(<AccountMetricsSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Guest accounts')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Force clear guests' })).toBeInTheDocument();
  });

  it('REQ-508: "Force clear guests" shows the dry-run count, and a successful clear refreshes the account metrics', async () => {
    let metricsCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/accounts/metrics')) {
        metricsCallCount += 1;
        return jsonResponse({
          totalUserCount: 10,
          currentGuestCount: metricsCallCount === 1 ? 5 : 0,
          claimedGuestCount: 1,
        });
      }
      if (path.includes('/admin/accounts/guests/count')) return jsonResponse({ count: 5 });
      if (path.includes('/admin/accounts/guests/clear')) {
        return jsonResponse({
          results: [{ userId: 'guest-1', outcome: 'Succeeded', errorMessage: null }],
        });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AccountMetricsSection accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByText('5');

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));
    expect(await screen.findByRole('button', { name: 'Yes, delete all 5 guest accounts' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Yes, delete all 5 guest accounts' }));

    await screen.findByText('guest-1 — Cleared.');
    await waitFor(() => expect(screen.getByText('Current guests').nextSibling?.textContent).toBe('0'));
  });
});

import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { IncidentReportsEntry } from './IncidentReportsEntry';

// S-108 (docs/backlog.md): dedicated isolation coverage for
// IncidentReportsEntry, extracted from AdminScreen.tsx by S-103 with no
// behavior change. Mirrors AdminScreen.test.tsx's REQ-904 assertions, but
// renders the component directly — only /admin/incident-reports needs
// stubbing here.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function incidentReportsResponse(openCount: number) {
  return {
    available: true,
    openCount,
    issues: Array.from({ length: openCount }, (_, i) => ({
      number: i + 1,
      title: `Issue ${i + 1}`,
      url: `https://github.com/johanpearson/xg-arcade/issues/${i + 1}`,
    })),
  };
}

describe('IncidentReportsEntry', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-904: shows plain "Incident reports" with no count and no GitHub link when zero issues are open', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse(incidentReportsResponse(0))));

    render(<IncidentReportsEntry accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: 'Incident reports' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'View open reports on GitHub' })).not.toBeInTheDocument();
    expect(
      screen.queryByText(
        "Couldn't check GitHub for open incident reports right now — this doesn't mean there are none, try reloading in a minute.",
      ),
    ).not.toBeInTheDocument();
  });

  it('REQ-904: shows "Incident reports (3)" and a "View open reports on GitHub" link when 3 issues are open', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse(incidentReportsResponse(3))));

    render(<IncidentReportsEntry accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: 'Incident reports (3)' })).toBeInTheDocument();
    const link = screen.getByRole('link', { name: 'View open reports on GitHub' });
    expect(link).toHaveAttribute(
      'href',
      'https://github.com/johanpearson/xg-arcade/issues?q=is%3Aissue+is%3Aopen+label%3Auser-reported',
    );
    expect(link).toHaveAttribute('target', '_blank');
  });

  it('REQ-904: shows a distinct "unavailable" message (never the zero-count rendering) when available is false', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ available: false, openCount: 0, issues: [] })),
    );

    render(<IncidentReportsEntry accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: 'Incident reports' })).toBeInTheDocument();
    expect(
      await screen.findByText(
        "Couldn't check GitHub for open incident reports right now — this doesn't mean there are none, try reloading in a minute.",
      ),
    ).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'View open reports on GitHub' })).not.toBeInTheDocument();
  });

  it('REQ-904: shows an inline error (not the "unavailable" message) on a non-401/403 fetch failure', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Server error', detail: 'Something broke.' }, 500)),
    );

    render(<IncidentReportsEntry accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: 'Incident reports' })).toBeInTheDocument();
    expect(await screen.findByText('Something broke.')).toBeInTheDocument();
    expect(
      screen.queryByText(
        "Couldn't check GitHub for open incident reports right now — this doesn't mean there are none, try reloading in a minute.",
      ),
    ).not.toBeInTheDocument();
  });

  it('REQ-904: shows an inline error on a network failure, describing the underlying error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => Promise.reject(new Error('Network request failed'))));

    render(<IncidentReportsEntry accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Network request failed')).toBeInTheDocument();
  });

  it('REQ-904: hides the entry entirely on a 403 (non-admin), with no error banner', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403)),
    );

    const { container } = render(<IncidentReportsEntry accessToken="token" onAuthError={onAuthError} />);

    await waitFor(() => expect(container).toBeEmptyDOMElement());
    expect(onAuthError).not.toHaveBeenCalled();
  });

  it('REQ-904: a 401 calls onAuthError', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );

    render(<IncidentReportsEntry accessToken="token" onAuthError={onAuthError} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });
});

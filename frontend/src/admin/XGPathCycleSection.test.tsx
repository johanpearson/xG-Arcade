import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { XGPathCycleSection } from './XGPathCycleSection';

// S-108 (docs/backlog.md): dedicated isolation coverage for
// XGPathCycleSection, extracted from AdminScreen.tsx by S-103 with no
// behavior change. Mirrors AdminScreen.test.tsx's REQ-1209 assertions, but
// renders the component directly — only /admin/xg-path/cycle needs stubbing
// here.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

describe('XGPathCycleSection', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1209: renders the current cycle number, pool size, used/remaining counts, and last-completion time from a successful fetch', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          hasData: true,
          cycleNumber: 3,
          observedPoolSize: 42,
          usedInCycleCount: 17,
          remainingInCycleCount: 25,
          lastCycleCompletedAt: '2026-08-01T09:30:00Z',
        }),
      ),
    );

    render(<XGPathCycleSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('xG Path target cycle')).toBeInTheDocument();
    expect(screen.getByText('Current cycle')).toBeInTheDocument();
    expect(screen.getByText('3')).toBeInTheDocument();
    expect(screen.getByText('Eligible pool size (as of last generation)')).toBeInTheDocument();
    expect(screen.getByText('42')).toBeInTheDocument();
    expect(screen.getByText('Used this cycle')).toBeInTheDocument();
    expect(screen.getByText('17')).toBeInTheDocument();
    expect(screen.getByText('Remaining this cycle')).toBeInTheDocument();
    expect(screen.getByText('25')).toBeInTheDocument();
    expect(screen.getByText('Last cycle completed')).toBeInTheDocument();
    expect(screen.getByText('2026-08-01T09:30:00Z')).toBeInTheDocument();
  });

  it('REQ-1209: shows a loading message before the cycle fetch resolves', () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => new Promise(() => {})));

    render(<XGPathCycleSection accessToken="token" onAuthError={vi.fn()} />);

    expect(screen.getByText('Loading xG Path cycle status…')).toBeInTheDocument();
  });

  it('REQ-1209: shows the pre-first-generation "no data yet" state when hasData is false, never an error and never a blank section', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          hasData: false,
          cycleNumber: null,
          observedPoolSize: null,
          usedInCycleCount: null,
          remainingInCycleCount: null,
          lastCycleCompletedAt: null,
        }),
      ),
    );

    render(<XGPathCycleSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('xG Path target cycle')).toBeInTheDocument();
    expect(
      await screen.findByText('No xG Path round has generated yet — no cycle data to show.'),
    ).toBeInTheDocument();
    expect(screen.queryByText('Current cycle')).not.toBeInTheDocument();
  });

  it('REQ-1209: renders "No cycle has completed yet" when lastCycleCompletedAt is null but a cycle is otherwise in progress', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          hasData: true,
          cycleNumber: 1,
          observedPoolSize: 12,
          usedInCycleCount: 4,
          remainingInCycleCount: 8,
          lastCycleCompletedAt: null,
        }),
      ),
    );

    render(<XGPathCycleSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('No cycle has completed yet')).toBeInTheDocument();
  });

  it('REQ-1209: a 401 from the cycle endpoint calls onAuthError', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );

    render(<XGPathCycleSection accessToken="token" onAuthError={onAuthError} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-1209: a 403 from the cycle endpoint hides the section entirely', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403)),
    );

    const { container } = render(<XGPathCycleSection accessToken="token" onAuthError={onAuthError} />);

    await waitFor(() => expect(container).toBeEmptyDOMElement());
    expect(onAuthError).not.toHaveBeenCalled();
  });

  it('REQ-1209: a non-401/403 error from the cycle endpoint shows an inline error message, not the metrics list', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Server error', detail: 'Something broke.' }, 500)),
    );

    render(<XGPathCycleSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('xG Path target cycle')).toBeInTheDocument();
    expect(await screen.findByText('Something broke.')).toBeInTheDocument();
    expect(screen.queryByText('Current cycle')).not.toBeInTheDocument();
  });
});

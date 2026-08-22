import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { UnverifiedDataSection } from './UnverifiedDataSection';
import type { UnverifiedPlayerData } from '../lib/types';

// S-156 (docs/backlog.md): dedicated isolation coverage for
// UnverifiedDataSection, extracted from AdminScreen.tsx by S-103 with no
// behavior change. Mirrors AdminScreen.test.tsx's former REQ-501/502/503
// assertions (now removed there as redundant, apart from the two cases that
// genuinely test AdminScreen's own fetch-and-refetch wiring). Renders the
// component directly with `rows` supplied as a prop (never fetched by this
// component itself) — only /admin/player-overrides and
// /admin/player-data/{approve,remove} action routes need stubbing here.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

const row1: UnverifiedPlayerData = {
  id: 'row-1',
  playerId: 'player-1',
  playerFullName: 'Henry',
  field: 'nationality',
  value: 'France',
  source: 'live_lookup',
  confidence: 'unverified',
  syncedAt: '2026-07-01T00:00:00Z',
};

const row2: UnverifiedPlayerData = {
  id: 'row-2',
  playerId: 'player-2',
  playerFullName: 'Mbappe',
  field: 'club',
  value: 'PSG',
  source: 'wikidata',
  confidence: 'unverified',
  syncedAt: '2026-07-02T00:00:00Z',
};

describe('UnverifiedDataSection', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-503: renders each row as "name · field · value · source" with the row count in the heading', () => {
    render(
      <UnverifiedDataSection accessToken="token" rows={[row1, row2]} onAuthError={vi.fn()} onRefresh={vi.fn()} />,
    );

    expect(screen.getByText('Unverified data (2)')).toBeInTheDocument();
    expect(screen.getByText('Henry · nationality · France · live_lookup')).toBeInTheDocument();
    expect(screen.getByText('Mbappe · club · PSG · wikidata')).toBeInTheDocument();
  });

  it('REQ-503: shows "No unverified data to review." when rows is empty', () => {
    render(<UnverifiedDataSection accessToken="token" rows={[]} onAuthError={vi.fn()} onRefresh={vi.fn()} />);

    expect(screen.getByText('Unverified data (0)')).toBeInTheDocument();
    expect(screen.getByText('No unverified data to review.')).toBeInTheDocument();
  });

  it('REQ-501/503: "Correct" opens an inline form pre-filled with the row\'s value and an empty reason', async () => {
    const user = userEvent.setup();
    render(<UnverifiedDataSection accessToken="token" rows={[row1]} onAuthError={vi.fn()} onRefresh={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Correct' }));

    expect(screen.getByLabelText('Value')).toHaveValue('France');
    expect(screen.getByLabelText('Reason')).toHaveValue('');
  });

  it('REQ-501: a successful "Save correction" submits value/reason via POST and calls onRefresh', async () => {
    const onRefresh = vi.fn().mockResolvedValue(undefined);
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse(
        {
          id: 'override-1',
          playerId: 'player-1',
          field: 'nationality',
          value: 'Guadeloupe',
          reason: 'Wikidata correction',
          lockedByAdminId: 'admin-1',
          lockedAt: '2026-07-19T00:00:00Z',
        },
        201,
      ),
    );
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<UnverifiedDataSection accessToken="token" rows={[row1]} onAuthError={vi.fn()} onRefresh={onRefresh} />);

    await user.click(screen.getByRole('button', { name: 'Correct' }));
    await user.clear(screen.getByLabelText('Value'));
    await user.type(screen.getByLabelText('Value'), 'Guadeloupe');
    await user.type(screen.getByLabelText('Reason'), 'Wikidata correction');
    await user.click(screen.getByRole('button', { name: 'Save correction' }));

    await waitFor(() => {
      const call = fetchMock.mock.calls.find(([url]) => String(url).includes('/admin/player-overrides'));
      expect(call).toBeDefined();
      const body = JSON.parse((call![1] as RequestInit).body as string);
      expect(body).toEqual({ playerId: 'player-1', field: 'nationality', value: 'Guadeloupe', reason: 'Wikidata correction' });
    });
    await waitFor(() => expect(onRefresh).toHaveBeenCalledTimes(1));
  });

  it('REQ-501: "Cancel" closes the inline form without submitting', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<UnverifiedDataSection accessToken="token" rows={[row1]} onAuthError={vi.fn()} onRefresh={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Correct' }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByLabelText('Value')).not.toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('REQ-501: a 409 from creating an override is shown inline, without crashing or removing the row', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({ title: 'Conflict', detail: 'An override already exists for this field — use PUT to edit it.' }, 409),
      ),
    );
    const user = userEvent.setup();

    render(<UnverifiedDataSection accessToken="token" rows={[row1]} onAuthError={vi.fn()} onRefresh={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Correct' }));
    await user.type(screen.getByLabelText('Reason'), 'Wikidata correction');
    await user.click(screen.getByRole('button', { name: 'Save correction' }));

    expect(
      await screen.findByText('An override already exists for this field — use PUT to edit it.'),
    ).toBeInTheDocument();
    expect(screen.getByText('Henry · nationality · France · live_lookup')).toBeInTheDocument();
  });

  it('REQ-501: a 401 from creating an override calls onAuthError', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );
    const user = userEvent.setup();

    render(<UnverifiedDataSection accessToken="token" rows={[row1]} onAuthError={onAuthError} onRefresh={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'Correct' }));
    await user.type(screen.getByLabelText('Reason'), 'reason');
    await user.click(screen.getByRole('button', { name: 'Save correction' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-503: "Approve selected"/"Remove selected" are disabled and no "Reason" field exists when no rows are selected', () => {
    render(
      <UnverifiedDataSection accessToken="token" rows={[row1]} onAuthError={vi.fn()} onRefresh={vi.fn()} />,
    );

    expect(screen.getByRole('button', { name: 'Approve selected' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Remove selected' })).toBeDisabled();
    expect(screen.queryByLabelText('Reason')).not.toBeInTheDocument();
  });

  it('REQ-503: selecting a row updates the "selected" count and enables the bulk buttons, without opening "Correct"', async () => {
    const user = userEvent.setup();
    render(
      <UnverifiedDataSection accessToken="token" rows={[row1]} onAuthError={vi.fn()} onRefresh={vi.fn()} />,
    );

    await user.click(screen.getByRole('checkbox', { name: /Select Henry/ }));

    expect(screen.getByText('1 selected')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Approve selected' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Remove selected' })).toBeEnabled();
    expect(screen.queryByLabelText('Reason')).not.toBeInTheDocument();
  });

  it('REQ-503: "Select all" then "Approve selected" calls the approve endpoint with every id and no reason field, then calls onRefresh', async () => {
    const onRefresh = vi.fn().mockResolvedValue(undefined);
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/player-data/approve')) {
        return jsonResponse({
          results: [
            { playerDataId: row1.id, approved: true, failureReason: null },
            { playerDataId: row2.id, approved: true, failureReason: null },
          ],
        });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(
      <UnverifiedDataSection accessToken="token" rows={[row1, row2]} onAuthError={vi.fn()} onRefresh={onRefresh} />,
    );

    await user.click(screen.getByRole('checkbox', { name: 'Select all' }));
    expect(screen.getByText('2 selected')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Approve selected' }));

    await waitFor(() => {
      const call = fetchMock.mock.calls.find(([url]) => String(url).includes('/admin/player-data/approve'));
      expect(call).toBeDefined();
      const body = JSON.parse((call![1] as RequestInit).body as string);
      expect(body).toEqual({ playerDataIds: [row1.id, row2.id] });
    });
    await waitFor(() => expect(onRefresh).toHaveBeenCalledTimes(1));
    expect(screen.getByText('0 selected')).toBeInTheDocument();
  });

  it('REQ-503: approving a single row via its own checkbox calls the endpoint with just that id', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/player-data/approve')) {
        return jsonResponse({ results: [{ playerDataId: row1.id, approved: true, failureReason: null }] });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(
      <UnverifiedDataSection accessToken="token" rows={[row1, row2]} onAuthError={vi.fn()} onRefresh={vi.fn()} />,
    );

    await user.click(screen.getByRole('checkbox', { name: /Select Henry/ }));
    await user.click(screen.getByRole('button', { name: 'Approve selected' }));

    await waitFor(() => {
      const call = fetchMock.mock.calls.find(([url]) => String(url).includes('/admin/player-data/approve'));
      expect(call).toBeDefined();
      const body = JSON.parse((call![1] as RequestInit).body as string);
      expect(body).toEqual({ playerDataIds: [row1.id] });
    });
  });

  it('REQ-503: a partial-failure bulk approve shows which rows succeeded and which failed, distinctly', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          results: [
            { playerDataId: row1.id, approved: true, failureReason: null },
            { playerDataId: row2.id, approved: false, failureReason: 'NotUnverified' },
          ],
        }),
      ),
    );
    const user = userEvent.setup();

    render(
      <UnverifiedDataSection accessToken="token" rows={[row1, row2]} onAuthError={vi.fn()} onRefresh={vi.fn()} />,
    );

    await user.click(screen.getByRole('checkbox', { name: 'Select all' }));
    await user.click(screen.getByRole('button', { name: 'Approve selected' }));

    expect(await screen.findByText('Henry · nationality · France — Approved.')).toBeInTheDocument();
    expect(
      await screen.findByText('Mbappe · club · PSG — Not approved — already reviewed by someone else.'),
    ).toBeInTheDocument();
  });

  it('REQ-503: an approve failureReason of "NotFound" reads "no longer exists"', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({ results: [{ playerDataId: row1.id, approved: false, failureReason: 'NotFound' }] }),
      ),
    );
    const user = userEvent.setup();

    render(<UnverifiedDataSection accessToken="token" rows={[row1]} onAuthError={vi.fn()} onRefresh={vi.fn()} />);

    await user.click(screen.getByRole('checkbox', { name: /Select Henry/ }));
    await user.click(screen.getByRole('button', { name: 'Approve selected' }));

    expect(await screen.findByText('Henry · nationality · France — Not approved — this row no longer exists.')).toBeInTheDocument();
  });

  it('REQ-503: a 401 from bulk approve calls onAuthError', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );
    const user = userEvent.setup();

    render(<UnverifiedDataSection accessToken="token" rows={[row1]} onAuthError={onAuthError} onRefresh={vi.fn()} />);

    await user.click(screen.getByRole('checkbox', { name: /Select Henry/ }));
    await user.click(screen.getByRole('button', { name: 'Approve selected' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-503: "Select all" then "Remove selected" calls the remove endpoint with every id, then calls onRefresh', async () => {
    const onRefresh = vi.fn().mockResolvedValue(undefined);
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/player-data/remove')) {
        return jsonResponse({
          results: [
            { playerDataId: row1.id, removed: true, failureReason: null },
            { playerDataId: row2.id, removed: true, failureReason: null },
          ],
        });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(
      <UnverifiedDataSection accessToken="token" rows={[row1, row2]} onAuthError={vi.fn()} onRefresh={onRefresh} />,
    );

    await user.click(screen.getByRole('checkbox', { name: 'Select all' }));
    await user.click(screen.getByRole('button', { name: 'Remove selected' }));

    await waitFor(() => {
      const call = fetchMock.mock.calls.find(([url]) => String(url).includes('/admin/player-data/remove'));
      expect(call).toBeDefined();
      const body = JSON.parse((call![1] as RequestInit).body as string);
      expect(body).toEqual({ playerDataIds: [row1.id, row2.id] });
    });
    await waitFor(() => expect(onRefresh).toHaveBeenCalledTimes(1));
  });

  it('REQ-503: a partial-failure bulk remove shows which rows succeeded and which failed, distinctly', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          results: [
            { playerDataId: row1.id, removed: true, failureReason: null },
            { playerDataId: row2.id, removed: false, failureReason: 'NotFound' },
          ],
        }),
      ),
    );
    const user = userEvent.setup();

    render(
      <UnverifiedDataSection accessToken="token" rows={[row1, row2]} onAuthError={vi.fn()} onRefresh={vi.fn()} />,
    );

    await user.click(screen.getByRole('checkbox', { name: 'Select all' }));
    await user.click(screen.getByRole('button', { name: 'Remove selected' }));

    expect(await screen.findByText('Henry · nationality · France — Removed.')).toBeInTheDocument();
    expect(
      await screen.findByText('Mbappe · club · PSG — Not removed — this row no longer exists.'),
    ).toBeInTheDocument();
  });

  it('REQ-503: a 401 from bulk remove calls onAuthError', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );
    const user = userEvent.setup();

    render(<UnverifiedDataSection accessToken="token" rows={[row1]} onAuthError={onAuthError} onRefresh={vi.fn()} />);

    await user.click(screen.getByRole('checkbox', { name: /Select Henry/ }));
    await user.click(screen.getByRole('button', { name: 'Remove selected' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-503: "Dismiss" clears the approval-results panel', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({ results: [{ playerDataId: row1.id, approved: true, failureReason: null }] }),
      ),
    );
    const user = userEvent.setup();

    render(<UnverifiedDataSection accessToken="token" rows={[row1]} onAuthError={vi.fn()} onRefresh={vi.fn()} />);

    await user.click(screen.getByRole('checkbox', { name: /Select Henry/ }));
    await user.click(screen.getByRole('button', { name: 'Approve selected' }));
    await screen.findByText('Henry · nationality · France — Approved.');

    await user.click(screen.getByRole('button', { name: 'Dismiss' }));

    expect(screen.queryByText('Henry · nationality · France — Approved.')).not.toBeInTheDocument();
  });

  it('REQ-503: a selected row is dropped from the selection if it disappears from `rows` on rerender (e.g. after a refetch)', async () => {
    const user = userEvent.setup();
    const { rerender } = render(
      <UnverifiedDataSection accessToken="token" rows={[row1, row2]} onAuthError={vi.fn()} onRefresh={vi.fn()} />,
    );

    await user.click(screen.getByRole('checkbox', { name: /Select Henry/ }));
    expect(screen.getByText('1 selected')).toBeInTheDocument();

    rerender(<UnverifiedDataSection accessToken="token" rows={[row2]} onAuthError={vi.fn()} onRefresh={vi.fn()} />);

    await waitFor(() => expect(screen.getByText('0 selected')).toBeInTheDocument());
  });
});

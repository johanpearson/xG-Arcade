import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AdminScreen } from './AdminScreen';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

// `routes` maps a URL substring to a handler — handlers can be stateful
// (e.g. a call counter) so a test can simulate a list changing after a
// refetch. Throws on any URL none of the routes match, so an unexpected
// call fails loudly rather than hanging.
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

// A 404 with no body — the same shape a genuine routing miss (round-control
// feature absent in Production) returns.
function bareNotFound() {
  return Promise.resolve({
    ok: false,
    status: 404,
    json: () => Promise.reject(new Error('no body')),
  } as unknown as Response);
}

const unverifiedRow = {
  id: 'row-1',
  playerId: 'player-1',
  playerFullName: 'Henry',
  field: 'nationality',
  value: 'France',
  source: 'live_lookup',
  confidence: 'unverified',
  syncedAt: '2026-07-01T00:00:00Z',
};

const unverifiedRow2 = {
  id: 'row-2',
  playerId: 'player-2',
  playerFullName: 'Mbappe',
  field: 'club',
  value: 'PSG',
  source: 'wikidata',
  confidence: 'unverified',
  syncedAt: '2026-07-02T00:00:00Z',
};

const activeRound = {
  hasActiveRound: true,
  round: {
    roundId: 'round-1',
    gameKey: 'xg-grid',
    startTime: '2026-07-19T00:00:00Z',
    endTime: '2026-07-20T00:00:00Z',
  },
};

describe('AdminScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-503: renders each unverified row as "name · field · value · source"', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([unverifiedRow]),
      '/admin/rounds/xg-grid/active': bareNotFound,
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText('Unverified data (1)')).toBeInTheDocument();
    expect(screen.getByText('Henry · nationality · France · live_lookup')).toBeInTheDocument();
  });

  it('REQ-503: shows "No unverified data to review." when the list is empty', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText('No unverified data to review.')).toBeInTheDocument();
  });

  it('REQ-501/503: "Correct" opens an inline form, and a successful submit refetches the list', async () => {
    let unverifiedCallCount = 0;
    stubFetch({
      '/admin/player-data/unverified': () => {
        unverifiedCallCount += 1;
        return jsonResponse(unverifiedCallCount === 1 ? [unverifiedRow] : []);
      },
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/player-overrides': () =>
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
    });
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Henry · nationality · France · live_lookup');

    await user.click(screen.getByRole('button', { name: 'Correct' }));
    await user.clear(screen.getByLabelText('Value'));
    await user.type(screen.getByLabelText('Value'), 'Guadeloupe');
    await user.type(screen.getByLabelText('Reason'), 'Wikidata correction');
    await user.click(screen.getByRole('button', { name: 'Save correction' }));

    expect(await screen.findByText('No unverified data to review.')).toBeInTheDocument();
  });

  it('REQ-501: a 409 from creating an override is shown inline, without crashing or removing the row', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([unverifiedRow]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/player-overrides': () =>
        jsonResponse(
          { title: 'Conflict', detail: 'An override already exists for this field — use PUT to edit it.' },
          409,
        ),
    });
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Henry · nationality · France · live_lookup');

    await user.click(screen.getByRole('button', { name: 'Correct' }));
    await user.type(screen.getByLabelText('Reason'), 'Wikidata correction');
    await user.click(screen.getByRole('button', { name: 'Save correction' }));

    expect(
      await screen.findByText('An override already exists for this field — use PUT to edit it.'),
    ).toBeInTheDocument();
    expect(screen.getByText('Henry · nationality · France · live_lookup')).toBeInTheDocument();
  });

  it('REQ-503: "Approve selected" is disabled when no rows are selected', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([unverifiedRow]),
      '/admin/rounds/xg-grid/active': bareNotFound,
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Henry · nationality · France · live_lookup');

    expect(screen.getByRole('button', { name: 'Approve selected' })).toBeDisabled();
  });

  it('REQ-503: no "reason" field is rendered anywhere in the bulk-approve UI (unlike "Correct")', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([unverifiedRow]),
      '/admin/rounds/xg-grid/active': bareNotFound,
    });
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Henry · nationality · France · live_lookup');

    // Selecting a row for bulk approve, without ever opening "Correct",
    // must not surface a reason field anywhere on the page — "Correct"'s
    // own inline form is the only place a reason field exists, and it's
    // not open here.
    await user.click(screen.getByRole('checkbox', { name: /Select Henry/ }));
    expect(screen.queryByLabelText('Reason')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Approve selected' })).toBeEnabled();
  });

  it('REQ-503: "Select all" then "Approve selected" calls the approve endpoint with every visible id and no reason field', async () => {
    let unverifiedCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/player-data/unverified')) {
        unverifiedCallCount += 1;
        return jsonResponse(unverifiedCallCount === 1 ? [unverifiedRow, unverifiedRow2] : []);
      }
      if (path.includes('/admin/rounds/xg-grid/active')) return bareNotFound();
      if (path.includes('/admin/player-data/approve')) {
        return jsonResponse({
          results: [
            { playerDataId: unverifiedRow.id, approved: true, failureReason: null },
            { playerDataId: unverifiedRow2.id, approved: true, failureReason: null },
          ],
        });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Henry · nationality · France · live_lookup');
    await screen.findByText('Mbappe · club · PSG · wikidata');

    await user.click(screen.getByRole('checkbox', { name: 'Select all' }));
    expect(screen.getByText('2 selected')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Approve selected' }));

    await waitFor(() => {
      const approveCall = fetchMock.mock.calls.find(([url]) => String(url).includes('/admin/player-data/approve'));
      expect(approveCall).toBeDefined();
      const body = JSON.parse((approveCall![1] as RequestInit).body as string);
      expect(body).toEqual({ playerDataIds: [unverifiedRow.id, unverifiedRow2.id] });
    });

    expect(await screen.findByText('No unverified data to review.')).toBeInTheDocument();
  });

  it('REQ-503: approving a single row via its own checkbox calls the endpoint with just that id', async () => {
    let unverifiedCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/player-data/unverified')) {
        unverifiedCallCount += 1;
        return jsonResponse(unverifiedCallCount === 1 ? [unverifiedRow, unverifiedRow2] : [unverifiedRow2]);
      }
      if (path.includes('/admin/rounds/xg-grid/active')) return bareNotFound();
      if (path.includes('/admin/player-data/approve')) {
        return jsonResponse({
          results: [{ playerDataId: unverifiedRow.id, approved: true, failureReason: null }],
        });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Henry · nationality · France · live_lookup');
    await screen.findByText('Mbappe · club · PSG · wikidata');

    await user.click(screen.getByRole('checkbox', { name: /Select Henry/ }));
    expect(screen.getByText('1 selected')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Approve selected' }));

    await waitFor(() => {
      const approveCall = fetchMock.mock.calls.find(([url]) => String(url).includes('/admin/player-data/approve'));
      expect(approveCall).toBeDefined();
      const body = JSON.parse((approveCall![1] as RequestInit).body as string);
      expect(body).toEqual({ playerDataIds: [unverifiedRow.id] });
    });

    expect(await screen.findByText('Mbappe · club · PSG · wikidata')).toBeInTheDocument();
    expect(screen.queryByText('Henry · nationality · France · live_lookup')).not.toBeInTheDocument();
  });

  it('REQ-503: a partial-failure bulk approve shows which rows succeeded and which failed, distinctly (not as a full success or full failure)', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([unverifiedRow, unverifiedRow2]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/player-data/approve': () =>
        jsonResponse({
          results: [
            { playerDataId: unverifiedRow.id, approved: true, failureReason: null },
            { playerDataId: unverifiedRow2.id, approved: false, failureReason: 'NotUnverified' },
          ],
        }),
    });
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Henry · nationality · France · live_lookup');
    await screen.findByText('Mbappe · club · PSG · wikidata');

    await user.click(screen.getByRole('checkbox', { name: 'Select all' }));
    await user.click(screen.getByRole('button', { name: 'Approve selected' }));

    expect(await screen.findByText('Henry · nationality · France — Approved.')).toBeInTheDocument();
    expect(
      await screen.findByText('Mbappe · club · PSG — Not approved — already reviewed by someone else.'),
    ).toBeInTheDocument();
  });

  it('REQ-503: "Remove selected" is disabled when no rows are selected', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([unverifiedRow]),
      '/admin/rounds/xg-grid/active': bareNotFound,
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Henry · nationality · France · live_lookup');

    expect(screen.getByRole('button', { name: 'Remove selected' })).toBeDisabled();
  });

  it('REQ-503: "Select all" then "Remove selected" calls the remove endpoint with every visible id', async () => {
    let unverifiedCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/player-data/unverified')) {
        unverifiedCallCount += 1;
        return jsonResponse(unverifiedCallCount === 1 ? [unverifiedRow, unverifiedRow2] : []);
      }
      if (path.includes('/admin/rounds/xg-grid/active')) return bareNotFound();
      if (path.includes('/admin/player-data/remove')) {
        return jsonResponse({
          results: [
            { playerDataId: unverifiedRow.id, removed: true, failureReason: null },
            { playerDataId: unverifiedRow2.id, removed: true, failureReason: null },
          ],
        });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Henry · nationality · France · live_lookup');
    await screen.findByText('Mbappe · club · PSG · wikidata');

    await user.click(screen.getByRole('checkbox', { name: 'Select all' }));
    expect(screen.getByText('2 selected')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Remove selected' }));

    await waitFor(() => {
      const removeCall = fetchMock.mock.calls.find(([url]) => String(url).includes('/admin/player-data/remove'));
      expect(removeCall).toBeDefined();
      const body = JSON.parse((removeCall![1] as RequestInit).body as string);
      expect(body).toEqual({ playerDataIds: [unverifiedRow.id, unverifiedRow2.id] });
    });

    expect(await screen.findByText('No unverified data to review.')).toBeInTheDocument();
  });

  it('REQ-503: removing a single row via its own checkbox calls the endpoint with just that id', async () => {
    let unverifiedCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/player-data/unverified')) {
        unverifiedCallCount += 1;
        return jsonResponse(unverifiedCallCount === 1 ? [unverifiedRow, unverifiedRow2] : [unverifiedRow2]);
      }
      if (path.includes('/admin/rounds/xg-grid/active')) return bareNotFound();
      if (path.includes('/admin/player-data/remove')) {
        return jsonResponse({
          results: [{ playerDataId: unverifiedRow.id, removed: true, failureReason: null }],
        });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Henry · nationality · France · live_lookup');
    await screen.findByText('Mbappe · club · PSG · wikidata');

    await user.click(screen.getByRole('checkbox', { name: /Select Henry/ }));
    expect(screen.getByText('1 selected')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Remove selected' }));

    await waitFor(() => {
      const removeCall = fetchMock.mock.calls.find(([url]) => String(url).includes('/admin/player-data/remove'));
      expect(removeCall).toBeDefined();
      const body = JSON.parse((removeCall![1] as RequestInit).body as string);
      expect(body).toEqual({ playerDataIds: [unverifiedRow.id] });
    });

    expect(await screen.findByText('Mbappe · club · PSG · wikidata')).toBeInTheDocument();
    expect(screen.queryByText('Henry · nationality · France · live_lookup')).not.toBeInTheDocument();
  });

  it('REQ-503: a partial-failure bulk remove shows which rows succeeded and which failed, distinctly (not as a full success or full failure)', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([unverifiedRow, unverifiedRow2]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/player-data/remove': () =>
        jsonResponse({
          results: [
            { playerDataId: unverifiedRow.id, removed: true, failureReason: null },
            { playerDataId: unverifiedRow2.id, removed: false, failureReason: 'NotFound' },
          ],
        }),
    });
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Henry · nationality · France · live_lookup');
    await screen.findByText('Mbappe · club · PSG · wikidata');

    await user.click(screen.getByRole('checkbox', { name: 'Select all' }));
    await user.click(screen.getByRole('button', { name: 'Remove selected' }));

    expect(await screen.findByText('Henry · nationality · France — Removed.')).toBeInTheDocument();
    expect(
      await screen.findByText('Mbappe · club · PSG — Not removed — this row no longer exists.'),
    ).toBeInTheDocument();
  });

  it('REQ-505/506: the round-control and user-deletion sections are entirely absent when the active-round probe 404s', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('No unverified data to review.');

    expect(screen.queryByText(/Round control/)).not.toBeInTheDocument();
    expect(screen.queryByText('Delete a user')).not.toBeInTheDocument();
  });

  it('REQ-505/506: the round-control and user-deletion sections render when the active-round probe succeeds', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': () => jsonResponse(activeRound),
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText('Round control — xg-grid')).toBeInTheDocument();
    expect(screen.getByText('Round round-1 · ends 2026-07-20T00:00:00Z')).toBeInTheDocument();
    expect(screen.getByText('Delete a user')).toBeInTheDocument();
  });

  it('REQ-505: "End round now" requires a second, explicit confirm click before calling the close endpoint', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (path.includes('/admin/rounds/xg-grid/active')) return jsonResponse(activeRound);
      if (path.includes('/admin/rounds/xg-grid/close')) return jsonResponse(activeRound.round);
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Round control — xg-grid');

    await user.click(screen.getByRole('button', { name: 'End round now' }));
    expect(fetchMock).not.toHaveBeenCalledWith(expect.stringContaining('/close'), expect.anything());

    await user.click(screen.getByRole('button', { name: 'Yes, end round now' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/admin/rounds/xg-grid/close'),
        expect.objectContaining({ method: 'POST' }),
      ),
    );
  });

  it('REQ-506: "Delete user" requires a second, explicit confirm click before calling the delete endpoint', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (path.includes('/admin/rounds/xg-grid/active')) return jsonResponse(activeRound);
      if (path.includes('/admin/users')) {
        return Promise.resolve({ ok: true, status: 204, json: () => Promise.resolve(null) } as Response);
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Delete a user');

    await user.type(screen.getByLabelText('Email'), 'test@example.com');
    await user.click(screen.getByRole('button', { name: 'Delete user' }));
    expect(fetchMock).not.toHaveBeenCalledWith(expect.stringContaining('/admin/users'), expect.anything());

    await user.click(screen.getByRole('button', { name: 'Yes, delete this user permanently' }));

    await waitFor(() => expect(screen.getByText('Deleted.')).toBeInTheDocument());
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/admin/users?email=test%40example.com'),
      expect.objectContaining({ method: 'DELETE' }),
    );
  });

  it('REQ-506: deleting a user with no match shows "No user found with that email." inline', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': () => jsonResponse(activeRound),
      '/admin/users': bareNotFound,
    });
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Delete a user');

    await user.type(screen.getByLabelText('Email'), 'nobody@example.com');
    await user.click(screen.getByRole('button', { name: 'Delete user' }));
    await user.click(screen.getByRole('button', { name: 'Yes, delete this user permanently' }));

    expect(await screen.findByText('No user found with that email.')).toBeInTheDocument();
  });

  it('REQ-504: a 403 from the unverified-data fetch shows only an access-denied message for the whole page', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () =>
        jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403),
      '/admin/rounds/xg-grid/active': bareNotFound,
    });

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText("You don't have access to this page.")).toBeInTheDocument();
    expect(onAuthError).not.toHaveBeenCalled();
    expect(screen.queryByText('Unverified data')).not.toBeInTheDocument();
  });

  it('REQ-504: a 401 from the unverified-data fetch calls onAuthError', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () =>
        jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401),
      '/admin/rounds/xg-grid/active': bareNotFound,
    });

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-504/505: a 403 from the round-control probe (unverified-data fetch succeeding) still shows access-denied for the whole page', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': () =>
        jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403),
    });

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText("You don't have access to this page.")).toBeInTheDocument();
    expect(onAuthError).not.toHaveBeenCalled();
    expect(screen.queryByText('No unverified data to review.')).not.toBeInTheDocument();
  });

  it('REQ-507: renders total/current/claimed guest counts from the metrics endpoint', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/accounts/metrics': () =>
        jsonResponse({ totalUserCount: 42, currentGuestCount: 7, claimedGuestCount: 3 }),
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText('Accounts')).toBeInTheDocument();
    // "Accounts" renders unconditionally on mount, before the
    // /admin/accounts/metrics fetch resolves (AdminScreen.tsx's `metrics`
    // state starts null) — the metrics block below is a separate,
    // later render, so it needs its own await rather than an immediate
    // getByText racing the fetch.
    expect(await screen.findByText('Total users')).toBeInTheDocument();
    expect(screen.getByText('42')).toBeInTheDocument();
    expect(screen.getByText('Current guests')).toBeInTheDocument();
    expect(screen.getByText('7')).toBeInTheDocument();
    expect(screen.getByText('Claimed guests')).toBeInTheDocument();
    expect(screen.getByText('3')).toBeInTheDocument();
  });

  it('REQ-507: a 403 from the metrics endpoint hides the Accounts/guest-clear sections without flipping the whole page to access-denied', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/accounts/metrics': () => jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403),
    });

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText('No unverified data to review.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText('Accounts')).not.toBeInTheDocument());
    expect(screen.queryByText('Guest accounts')).not.toBeInTheDocument();
    expect(onAuthError).not.toHaveBeenCalled();
  });

  it('REQ-507: a 401 from the metrics endpoint calls onAuthError', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/accounts/metrics': () => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401),
    });

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-508: "Force clear guests" shows the dry-run count in the confirm prompt, and only calls the clear endpoint after confirming', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (path.includes('/admin/rounds/xg-grid/active')) return bareNotFound();
      if (path.includes('/admin/accounts/metrics')) {
        return jsonResponse({ totalUserCount: 10, currentGuestCount: 5, claimedGuestCount: 1 });
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

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Guest accounts');

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

  it('REQ-508: "Cancel" during the confirm step does not call the clear endpoint', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (path.includes('/admin/rounds/xg-grid/active')) return bareNotFound();
      if (path.includes('/admin/accounts/metrics')) {
        return jsonResponse({ totalUserCount: 10, currentGuestCount: 5, claimedGuestCount: 1 });
      }
      if (path.includes('/admin/accounts/guests/count')) return jsonResponse({ count: 5 });
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Guest accounts');

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
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/accounts/metrics': () =>
        jsonResponse({ totalUserCount: 10, currentGuestCount: 0, claimedGuestCount: 1 }),
      '/admin/accounts/guests/count': () => jsonResponse({ count: 0 }),
    });
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Guest accounts');

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));

    expect(await screen.findByText('No guest accounts to clear right now.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Yes, delete all/ })).not.toBeInTheDocument();
  });

  it('REQ-508: a partial-outcome clear shows Succeeded/NotFound/Failed distinctly, using the server error message when Failed', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (path.includes('/admin/rounds/xg-grid/active')) return bareNotFound();
      if (path.includes('/admin/accounts/metrics')) {
        return jsonResponse({ totalUserCount: 10, currentGuestCount: 3, claimedGuestCount: 1 });
      }
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

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('Guest accounts');

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));
    await user.click(await screen.findByRole('button', { name: 'Yes, delete all 3 guest accounts' }));

    expect(await screen.findByText('guest-1 — Cleared.')).toBeInTheDocument();
    expect(screen.getByText('guest-2 — Not cleared — this account no longer exists.')).toBeInTheDocument();
    expect(screen.getByText('guest-3 — Not cleared — Supabase delete failed.')).toBeInTheDocument();
  });

  it('REQ-508: a successful clear refreshes the account metrics', async () => {
    let metricsCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (path.includes('/admin/rounds/xg-grid/active')) return bareNotFound();
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

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('5');

    await user.click(screen.getByRole('button', { name: 'Force clear guests' }));
    await user.click(await screen.findByRole('button', { name: 'Yes, delete all 5 guest accounts' }));

    await screen.findByText('guest-1 — Cleared.');
    await waitFor(() => expect(screen.getByText('Current guests').nextSibling?.textContent).toBe('0'));
  });

  // ---- REQ-1209: xG Path target cycle section --------------------------

  it('REQ-1209: renders the current cycle number, pool size, used/remaining counts, and last-completion time from a successful fetch', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/xg-path/cycle': () =>
        jsonResponse({
          hasData: true,
          cycleNumber: 3,
          observedPoolSize: 42,
          usedInCycleCount: 17,
          remainingInCycleCount: 25,
          lastCycleCompletedAt: '2026-08-01T09:30:00Z',
        }),
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText('xG Path target cycle')).toBeInTheDocument();
    expect(await screen.findByText('Current cycle')).toBeInTheDocument();
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

  it('REQ-1209: shows the pre-first-generation "no data yet" state when hasData is false, never an error and never a blank section', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/xg-path/cycle': () =>
        jsonResponse({
          hasData: false,
          cycleNumber: null,
          observedPoolSize: null,
          usedInCycleCount: null,
          remainingInCycleCount: null,
          lastCycleCompletedAt: null,
        }),
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText('xG Path target cycle')).toBeInTheDocument();
    expect(
      await screen.findByText('No xG Path round has generated yet — no cycle data to show.'),
    ).toBeInTheDocument();
    expect(screen.queryByText('Current cycle')).not.toBeInTheDocument();
  });

  it('REQ-1209: renders "No cycle has completed yet" when lastCycleCompletedAt is null but a cycle is otherwise in progress', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/xg-path/cycle': () =>
        jsonResponse({
          hasData: true,
          cycleNumber: 1,
          observedPoolSize: 12,
          usedInCycleCount: 4,
          remainingInCycleCount: 8,
          lastCycleCompletedAt: null,
        }),
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText('No cycle has completed yet')).toBeInTheDocument();
  });

  it('REQ-1209: a 401 from the cycle endpoint calls onAuthError', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/xg-path/cycle': () => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401),
    });

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-1209: a 403 from the cycle endpoint hides the section without flipping the whole page to access-denied', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/xg-path/cycle': () => jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403),
    });

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText('No unverified data to review.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText('xG Path target cycle')).not.toBeInTheDocument());
    expect(onAuthError).not.toHaveBeenCalled();
  });

  it('REQ-1209: a non-401/403 error from the cycle endpoint shows an inline error message within its own section, not a page-wide failure', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/xg-path/cycle': () => jsonResponse({ title: 'Server error', detail: 'Something broke.' }, 500),
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText('xG Path target cycle')).toBeInTheDocument();
    expect(await screen.findByText('Something broke.')).toBeInTheDocument();
    // The rest of the page must remain usable — this section's error is
    // scoped to itself, same as AccountMetricsSection's own error handling.
    expect(await screen.findByText('No unverified data to review.')).toBeInTheDocument();
  });
});

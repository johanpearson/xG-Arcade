import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AdminScreen } from './AdminScreen';

// S-156 (docs/backlog.md): this file previously carried the full
// render/interaction/error-path coverage for UnverifiedDataSection,
// RoundControlSection, UserDeletionSection, and GuestClearSection (composed
// inside AccountMetricsSection) as an indirect side effect of testing
// AdminScreen. Those subcomponents now each have their own dedicated
// `*.test.tsx` file exercising that behavior in isolation with props
// supplied directly, per the same pattern S-108 established for
// AccountMetricsSection/AnnouncementBannerSection/IncidentReportsEntry/
// PlayerSuggestionsEntry/XGPathCycleSection. What remains here is scoped to
// AdminScreen's own composition/wiring: fetching on mount and passing real
// data down, refetching via a real (not mocked) onRefresh — exercised for
// both UnverifiedDataSection (the "Correct… refetches the list" test) and
// RoundControlSection (the "End round now… refetches the active round"
// test) — and the activeRound-gated show/hide of
// RoundControlSection+UserDeletionSection together — not each subcomponent's
// own internal render/interaction/error branches, which the dedicated files
// now own.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

// AvatarModerationSection (REQ-517/S-183) fetches GET /admin/avatar-submissions
// unconditionally on every AdminScreen mount — unlike UserDeletionSection, it
// isn't gated behind the activeRound probe, so it always fires alongside the
// other section fetches this file already stubs. None of the tests below
// care about its content, so `stubFetch` folds in a default empty-list
// response for it (a test can still override this by supplying its own
// '/admin/avatar-submissions' entry in `routes`, which takes precedence).
// Without this default, the mocked fetch throws "Unexpected fetch:" for that
// URL on every render — caught internally by useAuthedFetch (never an
// unhandled rejection, so no test fails), but it silently left
// AvatarModerationSection stuck in a loadError state on every test that
// renders the default "Users" tab, which nothing here was asserting against.
const defaultRoutes: Record<string, () => Promise<Response>> = {
  '/admin/avatar-submissions': () => jsonResponse([]),
};

// `routes` maps a URL substring to a handler — handlers can be stateful
// (e.g. a call counter) so a test can simulate a list changing after a
// refetch. Throws on any URL none of the routes (or defaultRoutes above)
// match, so an unexpected call fails loudly rather than hanging.
function stubFetch(routes: Record<string, () => Promise<Response>>) {
  const merged = { ...defaultRoutes, ...routes };
  vi.stubGlobal(
    'fetch',
    vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      const match = Object.entries(merged).find(([suffix]) => path.includes(suffix));
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

const activeRound = {
  hasActiveRound: true,
  round: {
    roundId: 'round-1',
    sequenceNumber: 12,
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
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: UnverifiedDataSection now lives in the "Grid" nav group,
    // which isn't the default ("Users") — select it before asserting on
    // its content.
    await user.click(await screen.findByRole('tab', { name: 'Grid' }));

    expect(await screen.findByText('Unverified data (1)')).toBeInTheDocument();
    expect(screen.getByText('Henry · nationality · France · live_lookup')).toBeInTheDocument();
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
    // REQ-516: UnverifiedDataSection now lives in the "Grid" nav group,
    // which isn't the default ("Users") — select it before asserting on
    // its content.
    await user.click(await screen.findByRole('tab', { name: 'Grid' }));
    await screen.findByText('Henry · nationality · France · live_lookup');

    await user.click(screen.getByRole('button', { name: 'Correct' }));
    await user.clear(screen.getByLabelText('Value'));
    await user.type(screen.getByLabelText('Value'), 'Guadeloupe');
    await user.type(screen.getByLabelText('Reason'), 'Wikidata correction');
    await user.click(screen.getByRole('button', { name: 'Save correction' }));

    expect(await screen.findByText('No unverified data to review.')).toBeInTheDocument();
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

  it("REQ-505/506: a non-401/403/404 failure from the active-round probe (500) is also swallowed to 'no active round', not escalated to a page-wide error", async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': () =>
        jsonResponse({ title: 'Server error', detail: 'Something broke.' }, 500),
    });

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('No unverified data to review.');

    expect(screen.queryByText(/Round control/)).not.toBeInTheDocument();
    expect(screen.queryByText('Delete a user')).not.toBeInTheDocument();
    expect(screen.queryByText("You don't have access to this page.")).not.toBeInTheDocument();
    expect(screen.queryByText('Something broke.')).not.toBeInTheDocument();
    expect(onAuthError).not.toHaveBeenCalled();
  });

  it('REQ-505/506: the round-control and user-deletion sections render when the active-round probe succeeds', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': () => jsonResponse(activeRound),
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText('Round control — xg-grid')).toBeInTheDocument();
    // REQ-304: the round label uses the human-readable sequenceNumber, not
    // the raw roundId GUID, which must never appear as visible text.
    expect(screen.getByText('Grid Round #12 · ends 2026-07-20T00:00:00Z')).toBeInTheDocument();
    expect(screen.queryByText(/round-1/)).not.toBeInTheDocument();
    expect(screen.getByText('Delete a user')).toBeInTheDocument();
  });

  it('REQ-505: "End round now" refetches the active round via AdminScreen\'s real (not mocked) onRefresh', async () => {
    // Mirrors the "Correct… refetches the list" composition test above for
    // UnverifiedDataSection: the second /active probe response differs from
    // the first, so a passing assertion only follows from RoundControlSection's
    // onRefresh prop actually being AdminScreen's real refreshActiveRound
    // callback (not a mocked no-op) round-tripping through the network.
    let activeRoundCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/avatar-submissions')) return jsonResponse([]);
      if (path.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (path.includes('/admin/rounds/xg-grid/close')) return jsonResponse(activeRound.round);
      if (path.includes('/admin/rounds/xg-grid/active')) {
        activeRoundCallCount += 1;
        return jsonResponse(activeRoundCallCount === 1 ? activeRound : { hasActiveRound: false, round: null });
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: RoundControlSection now lives in the "Grid" nav group, which
    // isn't the default ("Users") — select it before interacting.
    await user.click(await screen.findByRole('tab', { name: 'Grid' }));
    await screen.findByText('Grid Round #12 · ends 2026-07-20T00:00:00Z');

    await user.click(screen.getByRole('button', { name: 'End round now' }));
    await user.click(screen.getByRole('button', { name: 'Yes, end round now' }));

    expect(await screen.findByText('No active round right now.')).toBeInTheDocument();
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

  // ---- REQ-512: pending player-suggestions badge ------------------------

  function pendingSuggestion(id: string) {
    return {
      id,
      playerName: 'Someone Player',
      assertedClubs: ['Some Club'],
      assertedNationality: 'Some Country',
      submittingUserId: 'user-1',
      submittingUserDisplayName: 'Player One',
      rowCategoryType: 'Nationality',
      colCategoryType: 'Club',
      createdAt: '2026-08-01T00:00:00Z',
    };
  }

  it('REQ-512: shows "Player suggestions (3)" when 3 suggestions are pending', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/suggestions': () =>
        jsonResponse([pendingSuggestion('s-1'), pendingSuggestion('s-2'), pendingSuggestion('s-3')]),
    });

    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: PlayerSuggestionsEntry now lives in the "Grid" nav group,
    // which isn't the default ("Users") — select it before asserting on
    // its content.
    await user.click(await screen.findByRole('tab', { name: 'Grid' }));

    expect(await screen.findByRole('button', { name: 'Player suggestions (3)' })).toBeInTheDocument();
  });

  it('REQ-512: shows plain "Player suggestions" with no "(0)" when zero suggestions are pending', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/suggestions': () => jsonResponse([]),
    });

    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: PlayerSuggestionsEntry now lives in the "Grid" nav group,
    // which isn't the default ("Users") — select it before asserting on
    // its content.
    await user.click(await screen.findByRole('tab', { name: 'Grid' }));

    // Wait for the fetch to resolve rather than asserting on the initial
    // (also badge-less) render, so this genuinely exercises the N===0 case
    // rather than passing trivially on the pre-fetch state.
    await screen.findByText('No unverified data to review.');
    expect(await screen.findByRole('button', { name: 'Player suggestions' })).toBeInTheDocument();
  });

  it('REQ-512: a 401 from the suggestions fetch calls onAuthError', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/suggestions': () => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401),
    });

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-512: a 403 from the suggestions fetch leaves the button showing plain "Player suggestions", with no error banner and no page-level access-denied flip', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/suggestions': () => jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403),
    });

    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);
    // REQ-516: PlayerSuggestionsEntry now lives in the "Grid" nav group,
    // which isn't the default ("Users") — select it before asserting on
    // its content.
    await user.click(await screen.findByRole('tab', { name: 'Grid' }));

    expect(await screen.findByRole('button', { name: 'Player suggestions' })).toBeInTheDocument();
    // The rest of the page renders normally — this section's failure never
    // flips the whole page to access-denied, same as AccountMetricsSection's
    // and XGPathCycleSection's own 403 resilience.
    expect(await screen.findByText('No unverified data to review.')).toBeInTheDocument();
    expect(screen.queryByText("You don't have access to this page.")).not.toBeInTheDocument();
    expect(onAuthError).not.toHaveBeenCalled();
  });

  it('REQ-512: clicking "Player suggestions" still calls onOpenSuggestions regardless of badge state', async () => {
    const onOpenSuggestions = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/suggestions': () => jsonResponse([pendingSuggestion('s-1')]),
    });
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={onOpenSuggestions} />);
    // REQ-516: PlayerSuggestionsEntry now lives in the "Grid" nav group,
    // which isn't the default ("Users") — select it before interacting.
    await user.click(await screen.findByRole('tab', { name: 'Grid' }));

    const button = await screen.findByRole('button', { name: 'Player suggestions (1)' });
    await user.click(button);

    expect(onOpenSuggestions).toHaveBeenCalledTimes(1);
  });

  it('REQ-512: a non-401/403 error from the suggestions fetch shows an inline error message, with no badge and no onAuthError call', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/suggestions': () => jsonResponse({ title: 'Server error', detail: 'Something broke.' }, 500),
    });

    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);
    // REQ-516: PlayerSuggestionsEntry now lives in the "Grid" nav group,
    // which isn't the default ("Users") — select it before asserting on
    // its content.
    await user.click(await screen.findByRole('tab', { name: 'Grid' }));

    expect(await screen.findByText('Something broke.')).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Player suggestions' })).toBeInTheDocument();
    // The rest of the page must remain usable — this section's error is
    // scoped to itself, same as XGPathCycleSection's own error handling.
    expect(await screen.findByText('No unverified data to review.')).toBeInTheDocument();
    expect(onAuthError).not.toHaveBeenCalled();
  });

  // ---- REQ-904: incident-reports admin notification ---------------------

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

  it('REQ-904: shows plain "Incident reports" with no count and no GitHub link when zero issues are open', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/incident-reports': () => jsonResponse(incidentReportsResponse(0)),
    });

    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: IncidentReportsEntry now lives in the "Issues" nav group,
    // which isn't the default ("Users") — select it before asserting on
    // its content.
    await user.click(await screen.findByRole('tab', { name: 'Issues' }));

    // Wait for the page (and this section's own fetch) to resolve before
    // asserting absence, so this genuinely exercises the openCount===0 case
    // rather than passing trivially on the pre-fetch render.
    await screen.findByText('No unverified data to review.');
    expect(await screen.findByRole('heading', { name: 'Incident reports' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'View open reports on GitHub' })).not.toBeInTheDocument();
    // Distinguishes this from the `available: false` case below, which
    // renders the exact same bare heading PLUS this inline message.
    expect(
      screen.queryByText(
        "Couldn't check GitHub for open incident reports right now — this doesn't mean there are none, try reloading in a minute.",
      ),
    ).not.toBeInTheDocument();
  });

  it('REQ-904: shows "Incident reports (3)" and a "View open reports on GitHub" link when 3 issues are open', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/incident-reports': () => jsonResponse(incidentReportsResponse(3)),
    });

    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: IncidentReportsEntry now lives in the "Issues" nav group,
    // which isn't the default ("Users") — select it before asserting on
    // its content.
    await user.click(await screen.findByRole('tab', { name: 'Issues' }));

    expect(await screen.findByRole('heading', { name: 'Incident reports (3)' })).toBeInTheDocument();
    const link = screen.getByRole('link', { name: 'View open reports on GitHub' });
    expect(link).toHaveAttribute(
      'href',
      'https://github.com/johanpearson/xg-arcade/issues?q=is%3Aissue+is%3Aopen+label%3Auser-reported',
    );
    expect(link).toHaveAttribute('target', '_blank');
  });

  it('REQ-904: shows a distinct "unavailable" message (never the zero-count rendering) when available is false', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/incident-reports': () => jsonResponse({ available: false, openCount: 0, issues: [] }),
    });

    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: IncidentReportsEntry now lives in the "Issues" nav group,
    // which isn't the default ("Users") — select it before asserting on
    // its content.
    await user.click(await screen.findByRole('tab', { name: 'Issues' }));

    expect(await screen.findByRole('heading', { name: 'Incident reports' })).toBeInTheDocument();
    // REQ-904: available:false must never be silently rendered the same way
    // as a real zero count — the zero-count test above asserts NO alert is
    // present; this is the distinguishing DOM difference between the two
    // otherwise-identical-looking headings.
    expect(
      await screen.findByText(
        "Couldn't check GitHub for open incident reports right now — this doesn't mean there are none, try reloading in a minute.",
      ),
    ).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'View open reports on GitHub' })).not.toBeInTheDocument();
  });

  it('REQ-904: shows an inline error (not the "unavailable" message) on a non-401/403 fetch failure', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/incident-reports': () => jsonResponse({ title: 'Server error', detail: 'Something broke.' }, 500),
    });

    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: IncidentReportsEntry now lives in the "Issues" nav group,
    // which isn't the default ("Users") — select it before asserting on
    // its content.
    await user.click(await screen.findByRole('tab', { name: 'Issues' }));

    expect(await screen.findByRole('heading', { name: 'Incident reports' })).toBeInTheDocument();
    expect(await screen.findByText('Something broke.')).toBeInTheDocument();
    expect(
      screen.queryByText(
        "Couldn't check GitHub for open incident reports right now — this doesn't mean there are none, try reloading in a minute.",
      ),
    ).not.toBeInTheDocument();
  });

  it('REQ-904: shows an inline error on a network failure, describing the underlying error', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/incident-reports': () => Promise.reject(new Error('Network request failed')),
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText('Network request failed')).toBeInTheDocument();
  });

  it('REQ-904: hides the "Incident reports" entry entirely on a 403 (non-admin), with no error banner', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/incident-reports': () => jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403),
    });

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);

    await screen.findByText('No unverified data to review.');
    await waitFor(() => expect(screen.queryByText(/Incident reports/)).not.toBeInTheDocument());
    expect(onAuthError).not.toHaveBeenCalled();
  });

  it('REQ-904: a 401 from the incident-reports fetch calls onAuthError', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/incident-reports': () => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401),
    });

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  // ---- REQ-511: site-wide announcement banner section -------------------

  const loadedActiveBanner = {
    id: 'banner-1',
    message: 'Scheduled maintenance tonight at 10pm UTC.',
    isActive: true,
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
    lastUpdatedByAdminId: 'admin-1',
  };

  const loadedInactiveBanner = { ...loadedActiveBanner, isActive: false };

  it('REQ-511: shows "No banner has been created yet" when the GET endpoint 404s (no-data-yet state)', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/announcement-banner': bareNotFound,
    });

    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: AnnouncementBannerSection now lives in the "Announcements"
    // nav group, which isn't the default ("Users") — select it before
    // asserting on its content.
    await user.click(await screen.findByRole('tab', { name: 'Announcements' }));

    expect(await screen.findByText('Site-wide announcement banner')).toBeInTheDocument();
    expect(
      await screen.findByText('No banner has been created yet — write one below.'),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create banner' })).toBeInTheDocument();
    // No banner exists yet, so the activate/deactivate action group must not render.
    expect(screen.queryByRole('button', { name: 'Activate' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Deactivate' })).not.toBeInTheDocument();
  });

  it('REQ-511: shows the current message and "Active" status for a loaded, active banner', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/announcement-banner': () => jsonResponse(loadedActiveBanner),
    });

    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: AnnouncementBannerSection now lives in the "Announcements"
    // nav group, which isn't the default ("Users") — select it before
    // asserting on its content.
    await user.click(await screen.findByRole('tab', { name: 'Announcements' }));

    expect(await screen.findByText('Status: Active — visible to every visitor')).toBeInTheDocument();
    // The message input's value is set from a separate `useEffect` keyed on
    // `banner`, which commits in its own pass after the "Status: Active" text
    // — asserting it synchronously right after `findByText` above raced that
    // effect and flaked intermittently under a full-suite run (see NOTES.md
    // 2026-07-25's REQ-507 flake for the same root cause on a sibling
    // section). `waitFor` gives the effect a chance to flush.
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue(loadedActiveBanner.message));
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Deactivate' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Activate' })).not.toBeInTheDocument();
  });

  it('REQ-511: shows "Inactive" status and an "Activate" button for a loaded, inactive banner', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/announcement-banner': () => jsonResponse(loadedInactiveBanner),
    });

    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: AnnouncementBannerSection now lives in the "Announcements"
    // nav group, which isn't the default ("Users") — select it before
    // asserting on its content.
    await user.click(await screen.findByRole('tab', { name: 'Announcements' }));

    expect(await screen.findByText('Status: Inactive — not shown to visitors')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Activate' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Deactivate' })).not.toBeInTheDocument();
  });

  it('REQ-511: a 403 from the announcement-banner fetch hides the section without flipping the whole page to access-denied', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/announcement-banner': () => jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403),
    });

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText('No unverified data to review.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText('Site-wide announcement banner')).not.toBeInTheDocument());
    expect(onAuthError).not.toHaveBeenCalled();
  });

  it('REQ-511: a 401 from the announcement-banner fetch calls onAuthError', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/announcement-banner': () => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401),
    });

    render(<AdminScreen accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-511: creating a banner (none exists yet) submits the typed message via PUT and shows the saved, inactive result', async () => {
    // stubFetch's URL-substring routing can't tell GET /admin/announcement-banner
    // apart from PUT /admin/announcement-banner (same URL, different verb) —
    // a manual fetchMock checking init.method is used here instead, same as
    // the existing "End round now"/"Delete user" confirm-step tests above.
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      if (path.includes('/admin/avatar-submissions')) return jsonResponse([]);
      if (path.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (path.includes('/admin/rounds/xg-grid/active')) return bareNotFound();
      if (path.includes('/admin/announcement-banner')) {
        if (init?.method === 'PUT') return jsonResponse(loadedInactiveBanner);
        return bareNotFound(); // GET: no banner created yet
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: AnnouncementBannerSection now lives in the "Announcements"
    // nav group, which isn't the default ("Users") — select it before
    // interacting.
    await user.click(await screen.findByRole('tab', { name: 'Announcements' }));
    await screen.findByText('No banner has been created yet — write one below.');

    await user.type(screen.getByLabelText('Message'), loadedInactiveBanner.message);
    await user.click(screen.getByRole('button', { name: 'Create banner' }));

    await waitFor(() => {
      const putCall = fetchMock.mock.calls.find(
        ([url, callInit]) => String(url).includes('/admin/announcement-banner') && (callInit as RequestInit)?.method === 'PUT',
      );
      expect(putCall).toBeDefined();
      const body = JSON.parse((putCall![1] as RequestInit).body as string);
      expect(body).toEqual({ message: loadedInactiveBanner.message });
    });

    expect(await screen.findByText('Status: Inactive — not shown to visitors')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Activate' })).toBeInTheDocument();
  });

  it('REQ-511: "Activate" calls the activate endpoint and flips the shown status to Active', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/avatar-submissions')) return jsonResponse([]);
      if (path.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (path.includes('/admin/rounds/xg-grid/active')) return bareNotFound();
      if (path.includes('/admin/announcement-banner/activate')) {
        return jsonResponse({ ...loadedInactiveBanner, isActive: true });
      }
      if (path.includes('/admin/announcement-banner')) return jsonResponse(loadedInactiveBanner);
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: AnnouncementBannerSection now lives in the "Announcements"
    // nav group, which isn't the default ("Users") — select it before
    // interacting.
    await user.click(await screen.findByRole('tab', { name: 'Announcements' }));
    await screen.findByText('Status: Inactive — not shown to visitors');

    await user.click(screen.getByRole('button', { name: 'Activate' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/admin/announcement-banner/activate'),
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    expect(await screen.findByText('Status: Active — visible to every visitor')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Deactivate' })).toBeInTheDocument();
  });

  // ---- REQ-516: grouped nav ----------------------------------------------

  it('REQ-516: renders a grouped nav tablist with all 5 groups (and no separate top-level tab for avatar moderation), defaulting to "Users" selected and visible', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/accounts/metrics': () =>
        jsonResponse({ totalUserCount: 1, currentGuestCount: 0, claimedGuestCount: 0 }),
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByRole('tablist', { name: 'Admin section' })).toBeInTheDocument();
    for (const label of ['Users', 'Grid', 'Path', 'Announcements', 'Issues']) {
      expect(screen.getByRole('tab', { name: label })).toBeInTheDocument();
    }
    expect(screen.getByRole('tab', { name: 'Users' })).toHaveAttribute('aria-selected', 'true');
    for (const label of ['Grid', 'Path', 'Announcements', 'Issues']) {
      expect(screen.getByRole('tab', { name: label })).toHaveAttribute('aria-selected', 'false');
    }
    // REQ-517/S-183: avatar moderation is grouped under "Users" (asserted via
    // its content's visibility below), not a standalone nav entry of its
    // own — the 5 tabs enumerated above are the complete set.
    expect(screen.getAllByRole('tab')).toHaveLength(5);

    // "Users" group content (AccountMetricsSection, AvatarModerationSection)
    // is visible by default...
    expect(await screen.findByText('Accounts')).toBeVisible();
    expect(await screen.findByText('Avatar moderation')).toBeVisible();
    // ...while "Grid" group content (UnverifiedDataSection) is mounted (its
    // fetch already ran) but hidden behind the unselected tab.
    expect(await screen.findByText('No unverified data to review.')).not.toBeVisible();
  });

  it('REQ-516/REQ-517: clicking a different tab shows only that group and hides the previously-visible one, including avatar moderation', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/accounts/metrics': () =>
        jsonResponse({ totalUserCount: 1, currentGuestCount: 0, claimedGuestCount: 0 }),
    });
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    expect(await screen.findByText('Accounts')).toBeVisible();
    expect(await screen.findByText('Avatar moderation')).toBeVisible();
    await screen.findByText('No unverified data to review.');

    await user.click(screen.getByRole('tab', { name: 'Grid' }));

    expect(screen.getByRole('tab', { name: 'Grid' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'Users' })).toHaveAttribute('aria-selected', 'false');
    expect(screen.getByText('No unverified data to review.')).toBeVisible();
    // The previously-visible default group's content is still mounted, but
    // no longer visible — never removed from the DOM by a tab switch.
    expect(screen.getByText('Accounts')).not.toBeVisible();
    expect(screen.getByText('Avatar moderation')).not.toBeVisible();
  });

  it('REQ-516: switching from "Users" to "Grid" and back does not re-fetch either section\'s data', async () => {
    let unverifiedCallCount = 0;
    let metricsCallCount = 0;
    stubFetch({
      '/admin/player-data/unverified': () => {
        unverifiedCallCount += 1;
        return jsonResponse([]);
      },
      '/admin/rounds/xg-grid/active': bareNotFound,
      '/admin/accounts/metrics': () => {
        metricsCallCount += 1;
        return jsonResponse({ totalUserCount: 1, currentGuestCount: 0, claimedGuestCount: 0 });
      },
    });
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('No unverified data to review.');
    await screen.findByText('Total users');
    expect(unverifiedCallCount).toBe(1);
    expect(metricsCallCount).toBe(1);

    await user.click(screen.getByRole('tab', { name: 'Grid' }));
    await user.click(screen.getByRole('tab', { name: 'Users' }));

    expect(unverifiedCallCount).toBe(1);
    expect(metricsCallCount).toBe(1);
  });

  it('REQ-516: round-control and user-deletion stay entirely absent from the DOM under the grouped nav, even after navigating to their groups', async () => {
    stubFetch({
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': bareNotFound,
    });
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    await screen.findByText('No unverified data to review.');

    await user.click(screen.getByRole('tab', { name: 'Grid' }));
    expect(screen.queryByText(/Round control/)).not.toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Users' }));
    expect(screen.queryByText('Delete a user')).not.toBeInTheDocument();
  });

  it('REQ-516: the page-level access-denied message renders with no grouped nav/tablist at all', async () => {
    stubFetch({
      '/admin/player-data/unverified': () =>
        jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403),
      '/admin/rounds/xg-grid/active': bareNotFound,
    });

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);

    expect(await screen.findByText("You don't have access to this page.")).toBeInTheDocument();
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(screen.queryByRole('tab')).not.toBeInTheDocument();
  });

  it('REQ-511: "Deactivate" calls the deactivate endpoint, flips the shown status to Inactive, and keeps the saved message', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/avatar-submissions')) return jsonResponse([]);
      if (path.includes('/admin/player-data/unverified')) return jsonResponse([]);
      if (path.includes('/admin/rounds/xg-grid/active')) return bareNotFound();
      if (path.includes('/admin/announcement-banner/deactivate')) {
        return jsonResponse({ ...loadedActiveBanner, isActive: false });
      }
      if (path.includes('/admin/announcement-banner')) return jsonResponse(loadedActiveBanner);
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AdminScreen accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />);
    // REQ-516: AnnouncementBannerSection now lives in the "Announcements"
    // nav group, which isn't the default ("Users") — select it before
    // interacting.
    await user.click(await screen.findByRole('tab', { name: 'Announcements' }));
    await screen.findByText('Status: Active — visible to every visitor');

    await user.click(screen.getByRole('button', { name: 'Deactivate' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/admin/announcement-banner/deactivate'),
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    expect(await screen.findByText('Status: Inactive — not shown to visitors')).toBeInTheDocument();
    // Same effect-timing race as the "Active" banner test above — see the
    // comment there.
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue(loadedActiveBanner.message));
    expect(screen.getByRole('button', { name: 'Activate' })).toBeInTheDocument();
  });
});

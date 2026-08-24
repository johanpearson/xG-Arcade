import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SuggestionsScreen } from './SuggestionsScreen';
import type { PendingSuggestion } from '../lib/types';

// S-090 (docs/backlog.md), REQ-509/REQ-510: SuggestionsScreen's own suite —
// mirrors AdminScreen.test.tsx's conventions exactly (jsonResponse/
// bareNotFound helpers, vi.stubGlobal('fetch', ...), userEvent for
// interaction) since ui-implementer's own header comment on
// SuggestionsScreen.tsx says it "follows AdminScreen's exact PageState/
// loading/401/403 shape rather than inventing a new one."

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function noContentResponse(status = 204) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.reject(new Error('no body')),
  } as unknown as Response);
}

const suggestion1: PendingSuggestion = {
  id: 'sugg-1',
  playerName: 'Clarence Seedorf',
  assertedClubs: ['AC Milan'],
  assertedNationality: 'Netherlands',
  submittingUserId: 'user-1',
  submittingUserDisplayName: 'Jane Doe',
  rowCategoryType: 'club',
  colCategoryType: 'club',
  createdAt: '2026-08-01T10:00:00Z',
};

const foundLookupResult = {
  found: true,
  wikidataQid: 'Q188207',
  fullName: 'Clarence Seedorf',
  nationality: 'Netherlands',
  clubs: ['AC Milan', 'Real Madrid'],
  existingPlayerId: null,
};

// REQ-515: same lookup result, but resolved to a wikidataQid that already
// has a local Player row — the one signal that gates the inline "Refresh
// from Wikidata" action's visibility.
const foundLookupResultWithExistingPlayer = {
  ...foundLookupResult,
  existingPlayerId: 'player-existing-1',
};

describe('SuggestionsScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  // ---- REQ-509: pending list -------------------------------------------

  it('REQ509: renders a pending suggestion row with player name, clubs, nationality, submitter, and timestamp', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse([suggestion1])),
    );

    render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);

    expect(await screen.findByText('Clarence Seedorf')).toBeInTheDocument();
    expect(screen.getByText('Claimed clubs: AC Milan')).toBeInTheDocument();
    expect(screen.getByText('Claimed nationality: Netherlands')).toBeInTheDocument();
    expect(screen.getByText('Submitted by Jane Doe · 2026-08-01T10:00:00Z')).toBeInTheDocument();
  });

  it('REQ509: shows an empty state when there are no pending suggestions', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse([])),
    );

    render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);

    expect(await screen.findByText('No pending suggestions to review.')).toBeInTheDocument();
    expect(screen.queryByText('Clarence Seedorf')).not.toBeInTheDocument();
  });

  // ---- REQ-509: review flow — loading, found, commit ---------------------

  it('REQ509: triggering a review shows a loading state, then found:true renders fetched data alongside the original claim with editable fields, and commit removes the row from the pending list', async () => {
    let listCallCount = 0;
    // A deferred promise for the lookup call specifically, so the loading
    // state can be observed deterministically instead of racing an
    // already-resolved mock promise's microtask flush.
    let resolveLookup: (() => void) | undefined;
    const lookupPromise = new Promise<void>((resolve) => {
      resolveLookup = resolve;
    });
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      const method = init?.method ?? 'GET';
      if (path.endsWith('/admin/suggestions') && method === 'GET') {
        listCallCount += 1;
        return jsonResponse(listCallCount === 1 ? [suggestion1] : []);
      }
      if (path.includes('/admin/suggestions/sugg-1/lookup')) {
        return lookupPromise.then(() => jsonResponse(foundLookupResult)).then((v) => v);
      }
      if (path.includes('/admin/suggestions/sugg-1/commit')) {
        return jsonResponse({
          playerId: 'player-1',
          playerCreated: false,
          nationality: 'Netherlands',
          nationalityWritten: true,
          clubsAdded: ['Real Madrid'],
          clubsAlreadyEffective: ['AC Milan'],
        });
      }
      throw new Error(`Unexpected fetch: ${method} ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);
    await screen.findByText('Clarence Seedorf');

    await user.click(screen.getByRole('button', { name: 'Review' }));
    expect(screen.getByText('Looking up player on Wikidata…')).toBeInTheDocument();

    resolveLookup?.();
    expect(await screen.findByText('Suggested by player')).toBeInTheDocument();
    expect(screen.getByText('Fetched from Wikidata')).toBeInTheDocument();
    // REQ-515: the fetched wikidataQid is always visible, even when there's
    // no existing local Player row to refresh (this fixture's
    // existingPlayerId is null).
    expect(screen.getByText('Wikidata ID: Q188207')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Refresh from Wikidata' })).not.toBeInTheDocument();
    // Original claim (from the suggestion) and the fetched Wikidata data are
    // both presented, distinctly, for admin judgment — never auto-approved.
    const comparisonClubLines = screen.getAllByText(/^Clubs: /);
    expect(comparisonClubLines).toHaveLength(2);
    expect(comparisonClubLines[0]).toHaveTextContent('Clubs: AC Milan');
    expect(comparisonClubLines[1]).toHaveTextContent('Clubs: AC Milan, Real Madrid');

    // Editable fields, pre-filled from the fetch but not read-only.
    expect(screen.getByLabelText('Full name')).toHaveValue('Clarence Seedorf');
    expect(screen.getByLabelText('Nationality')).toHaveValue('Netherlands');
    expect(screen.getByLabelText('Clubs (one per line)')).toHaveValue('AC Milan\nReal Madrid');

    await user.type(screen.getByLabelText('Reason'), 'Confirmed via live Wikidata lookup');
    await user.click(screen.getByRole('button', { name: 'Commit' }));

    await waitFor(() => {
      const commitCall = fetchMock.mock.calls.find(([url]) => String(url).includes('/admin/suggestions/sugg-1/commit'));
      expect(commitCall).toBeDefined();
    });
    expect(await screen.findByText('No pending suggestions to review.')).toBeInTheDocument();
    expect(screen.queryByText('Clarence Seedorf')).not.toBeInTheDocument();
    // REQ-509/S-129: the row is gone, but the panel's own commit response is
    // still surfaced — specific about what was actually written, not a
    // generic success string.
    expect(
      await screen.findByText(
        'Nationality set to Netherlands. 1 new club added: Real Madrid. AC Milan already up to date.',
      ),
    ).toBeInTheDocument();
  });

  // ---- S-129: commit confirmation reflects what was actually written -----

  it('REQ509/S129: a genuine no-op commit (everything already effective) says so plainly, not a generic success message', async () => {
    let listCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      const method = init?.method ?? 'GET';
      if (path.endsWith('/admin/suggestions') && method === 'GET') {
        listCallCount += 1;
        return jsonResponse(listCallCount === 1 ? [suggestion1] : []);
      }
      if (path.includes('/admin/suggestions/sugg-1/lookup')) {
        return jsonResponse(foundLookupResult);
      }
      if (path.includes('/admin/suggestions/sugg-1/commit')) {
        return jsonResponse({
          playerId: 'player-1',
          playerCreated: false,
          nationality: null,
          nationalityWritten: false,
          clubsAdded: [],
          clubsAlreadyEffective: ['AC Milan', 'Real Madrid'],
        });
      }
      throw new Error(`Unexpected fetch: ${method} ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);
    await screen.findByText('Clarence Seedorf');
    await user.click(screen.getByRole('button', { name: 'Review' }));
    await screen.findByText('Suggested by player');

    // This commit only confirms clubs that are already effective.
    await user.clear(screen.getByLabelText('Nationality'));
    await user.click(screen.getByRole('button', { name: 'Commit' }));

    expect(
      await screen.findByText('No changes — this data was already up to date.'),
    ).toBeInTheDocument();
  });

  it('REQ509/ADR-0060: a clubs-only commit (no nationality) succeeds without a reason, since PlayerAttribute has nowhere to store one', async () => {
    let listCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      const method = init?.method ?? 'GET';
      if (path.endsWith('/admin/suggestions') && method === 'GET') {
        listCallCount += 1;
        return jsonResponse(listCallCount === 1 ? [suggestion1] : []);
      }
      if (path.includes('/admin/suggestions/sugg-1/lookup')) {
        return jsonResponse(foundLookupResult);
      }
      if (path.includes('/admin/suggestions/sugg-1/commit')) {
        return jsonResponse({
          playerId: 'player-1',
          playerCreated: false,
          nationality: null,
          nationalityWritten: false,
          clubsAdded: ['AC Milan', 'Real Madrid'],
          clubsAlreadyEffective: [],
        });
      }
      throw new Error(`Unexpected fetch: ${method} ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);
    await screen.findByText('Clarence Seedorf');
    await user.click(screen.getByRole('button', { name: 'Review' }));
    await screen.findByText('Suggested by player');

    // Clear the nationality field — this commit only confirms clubs.
    await user.clear(screen.getByLabelText('Nationality'));

    // No reason typed, and the field is not marked required for this path.
    expect(screen.getByLabelText(/^Reason/)).not.toBeRequired();
    await user.click(screen.getByRole('button', { name: 'Commit' }));

    await waitFor(() => {
      const commitCall = fetchMock.mock.calls.find(([url]) => String(url).includes('/admin/suggestions/sugg-1/commit'));
      expect(commitCall).toBeDefined();
    });
    const [, commitInit] = fetchMock.mock.calls.find(([url]) => String(url).includes('/admin/suggestions/sugg-1/commit'))!;
    const body = JSON.parse((commitInit as RequestInit).body as string);
    expect(body.reason).toBe('');
    expect(body.nationality).toBeNull();
    expect(await screen.findByText('No pending suggestions to review.')).toBeInTheDocument();
  });

  // ---- REQ-509/ADR-0046: found:false vs. 503, never conflated -----------

  it('REQ509: found:false renders a distinct "no match" state, and a 503 renders a distinct "lookup unavailable" state — never the same text', async () => {
    let notFoundMessage = '';
    let unavailableMessage = '';

    // Scenario 1: found: false.
    {
      const fetchMock = vi.fn().mockImplementation((url: string) => {
        const path = String(url);
        if (path.endsWith('/admin/suggestions')) return jsonResponse([suggestion1]);
        if (path.includes('/lookup')) return jsonResponse({ found: false, wikidataQid: null, fullName: null, nationality: null, clubs: [] });
        throw new Error(`Unexpected fetch: ${path}`);
      });
      vi.stubGlobal('fetch', fetchMock);
      const user = userEvent.setup();

      const { unmount } = render(
        <SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />,
      );
      await screen.findByText('Clarence Seedorf');
      await user.click(screen.getByRole('button', { name: 'Review' }));

      const notFoundEl = await screen.findByText(/Wikidata has no footballer matching this name/);
      notFoundMessage = notFoundEl.textContent ?? '';
      // Never the same state as a lookup failure — no "Try again" retry
      // action exists for a genuine no-match result.
      expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument();

      unmount();
      vi.unstubAllGlobals();
    }

    // Scenario 2: a 503 ("lookup unavailable").
    {
      const fetchMock = vi.fn().mockImplementation((url: string) => {
        const path = String(url);
        if (path.endsWith('/admin/suggestions')) return jsonResponse([suggestion1]);
        if (path.includes('/lookup')) {
          return jsonResponse({ title: 'Live verification unavailable', detail: "We couldn't reach Wikidata to verify this player. Please try again." }, 503);
        }
        throw new Error(`Unexpected fetch: ${path}`);
      });
      vi.stubGlobal('fetch', fetchMock);
      const user = userEvent.setup();

      render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);
      await screen.findByText('Clarence Seedorf');
      await user.click(screen.getByRole('button', { name: 'Review' }));

      const unavailableEl = await screen.findByRole('alert');
      unavailableMessage = unavailableEl.textContent ?? '';
      expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
    }

    expect(notFoundMessage).not.toEqual('');
    expect(unavailableMessage).not.toEqual('');
    expect(notFoundMessage).not.toEqual(unavailableMessage);
  });

  // ---- REQ-509/ADR-0046: 409 already-resolved, distinct + refresh path --

  it('REQ509: a 409 from lookup (already resolved) surfaces distinctly, with a refresh path back to the pending list', async () => {
    let listCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      const method = init?.method ?? 'GET';
      if (path.endsWith('/admin/suggestions') && method === 'GET') {
        listCallCount += 1;
        return jsonResponse(listCallCount === 1 ? [suggestion1] : []);
      }
      if (path.includes('/lookup')) {
        return jsonResponse({ title: 'Suggestion already resolved', detail: 'This suggestion has already been committed or rejected.' }, 409);
      }
      throw new Error(`Unexpected fetch: ${method} ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);
    await screen.findByText('Clarence Seedorf');

    await user.click(screen.getByRole('button', { name: 'Review' }));

    expect(await screen.findByText(/Already resolved by another admin/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Refresh list' }));

    expect(await screen.findByText('No pending suggestions to review.')).toBeInTheDocument();
    expect(listCallCount).toBeGreaterThanOrEqual(2);
  });

  // ---- REQ-509: reject --------------------------------------------------

  it('REQ509: reject removes the suggestion from the pending list without any commit-shaped request being sent', async () => {
    let listCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      const method = init?.method ?? 'GET';
      if (path.endsWith('/admin/suggestions') && method === 'GET') {
        listCallCount += 1;
        return jsonResponse(listCallCount === 1 ? [suggestion1] : []);
      }
      if (path.includes('/lookup')) {
        return jsonResponse({ found: false, wikidataQid: null, fullName: null, nationality: null, clubs: [] });
      }
      if (path.includes('/reject')) return noContentResponse();
      if (path.includes('/commit')) throw new Error('commit must never be called by a reject action');
      throw new Error(`Unexpected fetch: ${method} ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);
    await screen.findByText('Clarence Seedorf');
    await user.click(screen.getByRole('button', { name: 'Review' }));
    await screen.findByText(/Wikidata has no footballer matching this name/);

    await user.click(screen.getByRole('button', { name: 'Reject suggestion' }));

    await waitFor(() => {
      const rejectCall = fetchMock.mock.calls.find(([url]) => String(url).includes('/reject'));
      expect(rejectCall).toBeDefined();
    });
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/commit'))).toBe(false);
    expect(await screen.findByText('No pending suggestions to review.')).toBeInTheDocument();
    expect(screen.queryByText('Clarence Seedorf')).not.toBeInTheDocument();
  });

  // ---- REQ-510: standalone manual search-and-add -------------------------

  it('REQ510: manual search-and-add never calls any /admin/suggestions/* endpoint, only /admin/player-search/*', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      const method = init?.method ?? 'GET';
      if (path.endsWith('/admin/suggestions') && method === 'GET') return jsonResponse([]);
      if (path.includes('/admin/player-search/lookup')) {
        return jsonResponse({ found: true, wikidataQid: 'Qpires', fullName: 'Robert Pires', nationality: 'France', clubs: ['Arsenal'] });
      }
      if (path.includes('/admin/player-search/commit')) {
        return jsonResponse({
          playerId: 'player-2',
          playerCreated: true,
          nationality: 'France',
          nationalityWritten: true,
          clubsAdded: ['Arsenal'],
          clubsAlreadyEffective: [],
        });
      }
      throw new Error(`Unexpected fetch: ${method} ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);
    await screen.findByText('No pending suggestions to review.');

    await user.type(screen.getByLabelText('Player name'), 'Robert Pires');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    expect(await screen.findByLabelText('Full name')).toHaveValue('Robert Pires');
    // REQ-510: no comparison-to-a-claim section exists here (claim is null) —
    // only the fetched-Wikidata side of the review panel renders.
    expect(screen.queryByText('Suggested by player')).not.toBeInTheDocument();

    await user.type(screen.getByLabelText('Reason'), 'Manually added via admin search');
    await user.click(screen.getByRole('button', { name: 'Commit' }));

    // S-129: no longer the generic, content-free "Player data committed." —
    // the real commit response is reflected instead.
    await waitFor(() => {
      expect(screen.queryByText('Player data committed.')).not.toBeInTheDocument();
      expect(
        screen.getByText('New player added. Nationality set to France. 1 new club added: Arsenal.'),
      ).toBeInTheDocument();
    });

    const calledUrls = fetchMock.mock.calls.map(([url]) => String(url));
    expect(calledUrls.some((url) => url.includes('/admin/player-search/lookup'))).toBe(true);
    expect(calledUrls.some((url) => url.includes('/admin/player-search/commit'))).toBe(true);
    // The whole point of REQ-510/ADR-0053: no suggestion RECORD exists
    // before, during, or after this action. The plain list-fetch
    // (GET /admin/suggestions, with nothing after "suggestions") is the
    // screen's own always-present pending-list load, not part of the search
    // action — but a call to any suggestion-SCOPED path (with an id/segment
    // after "suggestions/", e.g. lookup/commit/reject) must never happen.
    expect(calledUrls.some((url) => /\/admin\/suggestions\//.test(url))).toBe(false);
  });

  // ---- REQ-515: inline "Refresh from Wikidata" action ---------------------

  it('REQ515: the inline refresh button is absent when the lookup found no existing local Player row', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      const method = init?.method ?? 'GET';
      if (path.endsWith('/admin/suggestions') && method === 'GET') return jsonResponse([suggestion1]);
      if (path.includes('/admin/suggestions/sugg-1/lookup')) return jsonResponse(foundLookupResult);
      throw new Error(`Unexpected fetch: ${method} ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);
    await screen.findByText('Clarence Seedorf');
    await user.click(screen.getByRole('button', { name: 'Review' }));

    await screen.findByText('Wikidata ID: Q188207');
    expect(screen.queryByRole('button', { name: 'Refresh from Wikidata' })).not.toBeInTheDocument();
  });

  it('REQ515: a non-null existingPlayerId shows the inline refresh button, which calls the REQ-513 refresh endpoint directly and renders the shared changed/unchanged field presentation', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      const method = init?.method ?? 'GET';
      if (path.endsWith('/admin/suggestions') && method === 'GET') return jsonResponse([suggestion1]);
      if (path.includes('/admin/suggestions/sugg-1/lookup')) {
        return jsonResponse(foundLookupResultWithExistingPlayer);
      }
      if (path.includes('/admin/players/player-existing-1/refresh-from-wikidata')) {
        return jsonResponse({
          playerId: 'player-existing-1',
          wikidataQid: 'Q188207',
          fields: [
            { field: 'fullName', changed: true, oldValue: 'Clarance Seedorf', newValue: 'Clarence Seedorf' },
            { field: 'position', changed: false, oldValue: 'Midfielder', newValue: null },
            { field: 'birthYear', changed: false, oldValue: '1976', newValue: null },
            { field: 'photoUrl', changed: false, oldValue: null, newValue: null },
          ],
        });
      }
      throw new Error(`Unexpected fetch: ${method} ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);
    await screen.findByText('Clarence Seedorf');
    await user.click(screen.getByRole('button', { name: 'Review' }));

    await screen.findByText('Wikidata ID: Q188207');
    const refreshButton = await screen.findByRole('button', { name: 'Refresh from Wikidata' });
    await user.click(refreshButton);

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/admin/players/player-existing-1/refresh-from-wikidata'),
      expect.objectContaining({ method: 'POST' }),
    );
    expect(
      await screen.findByText('Full name: Changed — "Clarance Seedorf" → "Clarence Seedorf"'),
    ).toBeInTheDocument();
    expect(screen.getByText('Position: Unchanged — "Midfielder"')).toBeInTheDocument();
    expect(screen.getByText('Birth year: Unchanged — "1976"')).toBeInTheDocument();
    expect(screen.getByText('Photo URL: Unchanged — "(none)"')).toBeInTheDocument();

    // No confirmation step — clicking once was enough, no separate
    // "Confirm"/"Cancel" pair ever appeared for this action.
    expect(screen.queryByRole('button', { name: 'Confirm' })).not.toBeInTheDocument();

    // The rest of the review panel (commit form) is unaffected by the
    // inline refresh action — still present and usable.
    expect(screen.getByRole('button', { name: 'Commit' })).toBeInTheDocument();
  });

  it('REQ515: the inline refresh button shows a pending, disabled state while in flight', async () => {
    let resolveRefresh: (value: Response) => void = () => {};
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      const method = init?.method ?? 'GET';
      if (path.endsWith('/admin/suggestions') && method === 'GET') return jsonResponse([suggestion1]);
      if (path.includes('/admin/suggestions/sugg-1/lookup')) {
        return jsonResponse(foundLookupResultWithExistingPlayer);
      }
      if (path.includes('/admin/players/player-existing-1/refresh-from-wikidata')) {
        return new Promise<Response>((resolve) => {
          resolveRefresh = resolve;
        });
      }
      throw new Error(`Unexpected fetch: ${method} ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);
    await screen.findByText('Clarence Seedorf');
    await user.click(screen.getByRole('button', { name: 'Review' }));

    const refreshButton = await screen.findByRole('button', { name: 'Refresh from Wikidata' });
    await user.click(refreshButton);

    expect(screen.getByRole('button', { name: 'Refreshing…' })).toBeDisabled();

    resolveRefresh(
      (await jsonResponse({
        playerId: 'player-existing-1',
        wikidataQid: 'Q188207',
        fields: [
          { field: 'fullName', changed: false, oldValue: 'Clarence Seedorf', newValue: null },
          { field: 'position', changed: false, oldValue: 'Midfielder', newValue: null },
          { field: 'birthYear', changed: false, oldValue: '1976', newValue: null },
          { field: 'photoUrl', changed: false, oldValue: null, newValue: null },
        ],
      })) as unknown as Response,
    );

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Refresh from Wikidata' })).toBeEnabled(),
    );
  });

  it('REQ515: a 409 from the inline refresh endpoint reuses REQ-514\'s exact "no Wikidata id" message, not a new one', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      const method = init?.method ?? 'GET';
      if (path.endsWith('/admin/suggestions') && method === 'GET') return jsonResponse([suggestion1]);
      if (path.includes('/admin/suggestions/sugg-1/lookup')) {
        return jsonResponse(foundLookupResultWithExistingPlayer);
      }
      if (path.includes('/admin/players/player-existing-1/refresh-from-wikidata')) {
        return jsonResponse(
          {
            title: 'No Wikidata QID to refresh from',
            detail: 'This player has no WikidataQid on record — there is nothing to refresh from.',
          },
          409,
        );
      }
      throw new Error(`Unexpected fetch: ${method} ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);
    await screen.findByText('Clarence Seedorf');
    await user.click(screen.getByRole('button', { name: 'Review' }));

    const refreshButton = await screen.findByRole('button', { name: 'Refresh from Wikidata' });
    await user.click(refreshButton);

    expect(
      await screen.findByText('This player has no Wikidata id to refresh from.'),
    ).toBeInTheDocument();
    // The commit form's own error state is untouched by this action's error.
    expect(screen.queryByRole('button', { name: 'Commit' })).toBeInTheDocument();
  });

  it('REQ515: a 401 from the inline refresh endpoint calls onAuthError instead of showing a panel-local message', async () => {
    const onAuthError = vi.fn();
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      const method = init?.method ?? 'GET';
      if (path.endsWith('/admin/suggestions') && method === 'GET') return jsonResponse([suggestion1]);
      if (path.includes('/admin/suggestions/sugg-1/lookup')) {
        return jsonResponse(foundLookupResultWithExistingPlayer);
      }
      if (path.includes('/admin/players/player-existing-1/refresh-from-wikidata')) {
        return jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401);
      }
      throw new Error(`Unexpected fetch: ${method} ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<SuggestionsScreen accessToken="token" onAuthError={onAuthError} onBackToAdmin={vi.fn()} />);
    await screen.findByText('Clarence Seedorf');
    await user.click(screen.getByRole('button', { name: 'Review' }));

    const refreshButton = await screen.findByRole('button', { name: 'Refresh from Wikidata' });
    await user.click(refreshButton);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ515: the manual-search entry point (ManualSearchSection) also shows the inline refresh action when existingPlayerId is present — the shared PlayerReviewPanel, not a second copy', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      const method = init?.method ?? 'GET';
      if (path.endsWith('/admin/suggestions') && method === 'GET') return jsonResponse([]);
      if (path.includes('/admin/player-search/lookup')) {
        return jsonResponse({
          found: true,
          wikidataQid: 'Qpires',
          fullName: 'Robert Pires',
          nationality: 'France',
          clubs: ['Arsenal'],
          existingPlayerId: 'player-pires-1',
        });
      }
      if (path.includes('/admin/players/player-pires-1/refresh-from-wikidata')) {
        return jsonResponse({
          playerId: 'player-pires-1',
          wikidataQid: 'Qpires',
          fields: [
            { field: 'fullName', changed: false, oldValue: 'Robert Pires', newValue: null },
            { field: 'position', changed: false, oldValue: 'Midfielder', newValue: null },
            { field: 'birthYear', changed: false, oldValue: '1973', newValue: null },
            { field: 'photoUrl', changed: false, oldValue: null, newValue: null },
          ],
        });
      }
      throw new Error(`Unexpected fetch: ${method} ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<SuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);
    await screen.findByText('No pending suggestions to review.');

    await user.type(screen.getByLabelText('Player name'), 'Robert Pires');
    await user.click(screen.getByRole('button', { name: 'Search' }));

    await screen.findByText('Wikidata ID: Qpires');
    const refreshButton = await screen.findByRole('button', { name: 'Refresh from Wikidata' });
    await user.click(refreshButton);

    expect(
      await screen.findByText('Full name: Unchanged — "Robert Pires"'),
    ).toBeInTheDocument();
  });

  // ---- Auth: 401/403 (mirrors AdminScreen.test.tsx's own convention) ----

  it('REQ509/510: a 401 from the pending-suggestions fetch calls onAuthError, the same callback AdminScreen.tsx uses', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );

    render(<SuggestionsScreen accessToken="token" onAuthError={onAuthError} onBackToAdmin={vi.fn()} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ509/510: a 403 from the pending-suggestions fetch shows an access-denied state for the whole screen', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403)),
    );

    render(<SuggestionsScreen accessToken="token" onAuthError={onAuthError} onBackToAdmin={vi.fn()} />);

    expect(await screen.findByText("You don't have access to this page.")).toBeInTheDocument();
    expect(onAuthError).not.toHaveBeenCalled();
  });
});

import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LeaguesScreen } from './LeaguesScreen';
import type { CustomLeague } from '../lib/types';

// REQ-402/403: isolated coverage of LeaguesScreen's own create/join/list
// behavior, mounted directly (no App/routing involved) — same convention
// every other screen in this codebase already has (SettingsScreen.test.tsx,
// AdminScreen.test.tsx). App.test.tsx is not extended with a matching
// "navigate to Leagues" case here, since this file already covers the
// component's behavior directly and every other screen's own dedicated
// suite follows the same split.
function renderLeaguesScreen(
  overrides: Partial<Parameters<typeof LeaguesScreen>[0]> = {},
  fetchImpl: ReturnType<typeof vi.fn> = vi.fn(),
) {
  vi.stubGlobal('fetch', fetchImpl);

  const onAuthError = vi.fn();

  render(<LeaguesScreen accessToken="token" onAuthError={onAuthError} {...overrides} />);

  return { onAuthError };
}

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function problemResponse(title: string, detail: string, status: number) {
  return jsonResponse({ title, detail }, status);
}

// A small stateful fake backend: GET /leagues/mine always reflects whatever
// is currently in `leagues`; POST /leagues appends a new one (using the
// invite code exactly as CreatedInviteCode below); POST /leagues/join
// resolves to whichever seeded league matches inviteCode, or 404s.
function createFakeLeaguesBackend(initialLeagues: CustomLeague[] = []) {
  const leagues = [...initialLeagues];
  let nextId = 1;
  const CreatedInviteCode = 'NEWCODE';

  const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';

    if (url.includes('/leagues/mine') && method === 'GET') {
      // A snapshot, not the live mutable array — a real HTTP response
      // always deserializes into a fresh array, so this fake must too:
      // returning the same array reference this backend still mutates
      // in place would make React's setState bail out (Object.is) on a
      // later, in-place-mutated "update" that's really the identical
      // object, silently dropping the re-render this test needs to see.
      return jsonResponse([...leagues]);
    }

    if (url.includes('/leagues/join') && method === 'POST') {
      const body = JSON.parse(String(init?.body)) as { inviteCode: string };
      const match = leagues.find((l) => l.inviteCode === body.inviteCode);
      if (!match) {
        return problemResponse(
          'Invalid invite code',
          `No league found with invite code '${body.inviteCode}'.`,
          404,
        );
      }
      return jsonResponse(match);
    }

    if (url.endsWith('/leagues') && method === 'POST') {
      const body = JSON.parse(String(init?.body)) as { name: string };
      const created: CustomLeague = { id: `league-${nextId++}`, name: body.name, inviteCode: CreatedInviteCode };
      leagues.push(created);
      return jsonResponse(created);
    }

    throw new Error(`Unexpected fetch: ${method} ${url}`);
  });

  return { fetchMock, leagues };
}

describe('LeaguesScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-402/403: renders a "Leagues" heading once loaded', async () => {
    const { fetchMock } = createFakeLeaguesBackend([]);
    renderLeaguesScreen({}, fetchMock);

    expect(await screen.findByRole('heading', { name: 'Leagues' })).toBeInTheDocument();
  });

  it('REQ-402/403: shows an empty-state invitation when the player has no custom leagues yet', async () => {
    const { fetchMock } = createFakeLeaguesBackend([]);
    renderLeaguesScreen({}, fetchMock);

    expect(await screen.findByText("You're not in any custom leagues yet.")).toBeInTheDocument();
  });

  it('REQ-402/403: lists each of the player\'s custom leagues by name and invite code', async () => {
    const { fetchMock } = createFakeLeaguesBackend([
      { id: 'league-1', name: 'Friends League', inviteCode: 'ABC123' },
      { id: 'league-2', name: 'Work League', inviteCode: 'XYZ789' },
    ]);
    renderLeaguesScreen({}, fetchMock);

    expect(await screen.findByText('Friends League')).toBeInTheDocument();
    expect(screen.getByText('Work League')).toBeInTheDocument();
    expect(screen.getByText('Code: ABC123')).toBeInTheDocument();
    expect(screen.getByText('Code: XYZ789')).toBeInTheDocument();
  });

  it('REQ-402/403: an initial load failure (non-401) shows an inline error, not the form', async () => {
    const fetchMock = vi.fn().mockImplementation(() => problemResponse('Request failed', 'Server error.', 500));
    renderLeaguesScreen({}, fetchMock);

    expect(await screen.findByText('Server error.')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Leagues' })).not.toBeInTheDocument();
  });

  it('REQ-402/403: an initial load 401 calls onAuthError', async () => {
    const fetchMock = vi.fn().mockImplementation(() => problemResponse('Unauthorized', 'Unauthorized', 401));
    const { onAuthError } = renderLeaguesScreen({}, fetchMock);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  // ---- REQ-402: create a league -----------------------------------------

  it('REQ-402: rejects an empty league name client-side, without calling the API', async () => {
    const { fetchMock } = createFakeLeaguesBackend([]);
    const user = userEvent.setup();
    renderLeaguesScreen({}, fetchMock);
    await screen.findByRole('heading', { name: 'Leagues' });
    fetchMock.mockClear();

    await user.click(screen.getByRole('button', { name: 'Create league' }));

    expect(
      await screen.findByText('League name must be between 1 and 50 characters.'),
    ).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('REQ-402: creating a league with a name calls POST /leagues and the new league then appears in "My leagues"', async () => {
    const { fetchMock } = createFakeLeaguesBackend([]);
    const user = userEvent.setup();
    renderLeaguesScreen({}, fetchMock);
    await screen.findByText("You're not in any custom leagues yet.");

    await user.type(screen.getByLabelText('League name'), 'Friends League');
    await user.click(screen.getByRole('button', { name: 'Create league' }));

    expect(await screen.findByText('Friends League')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringMatching(/\/leagues$/),
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ name: 'Friends League' }) }),
    );
  });

  it('REQ-402: a 401 while creating a league calls onAuthError', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(() => jsonResponse([])) // initial GET /leagues/mine
      .mockImplementationOnce(() => problemResponse('Unauthorized', 'Unauthorized', 401)); // POST /leagues
    const user = userEvent.setup();
    const { onAuthError } = renderLeaguesScreen({}, fetchMock);
    await screen.findByText("You're not in any custom leagues yet.");

    await user.type(screen.getByLabelText('League name'), 'Friends League');
    await user.click(screen.getByRole('button', { name: 'Create league' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  // ---- REQ-403: join a league via code -----------------------------------

  it('REQ-403: rejects an empty invite code client-side, without calling the API', async () => {
    const { fetchMock } = createFakeLeaguesBackend([]);
    const user = userEvent.setup();
    renderLeaguesScreen({}, fetchMock);
    await screen.findByRole('heading', { name: 'Leagues' });
    fetchMock.mockClear();

    await user.click(screen.getByRole('button', { name: 'Join league' }));

    expect(await screen.findByText('Invite code is required.')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('REQ-403: joining with a valid invite code calls POST /leagues/join and the league then appears in "My leagues"', async () => {
    const { fetchMock } = createFakeLeaguesBackend([
      { id: 'league-1', name: "Friend's League", inviteCode: 'ABC123' },
    ]);
    const user = userEvent.setup();
    renderLeaguesScreen({}, fetchMock);
    await screen.findByRole('heading', { name: 'Leagues' });

    await user.type(screen.getByLabelText('Invite code'), 'ABC123');
    await user.click(screen.getByRole('button', { name: 'Join league' }));

    expect(await screen.findByText("Friend's League")).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/leagues/join'),
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ inviteCode: 'ABC123' }) }),
    );
  });

  it('REQ-403: an invalid invite code shows the server\'s own clear error inline and creates no membership (list unchanged)', async () => {
    const { fetchMock } = createFakeLeaguesBackend([]);
    const user = userEvent.setup();
    renderLeaguesScreen({}, fetchMock);
    await screen.findByText("You're not in any custom leagues yet.");

    await user.type(screen.getByLabelText('Invite code'), 'NOSUCH1');
    await user.click(screen.getByRole('button', { name: 'Join league' }));

    expect(
      await screen.findByText("No league found with invite code 'NOSUCH1'."),
    ).toBeInTheDocument();
    expect(screen.getByText("You're not in any custom leagues yet.")).toBeInTheDocument();
  });

  it('REQ-403: a 401 while joining a league calls onAuthError', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(() => jsonResponse([])) // initial GET /leagues/mine
      .mockImplementationOnce(() => problemResponse('Unauthorized', 'Unauthorized', 401)); // POST /leagues/join
    const user = userEvent.setup();
    const { onAuthError } = renderLeaguesScreen({}, fetchMock);
    await screen.findByText("You're not in any custom leagues yet.");

    await user.type(screen.getByLabelText('Invite code'), 'ABC123');
    await user.click(screen.getByRole('button', { name: 'Join league' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });
});

import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ChallengesTab } from './ChallengesTab';

// REQ-1402 (S-217): isolated coverage of ChallengesTab's own pending-list/
// accept/decline/match-created-acknowledgment behavior, plus the
// visibility-fix "Sent challenges" section (S-230) below.

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

function renderChallengesTab(overrides: Partial<Parameters<typeof ChallengesTab>[0]> = {}, fetchMock = vi.fn()) {
  vi.stubGlobal('fetch', fetchMock);
  const onAuthError = vi.fn();
  const onViewMatches = vi.fn();
  render(<ChallengesTab accessToken="token" onAuthError={onAuthError} onViewMatches={onViewMatches} {...overrides} />);
  return { onAuthError, onViewMatches };
}

const pendingChallenge = {
  id: 'challenge-1',
  challengerUserId: 'a1b2c3d4-0000-0000-0000-000000000000',
  challengerDisplayName: 'Alex',
  challengedUserId: 'me',
  challengedDisplayName: 'Me',
  status: 'Pending',
  createdAt: '2026-01-01T00:00:00Z',
  resolvedAt: null,
  resultingMatchId: null,
};

const sentChallenge = {
  id: 'challenge-2',
  challengerUserId: 'me',
  challengerDisplayName: 'Me',
  challengedUserId: 'b2c3d4e5-0000-0000-0000-000000000000',
  challengedDisplayName: 'Robin',
  status: 'Pending',
  createdAt: '2026-01-01T00:00:00Z',
  resolvedAt: null,
  resultingMatchId: null,
};

// Every test below drives GET /challenges/pending and GET /challenges/sent
// independently (ChallengesTab now fetches both on mount, REQ-1402
// visibility fix, S-230) — defaults both to `[]` unless a test overrides
// one. `pending`/`sent` may be a plain array (fixed response) or a thunk
// (re-evaluated on every GET — needed by the accept/decline tests below,
// where the response must reflect state a preceding POST just changed).
type ListOrThunk = unknown[] | (() => unknown[]);
function resolveList(value: ListOrThunk | undefined): unknown[] {
  if (value === undefined) return [];
  return typeof value === 'function' ? value() : value;
}
function challengesFetchMock({
  pending,
  sent,
  onPost,
}: {
  pending?: ListOrThunk;
  sent?: ListOrThunk;
  onPost?: (url: string) => Promise<Response> | undefined;
} = {}) {
  return vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';
    if (method === 'POST' && onPost) {
      const result = onPost(url);
      if (result) return result;
    }
    if (url.includes('/challenges/pending')) return jsonResponse(resolveList(pending));
    if (url.includes('/challenges/sent')) return jsonResponse(resolveList(sent));
    throw new Error(`Unexpected fetch: ${url}`);
  });
}

describe('ChallengesTab', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1402: shows a plain "No pending challenges." empty state', async () => {
    renderChallengesTab({}, challengesFetchMock());

    expect(await screen.findByText('No pending challenges.')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Challenges' })).toBeInTheDocument();
  });

  it('REQ-1402: renders each pending challenge with its challengerDisplayName label and an inline "(N)" heading count', async () => {
    renderChallengesTab({}, challengesFetchMock({ pending: [pendingChallenge] }));

    expect(await screen.findByRole('heading', { name: 'Challenges (1)' })).toBeInTheDocument();
    expect(screen.getByText('Alex challenged you')).toBeInTheDocument();
  });

  it('REQ-1402: accepting shows the "Match started!" acknowledgment and the row disappears once refetched', async () => {
    let resolved = false;
    const fetchMock = challengesFetchMock({
      pending: () => (resolved ? [] : [pendingChallenge]),
      onPost: (url) => {
        if (url.includes('/challenges/challenge-1/accept')) {
          resolved = true;
          return jsonResponse({ ...pendingChallenge, status: 'Accepted', resultingMatchId: 'match-1' });
        }
        return undefined;
      },
    });
    const user = userEvent.setup();
    const { onViewMatches } = renderChallengesTab({}, fetchMock);

    await screen.findByText('Alex challenged you');
    await user.click(screen.getByRole('button', { name: 'Accept' }));

    expect(await screen.findByText('Match started!')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText('No pending challenges.')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'View your matches' }));
    expect(onViewMatches).toHaveBeenCalledTimes(1);
  });

  it('REQ-1402: declining calls POST .../decline and the row disappears once refetched, with no acknowledgment banner', async () => {
    let resolved = false;
    const fetchMock = challengesFetchMock({
      pending: () => (resolved ? [] : [pendingChallenge]),
      onPost: (url) => {
        if (url.includes('/challenges/challenge-1/decline')) {
          resolved = true;
          return jsonResponse({ ...pendingChallenge, status: 'Declined' });
        }
        return undefined;
      },
    });
    const user = userEvent.setup();
    renderChallengesTab({}, fetchMock);

    await screen.findByText('Alex challenged you');
    await user.click(screen.getByRole('button', { name: 'Decline' }));

    await waitFor(() => expect(screen.getByText('No pending challenges.')).toBeInTheDocument());
    expect(screen.queryByText('Match started!')).not.toBeInTheDocument();
  });

  it('REQ-1402: a 409 while accepting shows the server\'s own detail text inline on that row', async () => {
    const fetchMock = challengesFetchMock({
      pending: [pendingChallenge],
      onPost: (url) => {
        if (url.includes('/challenges/challenge-1/accept')) {
          return problemResponse('Already resolved', 'This challenge has already been accepted or declined.', 409);
        }
        return undefined;
      },
    });
    const user = userEvent.setup();
    renderChallengesTab({}, fetchMock);

    await screen.findByText('Alex challenged you');
    await user.click(screen.getByRole('button', { name: 'Accept' }));

    expect(await screen.findByText('This challenge has already been accepted or declined.')).toBeInTheDocument();
  });

  it('REQ-1402: a 401 while declining calls onAuthError', async () => {
    const fetchMock = challengesFetchMock({
      pending: [pendingChallenge],
      onPost: (url) => {
        if (url.includes('/challenges/challenge-1/decline')) {
          return problemResponse('Unauthorized', 'Unauthorized', 401);
        }
        return undefined;
      },
    });
    const user = userEvent.setup();
    const { onAuthError } = renderChallengesTab({}, fetchMock);

    await screen.findByText('Alex challenged you');
    await user.click(screen.getByRole('button', { name: 'Decline' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  // ---- REQ-1402 visibility fix (S-230): "Sent challenges" section --------

  it('REQ-1402 (S-230): shows a plain "No pending challenges sent." empty state', async () => {
    renderChallengesTab({}, challengesFetchMock());

    expect(await screen.findByText('No pending challenges sent.')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Sent challenges' })).toBeInTheDocument();
  });

  it('REQ-1402 (S-230): renders each sent challenge with its challengedDisplayName and an inline "(N)" heading count, with no Accept/Decline actions', async () => {
    renderChallengesTab({}, challengesFetchMock({ sent: [sentChallenge] }));

    expect(await screen.findByRole('heading', { name: 'Sent challenges (1)' })).toBeInTheDocument();
    expect(screen.getByText('Waiting on Robin')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Accept' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Decline' })).not.toBeInTheDocument();
  });

  it('REQ-1402 (S-230): the received-challenges and sent-challenges sections are independent — a row in one never appears in the other', async () => {
    renderChallengesTab({}, challengesFetchMock({ pending: [pendingChallenge], sent: [sentChallenge] }));

    expect(await screen.findByText('Alex challenged you')).toBeInTheDocument();
    expect(screen.getByText('Waiting on Robin')).toBeInTheDocument();
  });
});

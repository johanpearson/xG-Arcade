import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ChallengesTab } from './ChallengesTab';

// REQ-1402 (S-217): isolated coverage of ChallengesTab's own pending-list/
// accept/decline/match-created-acknowledgment behavior.

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
  render(<ChallengesTab accessToken="token" onAuthError={onAuthError} {...overrides} />);
  return { onAuthError };
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

describe('ChallengesTab', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1402: shows a plain "No pending challenges." empty state', async () => {
    renderChallengesTab({}, vi.fn().mockImplementation(() => jsonResponse([])));

    expect(await screen.findByText('No pending challenges.')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Challenges' })).toBeInTheDocument();
  });

  it('REQ-1402: renders each pending challenge with its challengerDisplayName label and an inline "(N)" heading count', async () => {
    renderChallengesTab({}, vi.fn().mockImplementation(() => jsonResponse([pendingChallenge])));

    expect(await screen.findByRole('heading', { name: 'Challenges (1)' })).toBeInTheDocument();
    expect(screen.getByText('Alex challenged you')).toBeInTheDocument();
  });

  it('REQ-1402: accepting shows the "Match started!" acknowledgment and the row disappears once refetched', async () => {
    let resolved = false;
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.includes('/challenges/pending')) return jsonResponse(resolved ? [] : [pendingChallenge]);
      if (url.includes('/challenges/challenge-1/accept') && method === 'POST') {
        resolved = true;
        return jsonResponse({ ...pendingChallenge, status: 'Accepted', resultingMatchId: 'match-1' });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    renderChallengesTab({}, fetchMock);

    await screen.findByText('Alex challenged you');
    await user.click(screen.getByRole('button', { name: 'Accept' }));

    expect(await screen.findByText("Match started! You'll be able to play it soon.")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText('No pending challenges.')).toBeInTheDocument());
  });

  it('REQ-1402: declining calls POST .../decline and the row disappears once refetched, with no acknowledgment banner', async () => {
    let resolved = false;
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.includes('/challenges/pending')) return jsonResponse(resolved ? [] : [pendingChallenge]);
      if (url.includes('/challenges/challenge-1/decline') && method === 'POST') {
        resolved = true;
        return jsonResponse({ ...pendingChallenge, status: 'Declined' });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    renderChallengesTab({}, fetchMock);

    await screen.findByText('Alex challenged you');
    await user.click(screen.getByRole('button', { name: 'Decline' }));

    await waitFor(() => expect(screen.getByText('No pending challenges.')).toBeInTheDocument());
    expect(screen.queryByText("Match started! You'll be able to play it soon.")).not.toBeInTheDocument();
  });

  it('REQ-1402: a 409 while accepting shows the server\'s own detail text inline on that row', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.includes('/challenges/pending')) return jsonResponse([pendingChallenge]);
      if (url.includes('/challenges/challenge-1/accept') && method === 'POST') {
        return problemResponse('Already resolved', 'This challenge has already been accepted or declined.', 409);
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    renderChallengesTab({}, fetchMock);

    await screen.findByText('Alex challenged you');
    await user.click(screen.getByRole('button', { name: 'Accept' }));

    expect(await screen.findByText('This challenge has already been accepted or declined.')).toBeInTheDocument();
  });

  it('REQ-1402: a 401 while declining calls onAuthError', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.includes('/challenges/pending')) return jsonResponse([pendingChallenge]);
      if (url.includes('/challenges/challenge-1/decline') && method === 'POST') {
        return problemResponse('Unauthorized', 'Unauthorized', 401);
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onAuthError } = renderChallengesTab({}, fetchMock);

    await screen.findByText('Alex challenged you');
    await user.click(screen.getByRole('button', { name: 'Decline' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });
});

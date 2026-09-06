import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DisputeReview } from './DisputeReview';
import type { DisputeReviewProps } from './DisputeReview';
import type { ChainStepDisputeListItem } from '../lib/types';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function renderReview(overrides: Partial<DisputeReviewProps> = {}, fetchMock = vi.fn()) {
  vi.stubGlobal('fetch', fetchMock);
  const onAuthError = vi.fn();
  const onReviewed = vi.fn();
  const result = render(
    <DisputeReview matchId="match-1" accessToken="token" onAuthError={onAuthError} onReviewed={onReviewed} {...overrides} />,
  );
  return { onAuthError, onReviewed, ...result };
}

const myPending: ChainStepDisputeListItem = {
  disputeId: 'dispute-1',
  chainStepId: 'step-1',
  position: 2,
  claimedClubName: 'Chelsea',
  status: 'Pending',
  raisedAt: '2026-09-03T00:00:00Z',
  reviewedAt: null,
  raisedByMe: true,
};

const opponentPending: ChainStepDisputeListItem = {
  ...myPending,
  disputeId: 'dispute-2',
  raisedByMe: false,
};

// REQ-1412/1413 (design-document.md SCREEN-16 addendum, ADR-0109): the
// opponent-review half of the dispute flow. Mirrors MatchChat.test.tsx's own
// conventions for a sibling component with its own independent fetch+poll
// (useAuthedFetch mount fetch, own POST actions via useSubmitAction).
describe('DisputeReview', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1412/1413: renders nothing when there are no disputes at all', async () => {
    const { container } = renderReview({}, vi.fn().mockImplementation(() => jsonResponse([])));

    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });

  it('REQ-1412: a raisedByMe: true, status: Pending dispute shows the "waiting for your opponent" status text with no approve/deny buttons', async () => {
    renderReview({}, vi.fn().mockImplementation(() => jsonResponse([myPending])));

    expect(
      await screen.findByText('Waiting for your opponent to review your dispute on step 2: you claimed Chelsea.'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Deny' })).not.toBeInTheDocument();
  });

  it('REQ-1413: a raisedByMe: false, status: Pending dispute shows an actionable card; Approve calls approveChainStepDispute, refetches, and calls onReviewed', async () => {
    let listCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/matches/match-1/disputes') && method === 'GET') {
        listCallCount += 1;
        return jsonResponse(
          listCallCount === 1 ? [opponentPending] : [{ ...opponentPending, status: 'Approved', reviewedAt: '2026-09-04T00:00:00Z' }],
        );
      }
      if (url.endsWith('/matches/match-1/disputes/dispute-2/approve') && method === 'POST') {
        return jsonResponse({
          disputeId: 'dispute-2',
          chainStepId: 'step-1',
          claimedClubName: 'Chelsea',
          status: 'Approved',
          raisedAt: '2026-09-03T00:00:00Z',
          reviewedAt: '2026-09-04T00:00:00Z',
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onReviewed } = renderReview({}, fetchMock);

    expect(
      await screen.findByText(/Your opponent disputed step 2, claiming they played together at/),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Approve' }));

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/matches/match-1/disputes/dispute-2/approve'),
      expect.objectContaining({ method: 'POST' }),
    );
    await waitFor(() => expect(onReviewed).toHaveBeenCalledTimes(1));
    // The refetch after approving comes back Approved — no longer actionable
    // or listed at all (REQ-1413's own "no resolved-dispute history view").
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument());
  });

  it('REQ-1413: Deny calls denyChainStepDispute, refetches, and calls onReviewed', async () => {
    let listCallCount = 0;
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/matches/match-1/disputes') && method === 'GET') {
        listCallCount += 1;
        return jsonResponse(
          listCallCount === 1 ? [opponentPending] : [{ ...opponentPending, status: 'Denied', reviewedAt: '2026-09-04T00:00:00Z' }],
        );
      }
      if (url.endsWith('/matches/match-1/disputes/dispute-2/deny') && method === 'POST') {
        return jsonResponse({
          disputeId: 'dispute-2',
          chainStepId: 'step-1',
          claimedClubName: 'Chelsea',
          status: 'Denied',
          raisedAt: '2026-09-03T00:00:00Z',
          reviewedAt: '2026-09-04T00:00:00Z',
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onReviewed } = renderReview({}, fetchMock);

    await screen.findByText(/Your opponent disputed step 2, claiming they played together at/);
    await user.click(screen.getByRole('button', { name: 'Deny' }));

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/matches/match-1/disputes/dispute-2/deny'),
      expect.objectContaining({ method: 'POST' }),
    );
    await waitFor(() => expect(onReviewed).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Deny' })).not.toBeInTheDocument());
  });

  it('REQ-1412/1413: an already-reviewed (Approved/Denied) dispute is filtered out of both sections regardless of raisedByMe', async () => {
    const approvedMine = { ...myPending, status: 'Approved', reviewedAt: '2026-09-04T00:00:00Z' };
    const deniedTheirs = { ...opponentPending, disputeId: 'dispute-3', status: 'Denied', reviewedAt: '2026-09-04T00:00:00Z' };
    const { container } = renderReview({}, vi.fn().mockImplementation(() => jsonResponse([approvedMine, deniedTheirs])));

    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });

  it('REQ-1413: a failed review call renders the error inline', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      if (url.endsWith('/matches/match-1/disputes') && method === 'GET') return jsonResponse([opponentPending]);
      if (url.endsWith('/matches/match-1/disputes/dispute-2/approve') && method === 'POST') {
        return jsonResponse({ title: 'Conflict', detail: 'Already reviewed.' }, 409);
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    renderReview({}, fetchMock);

    await screen.findByText(/Your opponent disputed step 2, claiming they played together at/);
    await user.click(screen.getByRole('button', { name: 'Approve' }));

    expect(await screen.findByText('Already reviewed.')).toBeInTheDocument();
  });
});

import { renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useNotificationSummary } from './useNotificationSummary';

// REQ-1411 (S-217, design-document.md SCREEN-07's 2026-09-03 status note):
// isolated coverage of the polling hook itself — HeaderNav.test.tsx covers
// the badge's own rendering from a plain count prop; App.tsx's wiring
// (mounting this hook, summing the three counts) is exercised indirectly
// through the real app, not re-tested here.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

const emptySummary = {
  pendingFriendRequestCount: 0,
  pendingChallengeCount: 0,
  matchesAwaitingActionCount: 0,
  hasPending: false,
};

describe('useNotificationSummary', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('REQ-1411: returns the all-zero empty summary before accessToken is set', () => {
    const { result } = renderHook(() => useNotificationSummary(null, vi.fn()));

    expect(result.current).toEqual(emptySummary);
  });

  it('REQ-1411: fetches GET /notifications/summary once accessToken is provided, and returns its counts', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse({
        pendingFriendRequestCount: 2,
        pendingChallengeCount: 1,
        matchesAwaitingActionCount: 3,
        hasPending: true,
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    // A stable reference — see the polling test's own comment further down
    // in this file for why an inline `vi.fn()` here would be a real bug,
    // not just style: renderHook's own callback re-runs on every state
    // update the hook makes, so a fresh function identity on every render
    // would re-trigger the effect (and re-fetch) far more often than a real
    // caller (App.tsx, which always passes a useCallback-stable
    // handleLogout) ever would.
    const onAuthError = vi.fn();
    const { result } = renderHook(() => useNotificationSummary('token', onAuthError));

    await waitFor(() => expect(result.current.pendingFriendRequestCount).toBe(2));
    expect(result.current).toEqual({
      pendingFriendRequestCount: 2,
      pendingChallengeCount: 1,
      matchesAwaitingActionCount: 3,
      hasPending: true,
    });
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/notifications/summary'),
      expect.anything(),
    );
  });

  it('REQ-1411: polls again after 15s while mounted, replacing the previous counts', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(() =>
        jsonResponse({ pendingFriendRequestCount: 1, pendingChallengeCount: 0, matchesAwaitingActionCount: 0, hasPending: true }),
      )
      .mockImplementation(() =>
        jsonResponse({ pendingFriendRequestCount: 4, pendingChallengeCount: 0, matchesAwaitingActionCount: 0, hasPending: true }),
      );
    vi.stubGlobal('fetch', fetchMock);
    vi.useFakeTimers({ shouldAdvanceTime: true });

    // A stable onAuthError reference, not a fresh vi.fn() created inline on
    // every render — renderHook's callback re-runs on every state update
    // this hook makes (setSummary), and an unstable dependency here would
    // re-trigger the effect (and re-fetch) on every one of those renders,
    // not just every real 15s tick, the same "unmemoized callback re-runs
    // an effect far more often than intended" pitfall App.tsx's own
    // handleLoggedOut comment already documents.
    const onAuthError = vi.fn();
    const { result } = renderHook(() => useNotificationSummary('token', onAuthError));

    await waitFor(() => expect(result.current.pendingFriendRequestCount).toBe(1));

    await vi.advanceTimersByTimeAsync(15_000);
    await waitFor(() => expect(result.current.pendingFriendRequestCount).toBe(4));
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("REQ-1411: a failed background poll doesn't replace already-known counts with the empty summary", async () => {
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(() =>
        jsonResponse({ pendingFriendRequestCount: 1, pendingChallengeCount: 0, matchesAwaitingActionCount: 0, hasPending: true }),
      )
      .mockImplementation(() => Promise.resolve({ ok: false, status: 500, json: () => Promise.resolve({}) } as Response));
    vi.stubGlobal('fetch', fetchMock);
    vi.useFakeTimers({ shouldAdvanceTime: true });

    // Stable reference — see the previous test's own comment for why.
    const onAuthError = vi.fn();
    const { result } = renderHook(() => useNotificationSummary('token', onAuthError));
    await waitFor(() => expect(result.current.pendingFriendRequestCount).toBe(1));

    await vi.advanceTimersByTimeAsync(15_000);
    // Still 1 — a transient poll failure never blanks the badge.
    expect(result.current.pendingFriendRequestCount).toBe(1);
  });

  it('REQ-1411: a 401 calls onAuthError', async () => {
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized' }, 401));
    vi.stubGlobal('fetch', fetchMock);
    const onAuthError = vi.fn();

    renderHook(() => useNotificationSummary('token', onAuthError));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-1411: resets to the empty summary when accessToken becomes null (e.g. logout)', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse({ pendingFriendRequestCount: 2, pendingChallengeCount: 0, matchesAwaitingActionCount: 0, hasPending: true }),
    );
    vi.stubGlobal('fetch', fetchMock);

    const onAuthError = vi.fn();
    const { result, rerender } = renderHook(
      ({ accessToken }: { accessToken: string | null }) => useNotificationSummary(accessToken, onAuthError),
      { initialProps: { accessToken: 'token' } },
    );
    await waitFor(() => expect(result.current.pendingFriendRequestCount).toBe(2));

    rerender({ accessToken: null });

    expect(result.current).toEqual(emptySummary);
  });
});

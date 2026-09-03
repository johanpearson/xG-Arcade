import { act, renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ApiError } from './apiClient';
import { useSubmitAction } from './useSubmitAction';

// Quality-gate follow-up to commit 1203d47 (ADR-0084 code-health budget,
// rule-of-three): direct coverage of the shared "submit action" state
// machine extracted out of five near-identical hand-rolled copies in
// src/social/ (FriendsTab.tsx x2, ChallengesTab.tsx, MatchmakingTab.tsx,
// SendFriendRequestAction.tsx). Each of those components' own existing
// tests still exercise this behavior end-to-end through real DOM
// interactions — this file adds isolated coverage of the hook itself so
// the shared machinery has its own direct tests too, the same way
// useAuthedFetch.ts/useNotificationSummary.test.ts do for the mirror-image
// fetch hook.

describe('useSubmitAction', () => {
  it('starts idle: not submitting, no error', () => {
    const { result } = renderHook(() => useSubmitAction({ onAuthError: vi.fn() }));

    expect(result.current.submitting).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('sets submitting true while the action is in flight, then false once it resolves', async () => {
    let resolveAction: (value: string) => void = () => {};
    const action = () => new Promise<string>((resolve) => (resolveAction = resolve));
    const { result } = renderHook(() => useSubmitAction<string>({ onAuthError: vi.fn() }));

    act(() => {
      void result.current.run(action);
    });
    await waitFor(() => expect(result.current.submitting).toBe(true));

    act(() => resolveAction('done'));
    await waitFor(() => expect(result.current.submitting).toBe(false));
  });

  it('calls onSuccess with the resolved value once the action succeeds', async () => {
    const onSuccess = vi.fn();
    const { result } = renderHook(() => useSubmitAction<string>({ onAuthError: vi.fn() }));

    await act(async () => {
      await result.current.run(() => Promise.resolve('resolved-value'), onSuccess);
    });

    expect(onSuccess).toHaveBeenCalledWith('resolved-value');
    expect(result.current.error).toBeNull();
  });

  it('clears any previous error before a new run starts', async () => {
    const { result } = renderHook(() => useSubmitAction<string>({ onAuthError: vi.fn() }));

    await act(async () => {
      await result.current.run(() => Promise.reject(new Error('first failure')));
    });
    await waitFor(() => expect(result.current.error).toBe('first failure'));

    let resolveAction: (value: string) => void = () => {};
    act(() => {
      void result.current.run(() => new Promise<string>((resolve) => (resolveAction = resolve)));
    });
    await waitFor(() => expect(result.current.error).toBeNull());

    act(() => resolveAction('done'));
  });

  it('captures a non-401 thrown error via describeError, and still clears submitting', async () => {
    const { result } = renderHook(() => useSubmitAction<string>({ onAuthError: vi.fn() }));

    await act(async () => {
      await result.current.run(() => Promise.reject(new Error('boom')));
    });

    expect(result.current.error).toBe('boom');
    expect(result.current.submitting).toBe(false);
  });

  it('escalates a 401 via onAuthError, never sets error, and still clears submitting', async () => {
    const onAuthError = vi.fn();
    const { result } = renderHook(() => useSubmitAction<string>({ onAuthError }));

    await act(async () => {
      await result.current.run(() => Promise.reject(new ApiError('Unauthorized', 'Unauthorized', 401)));
    });

    expect(onAuthError).toHaveBeenCalledTimes(1);
    expect(result.current.error).toBeNull();
    expect(result.current.submitting).toBe(false);
  });

  it('never calls onSuccess when the action throws', async () => {
    const onSuccess = vi.fn();
    const { result } = renderHook(() => useSubmitAction<string>({ onAuthError: vi.fn() }));

    await act(async () => {
      await result.current.run(() => Promise.reject(new Error('boom')), onSuccess);
    });

    expect(onSuccess).not.toHaveBeenCalled();
  });
});

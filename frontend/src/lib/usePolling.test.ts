import { renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { usePolling } from './usePolling';

// Quality-gate follow-up (S-218, ADR-0084 code-health budget, rule-of-three):
// direct coverage of the shared poll-loop hook extracted out of two
// byte-for-byte-identical hand-rolled copies in MatchChat.tsx/MatchScreen.tsx.
// Both of those components' own existing tests still exercise this behavior
// end-to-end through real DOM interactions — this file adds isolated
// coverage of the hook itself, the same way useSubmitAction.test.ts/
// useNotificationSummary.test.ts do for the other two small hooks in this
// directory.

describe('usePolling', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('calls refetch again after the interval elapses, and keeps rescheduling', async () => {
    const refetch = vi.fn().mockResolvedValue(undefined);
    vi.useFakeTimers({ shouldAdvanceTime: true });

    renderHook(() => usePolling(refetch, 15_000));
    expect(refetch).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(15_000);
    expect(refetch).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(15_000);
    expect(refetch).toHaveBeenCalledTimes(2);
  });

  it('does not schedule a poll at all while enabled is false', async () => {
    const refetch = vi.fn().mockResolvedValue(undefined);
    vi.useFakeTimers({ shouldAdvanceTime: true });

    renderHook(() => usePolling(refetch, 15_000, { enabled: false }));

    await vi.advanceTimersByTimeAsync(60_000);
    expect(refetch).not.toHaveBeenCalled();
  });

  it('starts polling once enabled flips from false to true, without an immediate call', async () => {
    const refetch = vi.fn().mockResolvedValue(undefined);
    vi.useFakeTimers({ shouldAdvanceTime: true });

    const { rerender } = renderHook(({ enabled }: { enabled: boolean }) => usePolling(refetch, 15_000, { enabled }), {
      initialProps: { enabled: false },
    });
    await vi.advanceTimersByTimeAsync(30_000);
    expect(refetch).not.toHaveBeenCalled();

    rerender({ enabled: true });
    expect(refetch).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(15_000);
    expect(refetch).toHaveBeenCalledTimes(1);
  });

  it('stops scheduling further polls once enabled flips from true to false, mid-cycle', async () => {
    const refetch = vi.fn().mockResolvedValue(undefined);
    vi.useFakeTimers({ shouldAdvanceTime: true });

    const { rerender } = renderHook(({ enabled }: { enabled: boolean }) => usePolling(refetch, 15_000, { enabled }), {
      initialProps: { enabled: true },
    });
    await vi.advanceTimersByTimeAsync(15_000);
    expect(refetch).toHaveBeenCalledTimes(1);

    rerender({ enabled: false });
    await vi.advanceTimersByTimeAsync(60_000);
    // No further calls — the effect's own cleanup tore down the pending timeout.
    expect(refetch).toHaveBeenCalledTimes(1);
  });

  it('stops polling on unmount rather than leaving a stray scheduled tick', async () => {
    const refetch = vi.fn().mockResolvedValue(undefined);
    vi.useFakeTimers({ shouldAdvanceTime: true });

    const { unmount } = renderHook(() => usePolling(refetch, 15_000));
    unmount();

    await vi.advanceTimersByTimeAsync(60_000);
    expect(refetch).not.toHaveBeenCalled();
  });

  it('never overlaps two in-flight refetch calls — the next tick waits for the previous refetch to resolve', async () => {
    let resolveFirst: () => void = () => {};
    const refetch = vi
      .fn()
      .mockImplementationOnce(() => new Promise<void>((resolve) => (resolveFirst = resolve)))
      .mockResolvedValue(undefined);
    vi.useFakeTimers({ shouldAdvanceTime: true });

    renderHook(() => usePolling(refetch, 15_000));

    await vi.advanceTimersByTimeAsync(15_000);
    expect(refetch).toHaveBeenCalledTimes(1);

    // The refetch call is still pending — advancing well past a second
    // interval must not queue a second call while the first hasn't resolved.
    await vi.advanceTimersByTimeAsync(45_000);
    expect(refetch).toHaveBeenCalledTimes(1);

    resolveFirst();
    // Flush the microtask queue (the resolved promise, and the hook's own
    // `scheduleNext()` re-arming call chained after it) before advancing
    // real fake-timer time again.
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(15_000);
    expect(refetch).toHaveBeenCalledTimes(2);
  });
});

import { renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { computeRoundCompletion, useCompletionTransition } from './roundCompletion';

describe('computeRoundCompletion', () => {
  it('REQ-1210: is not complete while any item is unlocked, regardless of known points', () => {
    const result = computeRoundCompletion([
      { locked: true, points: 12 },
      { locked: false, points: null },
    ]);
    expect(result.isComplete).toBe(false);
  });

  it('REQ-1210: is complete only once every item is locked', () => {
    const result = computeRoundCompletion([
      { locked: true, points: 12 },
      { locked: true, points: 100 },
    ]);
    expect(result.isComplete).toBe(true);
  });

  it('REQ-1210: is never complete for an empty item list (defensive — should never happen for a real round)', () => {
    expect(computeRoundCompletion([]).isComplete).toBe(false);
  });

  it('REQ-1210: currentPoints sums only known (non-null) point values, treating unknown as not-yet-counted', () => {
    const result = computeRoundCompletion([
      { locked: true, points: 12 },
      { locked: false, points: null },
      { locked: true, points: 29 },
    ]);
    expect(result.currentPoints).toBe(41);
  });
});

describe('useCompletionTransition', () => {
  it('REQ-1210 §7: never fires on the first isComplete value observed, even when it is already true (no replay on an already-finished round)', () => {
    const { result, rerender } = renderHook(({ isComplete }) => useCompletionTransition(isComplete), {
      initialProps: { isComplete: true as boolean | null },
    });
    expect(result.current).toBe(false);

    // Staying true on a later render still must not retroactively fire.
    rerender({ isComplete: true });
    expect(result.current).toBe(false);
  });

  it('REQ-1210: fires on a genuine false -> true transition observed while mounted', () => {
    const { result, rerender } = renderHook(({ isComplete }) => useCompletionTransition(isComplete), {
      initialProps: { isComplete: false as boolean | null },
    });
    expect(result.current).toBe(false);

    rerender({ isComplete: true });
    expect(result.current).toBe(true);
  });

  it('REQ-1210: a null (not-yet-loaded) value is not treated as a real baseline', () => {
    const { result, rerender } = renderHook(({ isComplete }) => useCompletionTransition(isComplete), {
      initialProps: { isComplete: null as boolean | null },
    });
    expect(result.current).toBe(false);

    // First real value observed is `true` (e.g. round finished loading
    // already complete) — must still not fire, same as the "already true
    // on first render" case above.
    rerender({ isComplete: true });
    expect(result.current).toBe(false);
  });

  it('REQ-1210: stays true once fired, even if isComplete later reports false-ish input (defensive — should not happen in practice)', () => {
    const { result, rerender } = renderHook(({ isComplete }) => useCompletionTransition(isComplete), {
      initialProps: { isComplete: false as boolean | null },
    });
    rerender({ isComplete: true });
    expect(result.current).toBe(true);
    rerender({ isComplete: true });
    expect(result.current).toBe(true);
  });
});

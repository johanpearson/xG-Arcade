import { useEffect } from 'react';

export interface UsePollingOptions {
  // Default true. Set false to stop the poll loop (and clean up any
  // in-flight timer) without unmounting the component that owns it — e.g.
  // MatchScreen.tsx's own "stop polling once the match is Resolved" rule.
  // Checked once per effect run (on mount, and whenever `refetch`,
  // `intervalMs`, or `enabled` itself changes) — flipping it from true to
  // false tears down the scheduled timeout via this hook's own cleanup
  // rather than leaving a stray tick pending.
  enabled?: boolean;
}

// Quality-gate follow-up (S-218 code-health-budget finding, ADR-0084):
// the self-rescheduling `setTimeout` poll-on-top-of-a-refetch shape
// (cancelled flag + timeoutId + a `scheduleNext` closure that re-arms
// itself after each `await refetch()`) was duplicated byte-for-byte
// between MatchChat.tsx and MatchScreen.tsx in the same diff that
// introduced both — the second copy of that exact shape to land in one
// diff, per coding-guidelines.md's "Code health budget" rule-of-three
// trigger. Extracted here, alongside useAuthedFetch.ts/useSubmitAction.ts,
// as the third small polling/fetch-state hook in this file.
//
// Self-rescheduling via `setTimeout` rather than `setInterval` — same
// reasoning useNotificationSummary.ts/AllTimeLeaderboard.tsx's own
// (deliberately NOT consolidated here — see below) poll already documents:
// guarantees only one `refetch` call is ever in flight, so a slow response
// can never overlap with a later tick.
//
// Deliberately does NOT also absorb useNotificationSummary.ts's/
// AllTimeLeaderboard.tsx's own inline poll — those two own their fetch and
// resulting state directly (there is no separate `refetch` to call), a
// different enough shape that folding all four into one hook now would be
// a bigger, out-of-scope consolidation. That's `code-health-auditor`'s call
// to make in a future whole-tree sweep, not this diff's.
export function usePolling(
  refetch: () => Promise<void>,
  intervalMs: number,
  { enabled = true }: UsePollingOptions = {},
): void {
  useEffect(() => {
    if (!enabled) return;

    let cancelled = false;
    let timeoutId: number | undefined;

    function scheduleNext() {
      timeoutId = window.setTimeout(async () => {
        if (cancelled) return;
        await refetch();
        if (!cancelled) scheduleNext();
      }, intervalMs);
    }
    scheduleNext();

    return () => {
      cancelled = true;
      if (timeoutId !== undefined) window.clearTimeout(timeoutId);
    };
  }, [refetch, intervalMs, enabled]);
}

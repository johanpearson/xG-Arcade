import { useCallback, useEffect, useState } from 'react';
import { ApiError, describeError } from './apiClient';

export interface UseAuthedFetchOptions {
  onAuthError: () => void;
}

export interface UseAuthedFetchResult<T> {
  data: T | null;
  hidden: boolean;
  loadError: string | null;
  refetch: () => Promise<void>;
}

// Shared fetch/cancel/401/403/thrown-error shape for any authed
// fetch-on-mount screen or section. Originally admin-screen-only —
// duplicated four times there (PlayerSuggestionsEntry/REQ-512,
// AnnouncementBannerSection/REQ-511, AccountMetricsSection/REQ-507,
// XGPathCycleSection/REQ-1209) and flagged as a rule-of-three-plus
// duplication candidate during REQ-512's quality gate; extracted once a
// fifth near-identical instance (IncidentReportsEntry/REQ-904) made it
// concrete, as `useAdminSectionFetch` in `src/admin/`. Promoted here under
// its current name (S-120) once the exact same shape turned up hand-rolled
// outside the admin area too (LeaguesScreen and others) — coding-guidelines.md
// flagged that as the trigger for a shared location rather than a second
// from-scratch duplication. A 401 escalates via onAuthError; a 403 sets
// `hidden` (the caller decides what to do with that — usually returning
// null); any other thrown error is captured as `loadError`;
// unmount-during-fetch is guarded internally so no caller needs its own
// local `cancelled` flag. `refetch` re-runs fetchFn on demand (e.g.
// AccountMetricsSection passes it down as GuestClearSection's onCleared)
// and resolves once the resulting state update has been applied, matching
// what each caller's own hand-rolled refresh function used to do.
//
// Deliberately does NOT own any state that arises from a *successful*
// response — XGPathCycleSection's `hasData` and IncidentReportsEntry's
// `available` are both business-level "is there real data yet" distinctions
// that live inside `data` and are branched on by the caller, never inside
// this hook. Folding those in would conflate "the fetch itself
// succeeded/failed" (this hook's whole job) with "what the successful
// response means" (the caller's job) — see IncidentReportsEntry's own
// comment for why that distinction matters (REQ-904's `available: false`
// must never read as a thrown error or as a hidden section).
export function useAuthedFetch<T>(
  fetchFn: () => Promise<T>,
  { onAuthError }: UseAuthedFetchOptions,
): UseAuthedFetchResult<T> {
  const [data, setData] = useState<T | null>(null);
  const [hidden, setHidden] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);

  const runFetch = useCallback(
    async (isCancelled: () => boolean) => {
      try {
        const result = await fetchFn();
        if (isCancelled()) return;
        setData(result);
        setLoadError(null);
      } catch (err) {
        if (isCancelled()) return;
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          setHidden(true);
          return;
        }
        setLoadError(describeError(err));
      }
    },
    [fetchFn, onAuthError],
  );

  useEffect(() => {
    let cancelled = false;
    runFetch(() => cancelled);
    return () => {
      cancelled = true;
    };
  }, [runFetch]);

  const refetch = useCallback(() => runFetch(() => false), [runFetch]);

  return { data, hidden, loadError, refetch };
}

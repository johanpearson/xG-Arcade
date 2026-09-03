import { useCallback, useState } from 'react';
import { ApiError, describeError } from './apiClient';

export interface UseSubmitActionOptions {
  onAuthError: () => void;
}

export interface UseSubmitActionResult<T> {
  submitting: boolean;
  error: string | null;
  // Runs `action`, tracking submitting/error state around it exactly the
  // way every hand-rolled "submit" handler in this codebase already did:
  // clear any previous error, flip `submitting` on, and on success hand the
  // result to `onSuccess` (most callers use this to update their own local
  // state — e.g. `setSent(true)`, `setExpiresAt(result.expiresAt)` — rather
  // than this hook owning that, since what "success" means is always
  // caller-specific). A thrown 401 escalates via `onAuthError` and is
  // deliberately never surfaced through `error` (mirrors useAuthedFetch.ts's
  // own 401 handling); any other thrown error is captured as `error` via
  // the same `describeError` every other submit path in this codebase uses,
  // so the caller renders the server's own detail text.
  run: (action: () => Promise<T>, onSuccess?: (result: T) => void | Promise<void>) => Promise<void>;
}

// The "submit action" shape (clear error, set submitting, await a call,
// react to success, escalate a 401, capture any other error, always clear
// submitting) was duplicated five times across `src/social/` (COMP-16's
// FriendsTab.tsx had it twice, ChallengesTab.tsx, MatchmakingTab.tsx, and
// SendFriendRequestAction.tsx each once) before being flagged as a
// rule-of-three-plus code-health-budget finding (ADR-0084) during S-217's
// quality gate. Extracted here, alongside useAuthedFetch.ts, since it's the
// mirror-image hook: that one owns the mount-fetch shape, this one owns the
// user-triggered-submit shape. Each call site keeps its own success/failure
// *rendering* (a row disappearing, a "sent" message appearing, a status
// banner) — this hook only owns the state machine around the request
// itself, the same "fetch succeeded/failed vs. what the response means"
// split useAuthedFetch.ts's own doc comment draws.
export function useSubmitAction<T>({ onAuthError }: UseSubmitActionOptions): UseSubmitActionResult<T> {
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = useCallback(
    async (action: () => Promise<T>, onSuccess?: (result: T) => void | Promise<void>) => {
      setError(null);
      setSubmitting(true);
      try {
        const result = await action();
        await onSuccess?.(result);
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
          return;
        }
        setError(describeError(err));
      } finally {
        setSubmitting(false);
      }
    },
    [onAuthError],
  );

  return { submitting, error, run };
}

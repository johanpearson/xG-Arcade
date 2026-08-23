// Shared fetch foundation every domain file in this directory (auth.ts,
// rounds.ts, path.ts, leaderboard.ts, admin.ts, leagues.ts, announcements.ts,
// incidents.ts) imports from — S-111's split of the original monolithic
// api.ts, mirroring the backend's own CompositionRoot precedent
// (docs/backlog.md S-111). Nothing here is domain-specific.

// Reuses the exact pattern established in App.tsx by S-002.
export const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? '';

// Carries the server's ProblemDetails title/detail through to the UI so
// error messages state what actually happened (docs/design-document.md §5)
// rather than a generic "something went wrong."
export class ApiError extends Error {
  readonly title: string;
  readonly detail?: string;
  readonly status?: number;

  constructor(title: string, detail: string | undefined, status: number | undefined) {
    super(detail ?? title);
    this.title = title;
    this.detail = detail;
    this.status = status;
  }
}

export async function throwApiError(response: Response): Promise<never> {
  let title = 'Request failed';
  let detail: string | undefined;
  try {
    const body = (await response.json()) as { title?: string; detail?: string };
    if (body.title) title = body.title;
    detail = body.detail;
  } catch {
    // Bare 404s (e.g. cell not found) have no JSON body at all — fall back
    // to the generic title rather than throwing on the parse itself.
  }
  throw new ApiError(title, detail, response.status);
}

export function describeError(error: unknown): string {
  if (error instanceof ApiError) return error.detail ?? error.title;
  if (error instanceof Error) return error.message;
  return 'Something went wrong. Check your connection and try again.';
}

// S-168: the shared request shape every domain file's 47 call sites used to
// hand-roll individually (build headers, fetch, ok-check + throwApiError,
// json() cast). `accessToken` is `string | null` rather than always-required
// because a handful of call sites are genuinely unauthenticated (auth.ts's
// signup/login/playAsGuest/refreshAccessToken, before a session exists) —
// the Authorization header is only attached when a token is actually passed,
// never sent as `Bearer null`/`Bearer undefined`. `Content-Type: application/
// json` is only attached when `init.body` is present, matching every
// existing call site's own body-carrying-vs-not distinction. A 204 response
// has no body to parse — resolved to `undefined` (cast to `T`) via an
// explicit status check *before* calling `response.json()`, rather than a
// catch around the parse itself, since several call sites (rejectSuggestion,
// deleteAccount, logout, deleteUserByEmail's success path — all confirmed
// 204 `Results.NoContent()` responses server-side) never had a body to parse
// in the first place and only care about the ok-check succeeding. Anything
// else that fails to parse on an otherwise-ok response (e.g. a genuinely
// malformed body on a call site expecting real typed data) is left to throw
// rather than silently resolving to `undefined` — quality-architect review,
// S-168: swallowing that indiscriminately would let a real parse failure on
// one of the ~40 typed call sites masquerade as valid data of type `T`,
// the same "failure indistinguishable from no-data" trap this doc's
// external-client swallow-to-empty guideline already warns about elsewhere.
//
// Deliberately still throws (via throwApiError) on any non-ok response,
// including 404 — callers that treat a specific status as a real, expected
// non-error outcome (e.g. fetchCurrentRound's/fetchActiveAdminRound's/
// deleteUserByEmail's own 404-as-null-or-sentinel idioms) catch the resulting
// `ApiError` and branch on `error.status` themselves rather than this helper
// growing a status-code-allowlist parameter — see each of those functions'
// own comment for why that status is special to them specifically, not to
// requests in general.
export async function apiRequest<T>(
  accessToken: string | null,
  path: string,
  init?: RequestInit,
): Promise<T> {
  const headers: Record<string, string> = {
    ...(init?.headers as Record<string, string> | undefined),
  };
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`;
  if (init?.body !== undefined) headers['Content-Type'] = 'application/json';

  // Only pass a second `fetch` argument at all when there's actually
  // something to put in it — fetchAnnouncementBanner's genuinely
  // header-less, tokenless GET calls `fetch(url)` with no options object,
  // same as before this helper existed (its own test asserts that exact
  // call shape, the concrete proxy for "sends no Authorization header of
  // any kind").
  const requestInit: RequestInit | undefined =
    init || Object.keys(headers).length > 0 ? { ...init, headers } : undefined;
  const url = `${API_BASE_URL}${path}`;
  const response = requestInit ? await fetch(url, requestInit) : await fetch(url);
  if (!response.ok) await throwApiError(response);
  if (response.status === 204) return undefined as T;

  return (await response.json()) as T;
}

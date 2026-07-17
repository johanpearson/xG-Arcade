import type {
  CurrentRoundResponse,
  LeaderboardResponse,
  LoginResponse,
  PlayerAutocompleteSuggestion,
  SignupResponse,
  SubmitGuessResponse,
} from './types';

// Reuses the exact pattern established in App.tsx by S-002.
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? '';

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

async function throwApiError(response: Response): Promise<never> {
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

export async function signup(
  email: string,
  password: string,
  confirmPassword: string,
  displayName: string,
  ageConfirmed: boolean,
): Promise<SignupResponse> {
  const response = await fetch(`${API_BASE_URL}/auth/signup`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password, confirmPassword, displayName, ageConfirmed }),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as SignupResponse;
}

export async function login(email: string, password: string): Promise<LoginResponse> {
  const response = await fetch(`${API_BASE_URL}/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as LoginResponse;
}

// Returns null for the "no active round" empty state (404) rather than
// throwing — that's a real, expected state (design-document.md §5: "empty
// states are invitations"), not an error.
export async function fetchCurrentRound(
  accessToken: string,
): Promise<CurrentRoundResponse | null> {
  const response = await fetch(`${API_BASE_URL}/rounds/current`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (response.status === 404) return null;
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as CurrentRoundResponse;
}

export async function submitGuess(
  accessToken: string,
  roundId: string,
  cellId: string,
  submittedName: string,
): Promise<SubmitGuessResponse> {
  const response = await fetch(
    `${API_BASE_URL}/rounds/${roundId}/cells/${cellId}/guesses`,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${accessToken}`,
      },
      body: JSON.stringify({ submittedName }),
    },
  );
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as SubmitGuessResponse;
}

// REQ-401/404/607: the global leaderboard (SCREEN-03) — the only league
// Tier 0 has (custom leagues are deferred, MVP-SCOPE.md). `cursor`/
// `pageSize` are optional and only appended as query params when provided —
// omitting both fetches the first page at the backend's default pageSize,
// which is what the initial load and the 15s poll both do; SCREEN-03's
// "Load more" passes the previous response's `nextCursor` explicitly.
export async function fetchLeaderboard(
  accessToken: string,
  cursor?: number,
  pageSize?: number,
): Promise<LeaderboardResponse> {
  const params = new URLSearchParams();
  if (cursor !== undefined) params.set('cursor', String(cursor));
  if (pageSize !== undefined) params.set('pageSize', String(pageSize));
  const query = params.toString();
  const response = await fetch(
    `${API_BASE_URL}/leagues/global/leaderboard${query ? `?${query}` : ''}`,
    { headers: { Authorization: `Bearer ${accessToken}` } },
  );
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as LeaderboardResponse;
}

// REQ-207/ADR-0007 (S-032): sourced from PlayerNameIndex only, never
// PlayerAttribute/PlayerOverride (see PlayerAutocompleteSuggestion's own
// comment in types.ts) — GuessInput treats a failed/empty result as "no
// suggestions," never as a reason to block guess submission.
export async function fetchPlayerAutocomplete(
  accessToken: string,
  query: string,
  limit?: number,
): Promise<PlayerAutocompleteSuggestion[]> {
  const params = new URLSearchParams();
  params.set('query', query);
  if (limit !== undefined) params.set('limit', String(limit));
  const response = await fetch(
    `${API_BASE_URL}/players/autocomplete?${params.toString()}`,
    { headers: { Authorization: `Bearer ${accessToken}` } },
  );
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as PlayerAutocompleteSuggestion[];
}

// REQ-710 (S-039): the server re-verifies `password` against Supabase Auth
// before deleting anything — a wrong password throws (401, title "Incorrect
// password") rather than resolving. Success is 204 No Content, nothing to parse.
export async function deleteAccount(accessToken: string, password: string): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/auth/account`, {
    method: 'DELETE',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ password }),
  });
  if (!response.ok) await throwApiError(response);
}

import type {
  CurrentRoundResponse,
  LeaderboardResponse,
  LoginResponse,
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
  displayName: string,
  ageConfirmed: boolean,
): Promise<SignupResponse> {
  const response = await fetch(`${API_BASE_URL}/auth/signup`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password, displayName, ageConfirmed }),
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

// REQ-401/404: the global leaderboard (SCREEN-03) — the only league Tier 0
// has (custom leagues are deferred, MVP-SCOPE.md).
export async function fetchLeaderboard(accessToken: string): Promise<LeaderboardResponse> {
  const response = await fetch(`${API_BASE_URL}/leagues/global/leaderboard`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as LeaderboardResponse;
}

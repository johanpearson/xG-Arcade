import type {
  AdminActiveRound,
  AdminRound,
  ApprovePlayerDataResponse,
  ClosedRoundListResponse,
  CurrentRoundResponse,
  CurrentUser,
  CustomLeague,
  LeaderboardResponse,
  LoginResponse,
  PlayerAutocompleteSuggestion,
  PlayerOverride,
  RemovePlayerDataResponse,
  SignupResponse,
  SubmitGuessResponse,
  UnverifiedPlayerData,
  UpdateDisplayNameResponse,
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

// REQ-717/ADR-0036: provisions a real, auto-enrolled guest User row with no
// email/password (POST /auth/guest — see AuthController.Guest). Same
// response shape as login()/signup's follow-up login above (LoginResponse),
// and the caller stores/treats it identically to any other login from this
// point on — no separate "guest mode" client-side state (ADR-0036's
// explicit design goal, mirrored here rather than reinterpreted).
//
// REQ-717's 2026-07-21 "Bot-check (captcha)" addition / ADR-0037: this
// endpoint now requires a `captchaToken` (a Cloudflare Turnstile token the
// caller obtains client-side via `lib/turnstile.ts`'s `getTurnstileToken()`
// *before* ever calling this function) — superseding the original
// no-request-body design. This function forwards the token unmodified; it
// performs no captcha verification of its own (ADR-0037's "mediate, don't
// reimplement" boundary — Supabase verifies it against Cloudflare
// server-side). A captcha-specific rejection comes back as a distinct 400
// with `title === 'Captcha verification failed'` (vs. the generic 500
// "Guest sign-in failed" for any other failure) — left to throw as an
// ApiError like any other failure here; the caller (AuthScreen.tsx)
// branches on `error.title` to decide whether to reset the Turnstile widget.
export async function playAsGuest(captchaToken: string): Promise<LoginResponse> {
  const response = await fetch(`${API_BASE_URL}/auth/guest`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ captchaToken }),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as LoginResponse;
}

// REQ-717/ADR-0036: the claim/upgrade path (POST /auth/claim,
// [Authorize]-protected — AuthController.Claim) — adds a real email and
// password to the caller's existing guest identity, converting it in place
// rather than creating a second account. A 400 (caller isn't currently a
// guest, or the email is already in use) is left to throw so the caller
// shows the server's own detail text inline, same convention as
// createLeague/joinLeague above. Returns the same MeResponse shape
// fetchMe already returns, reflecting the account's newly-set email.
export async function claimAccount(
  accessToken: string,
  email: string,
  password: string,
  confirmPassword: string,
): Promise<CurrentUser> {
  const response = await fetch(`${API_BASE_URL}/auth/claim`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ email, password, confirmPassword }),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as CurrentUser;
}

// REQ-715/ADR-0033: exchanges a stored refresh token for a new access token
// (and, if Supabase's own token rotation returns one, a new refresh token),
// mediated through the backend exactly like login/signup (ADR-0013) — never
// a direct frontend-to-Supabase call. Deliberately unauthenticated (no
// Authorization header): the whole reason to call this is that the caller
// may not have a currently-valid access token at all. An invalid, expired,
// or revoked refresh token throws (401, title "Refresh failed") — App.tsx's
// caller falls through to a full logout on that, never an infinite retry.
export async function refreshAccessToken(refreshToken: string): Promise<LoginResponse> {
  const response = await fetch(`${API_BASE_URL}/auth/refresh`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ refreshToken }),
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

// REQ-209: `chosenPlayerId` is only ever sent on a resubmission answering a
// disambiguation prompt (the player GUID they picked from
// SubmitGuessResponse.candidates) — omitted entirely (not sent as
// undefined/null) on every ordinary submission, matching the backend
// contract's "optional field, only present on a resubmission" shape.
export async function submitGuess(
  accessToken: string,
  roundId: string,
  cellId: string,
  submittedName: string,
  chosenPlayerId?: string,
): Promise<SubmitGuessResponse> {
  const body: { submittedName: string; chosenPlayerId?: string } = { submittedName };
  if (chosenPlayerId) body.chosenPlayerId = chosenPlayerId;

  const response = await fetch(
    `${API_BASE_URL}/rounds/${roundId}/cells/${cellId}/guesses`,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${accessToken}`,
      },
      body: JSON.stringify(body),
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

// REQ-407/ADR-0031 (S-053): the active round's own leaderboard (SCREEN-03's
// "Current Round" scope) — participant-only, recomputed live on every
// call, never cached (ADR-0031). Same cursor/pageSize shape as
// fetchLeaderboard above. Deliberately does NOT swallow the "no active
// round" 404 the way fetchCurrentRound does for its own 404 — the caller
// needs to tell that apart from any other failure, so it's left to throw as
// an ApiError (status 404, title "No active round") and the caller branches
// on `error.status` (mirroring LeaderboardScreen's existing
// `error instanceof ApiError && error.status === 401` check elsewhere).
export async function fetchActiveRoundLeaderboard(
  accessToken: string,
  cursor?: number,
  pageSize?: number,
): Promise<LeaderboardResponse> {
  const params = new URLSearchParams();
  if (cursor !== undefined) params.set('cursor', String(cursor));
  if (pageSize !== undefined) params.set('pageSize', String(pageSize));
  const query = params.toString();
  const response = await fetch(
    `${API_BASE_URL}/leagues/global/leaderboard/active-round${query ? `?${query}` : ''}`,
    { headers: { Authorization: `Bearer ${accessToken}` } },
  );
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as LeaderboardResponse;
}

// REQ-408 (S-054): the browsable round-selection list (SCREEN-03's "past
// rounds" scope) — closed rounds only, most recently closed first, same
// cursor/pageSize shape as fetchLeaderboard/fetchActiveRoundLeaderboard
// above (REQ-408's explicit "one pagination convention, not two" resolution).
export async function fetchClosedRounds(
  accessToken: string,
  cursor?: number,
  pageSize?: number,
): Promise<ClosedRoundListResponse> {
  const params = new URLSearchParams();
  if (cursor !== undefined) params.set('cursor', String(cursor));
  if (pageSize !== undefined) params.set('pageSize', String(pageSize));
  const query = params.toString();
  const response = await fetch(
    `${API_BASE_URL}/leagues/global/leaderboard/closed-rounds${query ? `?${query}` : ''}`,
    { headers: { Authorization: `Bearer ${accessToken}` } },
  );
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as ClosedRoundListResponse;
}

// REQ-408 (S-054): one specific closed round's final, locked leaderboard
// (never recomputed once closed, unlike fetchActiveRoundLeaderboard above).
// A 404 ("Round not found") and a 409 ("Round not closed yet") are two
// distinct, real states the caller must tell apart — both are left to throw
// as an ApiError so the caller can branch on `error.status`, same reasoning
// as fetchActiveRoundLeaderboard's 404 above.
export async function fetchClosedRoundLeaderboard(
  accessToken: string,
  roundId: string,
  cursor?: number,
  pageSize?: number,
): Promise<LeaderboardResponse> {
  const params = new URLSearchParams();
  if (cursor !== undefined) params.set('cursor', String(cursor));
  if (pageSize !== undefined) params.set('pageSize', String(pageSize));
  const query = params.toString();
  const response = await fetch(
    `${API_BASE_URL}/leagues/global/leaderboard/closed-rounds/${roundId}${query ? `?${query}` : ''}`,
    { headers: { Authorization: `Bearer ${accessToken}` } },
  );
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as LeaderboardResponse;
}

// REQ-405 (S-027): the four fixed, calendar-aligned window resolutions
// SCREEN-03's "Time Windows" scope offers — a closed set matching the
// backend's `{resolution}` route segment exactly (case-insensitive
// server-side, but the frontend always sends lowercase so there's never a
// reason to rely on that leniency). "Calendar-aligned," not "rolling":
// week/month/year are fixed calendar periods (LeaderboardService
// .GetCalendarWindow), never a rolling last-N-days window.
export type WindowResolution = 'round' | 'week' | 'month' | 'year';

// REQ-405 (S-027): one calendar-aligned time-window's leaderboard
// (SCREEN-03's "Time Windows" scope) — same cursor/pageSize/response shape as
// fetchLeaderboard/fetchActiveRoundLeaderboard/fetchClosedRoundLeaderboard
// above, summing only locked `FinalPoints` (never live/provisional points,
// unlike fetchActiveRoundLeaderboard). An empty ranked list is a real,
// expected state (nothing has happened in that window yet) — the response
// still resolves normally with `rows: []`, not a 404, so there's no
// empty-as-null handling needed here the way fetchCurrentRound has for its
// own different "empty" meaning.
export async function fetchWindowedLeaderboard(
  accessToken: string,
  resolution: WindowResolution,
  cursor?: number,
  pageSize?: number,
): Promise<LeaderboardResponse> {
  const params = new URLSearchParams();
  if (cursor !== undefined) params.set('cursor', String(cursor));
  if (pageSize !== undefined) params.set('pageSize', String(pageSize));
  const query = params.toString();
  const response = await fetch(
    `${API_BASE_URL}/leagues/global/leaderboard/window/${resolution}${query ? `?${query}` : ''}`,
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

// REQ-504: nothing calls this before S-026 — it's the only source of
// `isAdmin`, used solely to decide whether to show the admin nav entry
// point (App.tsx). A 401 here means the token itself is dead, same meaning
// as everywhere else in this app.
export async function fetchMe(accessToken: string): Promise<CurrentUser> {
  const response = await fetch(`${API_BASE_URL}/auth/me`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as CurrentUser;
}

// REQ-714: edits the caller's own DisplayName from Settings — same 1-30
// character bound and case-insensitive uniqueness mechanism as signup
// (REQ-701). A 409 here uses the identical ProblemDetails shape as signup's
// conflict (AuthController.DisplayNameConflictProblem()), so the caller's
// existing ApiError/describeError handling already surfaces it correctly
// with no special-casing needed.
export async function updateDisplayName(
  accessToken: string,
  displayName: string,
): Promise<UpdateDisplayNameResponse> {
  const response = await fetch(`${API_BASE_URL}/auth/display-name`, {
    method: 'PUT',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ displayName }),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as UpdateDisplayNameResponse;
}

// REQ-503 (SCREEN-04): always registered, regardless of environment — no
// 404-as-hidden handling needed here the way the round-control probe below
// has, since this section is never Production-gated.
export async function fetchUnverifiedPlayerData(
  accessToken: string,
): Promise<UnverifiedPlayerData[]> {
  const response = await fetch(`${API_BASE_URL}/admin/player-data/unverified`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as UnverifiedPlayerData[];
}

// REQ-503 (2026-07-20 extension): the bulk "approve" action — a single id
// is just the N=1 case, same endpoint. Always resolves (never throws) with
// a 200 and one result per requested id; a row that no longer exists or is
// no longer unverified fails independently of the rest of the batch
// (surfaced per-row via each result's `failureReason`), never as an
// all-or-nothing batch success/failure. No `reason` field — unlike
// createPlayerOverride below, approve doesn't require one.
export async function approvePlayerData(
  accessToken: string,
  playerDataIds: string[],
): Promise<ApprovePlayerDataResponse> {
  const response = await fetch(`${API_BASE_URL}/admin/player-data/approve`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ playerDataIds }),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as ApprovePlayerDataResponse;
}

// REQ-503 (2026-07-20 extension): the bulk "remove" action — sibling to
// approvePlayerData above in every respect except the endpoint it calls: a
// single id is just the N=1 case, same endpoint. Always resolves (never
// throws) with a 200 and one result per requested id; a row that no longer
// exists fails independently of the rest of the batch (surfaced per-row via
// each result's `failureReason`), never as an all-or-nothing batch
// success/failure. No `reason` field — same as approve, unlike
// createPlayerOverride below.
export async function removePlayerData(
  accessToken: string,
  playerDataIds: string[],
): Promise<RemovePlayerDataResponse> {
  const response = await fetch(`${API_BASE_URL}/admin/player-data/remove`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ playerDataIds }),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as RemovePlayerDataResponse;
}

// REQ-501: 409 (an override already exists for this playerId/field) is left
// to throw like any other error — the caller shows the server's own detail
// text inline rather than treating it specially, since there's no "edit an
// existing override" UI to route to instead.
export async function createPlayerOverride(
  accessToken: string,
  playerId: string,
  field: string,
  value: string,
  reason: string,
): Promise<PlayerOverride> {
  const response = await fetch(`${API_BASE_URL}/admin/player-overrides`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ playerId, field, value, reason }),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as PlayerOverride;
}

// REQ-505: a bare 404 here (no body, same shape as any other routing miss)
// means the round-control/user-deletion feature isn't registered in this
// environment at all (ASPNETCORE_ENVIRONMENT == Production) — mirrors
// fetchCurrentRound's existing 404-as-null idiom, but the meaning here is
// "hide the section," not "empty state to render."
export async function fetchActiveAdminRound(
  accessToken: string,
  gameKey: string,
): Promise<AdminActiveRound | null> {
  const response = await fetch(`${API_BASE_URL}/admin/rounds/${gameKey}/active`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (response.status === 404) return null;
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as AdminActiveRound;
}

// REQ-505: 404 here (no active round for this game right now) is a real
// error distinct from the probe's 404-as-hidden above — left to throw.
export async function closeAdminRound(accessToken: string, gameKey: string): Promise<AdminRound> {
  const response = await fetch(`${API_BASE_URL}/admin/rounds/${gameKey}/close`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as AdminRound;
}

// REQ-505: 400 problem-details ("Invalid end time") when the chosen time
// isn't after both the round's start time and now — left to throw so the
// caller can show `detail` inline.
export async function updateAdminRoundEndTime(
  accessToken: string,
  gameKey: string,
  endTimeIso: string,
): Promise<AdminRound> {
  const response = await fetch(`${API_BASE_URL}/admin/rounds/${gameKey}/end-time`, {
    method: 'PUT',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ endTime: endTimeIso }),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as AdminRound;
}

export type DeleteUserResult = 'deleted' | 'not-found';

// REQ-506: a 404 (no user with this email) is a real, expected outcome the
// caller shows inline ("No user found with that email.") rather than a
// thrown error — mirrors why fetchCurrentRound treats its own 404 as data,
// not a failure, though the meaning here is "not found," not "hidden."
export async function deleteUserByEmail(
  accessToken: string,
  email: string,
): Promise<DeleteUserResult> {
  const response = await fetch(
    `${API_BASE_URL}/admin/users?email=${encodeURIComponent(email)}`,
    {
      method: 'DELETE',
      headers: { Authorization: `Bearer ${accessToken}` },
    },
  );
  if (response.status === 404) return 'not-found';
  if (!response.ok) await throwApiError(response);
  return 'deleted';
}

// REQ-402: creates a custom league and automatically enrolls the caller as
// its first member (XGArcade.Api.Leagues.LeagueEndpoints — POST /leagues).
export async function createLeague(accessToken: string, name: string): Promise<CustomLeague> {
  const response = await fetch(`${API_BASE_URL}/leagues`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ name }),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as CustomLeague;
}

// REQ-403: joins the caller to the league identified by inviteCode
// (POST /leagues/join). An unrecognized code throws (404, title "Invalid
// invite code") — left to throw (not swallowed to null/empty) so the
// caller shows the server's own specific detail text inline, same
// "server's own detail text shown inline" convention SettingsScreen's
// display-name conflict already uses.
export async function joinLeague(accessToken: string, inviteCode: string): Promise<CustomLeague> {
  const response = await fetch(`${API_BASE_URL}/leagues/join`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ inviteCode }),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as CustomLeague;
}

// This story's "simple list" of the caller's own custom leagues
// (GET /leagues/mine) — no per-league leaderboard data, just enough to
// show which league(s) exist and their invite code for re-sharing.
export async function fetchMyLeagues(accessToken: string): Promise<CustomLeague[]> {
  const response = await fetch(`${API_BASE_URL}/leagues/mine`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as CustomLeague[];
}

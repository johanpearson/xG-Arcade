import type {
  AdminAccountMetrics,
  AdminActiveRound,
  AdminRound,
  AdminXGPathCycleState,
  ApprovePlayerDataResponse,
  ClearGuestAccountsResponse,
  ClosedRoundListResponse,
  CommitPlayerDataPayload,
  CommitPlayerDataResult,
  CurrentPathResponse,
  CurrentRoundResponse,
  CurrentUser,
  CustomLeague,
  GuestAccountCountResponse,
  LeaderboardResponse,
  LoginResponse,
  PendingSuggestion,
  PlayerAutocompleteSuggestion,
  PlayerOverride,
  RemovePlayerDataResponse,
  SignupResponse,
  SubmitGuessResponse,
  SubmitSuggestionResponse,
  UnverifiedPlayerData,
  UpdateDisplayNameResponse,
  WikidataPlayerLookupResult,
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

// REQ-701/REQ-717's 2026-07-25 "scope correction" addition / ADR-0037's
// amendment: Supabase's captcha-protection toggle is project-wide (see
// NOTES.md's 2026-07-25 entry), not scoped to `POST /auth/guest` alone, so
// signup now requires a `captchaToken` the exact same way playAsGuest below
// already does — a Cloudflare Turnstile token the caller obtains
// client-side via `lib/turnstile.ts`'s `getTurnstileToken()` *before* ever
// calling this function. This function forwards the token unmodified; it
// performs no captcha verification of its own (same "mediate, don't
// reimplement" boundary as playAsGuest — Supabase verifies it against
// Cloudflare server-side). A captcha-specific rejection comes back as a
// distinct 400 with `title === 'Captcha verification failed'` (vs. the
// generic account-enumeration-safe "Signup could not be completed" for any
// other rejection reason) — left to throw as an ApiError like any other
// failure here; the caller (AuthScreen.tsx) branches on `error.title` to
// decide whether to reset the Turnstile widget.
export async function signup(
  email: string,
  password: string,
  confirmPassword: string,
  displayName: string,
  ageConfirmed: boolean,
  captchaToken: string,
): Promise<SignupResponse> {
  const response = await fetch(`${API_BASE_URL}/auth/signup`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password, confirmPassword, displayName, ageConfirmed, captchaToken }),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as SignupResponse;
}

// REQ-701/REQ-717's 2026-07-25 "scope correction" addition / ADR-0037's
// amendment: same `captchaToken` requirement and reasoning as signup above
// — Supabase's captcha-protection toggle covers `token?grant_type=password`
// (the endpoint this mediates) just as much as anonymous sign-in, so a
// Turnstile token obtained client-side via `getTurnstileToken()` is now
// required here too. A captcha-specific rejection comes back as the same
// distinct 400 (`title === 'Captcha verification failed'`) as signup/guest,
// vs. the generic 401 "Login failed" for a wrong password or any other
// rejection reason — left to throw as an ApiError; the caller branches on
// `error.title` the same way it does for signup/playAsGuest.
export async function login(email: string, password: string, captchaToken: string): Promise<LoginResponse> {
  const response = await fetch(`${API_BASE_URL}/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password, captchaToken }),
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

// REQ-1201/1202/1203 (S-086): mirrors fetchCurrentRound's exact pattern —
// same 404-as-null idiom (no active xg-path round is a real, expected empty
// state, not an error) and the same bearer-auth header handling. Returns the
// whole round's puzzle list at once, each puzzle carrying only the clue
// turns unlocked so far for the requesting player — GET /path/current is
// also what PathScreen re-calls after every guess submission to pick up the
// newly-revealed turn, since POST .../guesses' own response carries no clue
// data (see PathScreen.tsx's own comment on why a re-fetch, not a local
// patch, is the mechanism here).
export async function fetchCurrentPath(
  accessToken: string,
): Promise<CurrentPathResponse | null> {
  const response = await fetch(`${API_BASE_URL}/path/current`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (response.status === 404) return null;
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as CurrentPathResponse;
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

// REQ-215 (S-089): submits a player-suggested correction for a specific
// cell/round — the entry point only ever appears (GuessInput.tsx) after
// that cell's triggering guess was scored incorrect or hit REQ-211's live
// lookup timeout. `playerName` is the name already known from that
// triggering guess (or the disambiguation candidate's own name, when the
// trigger followed a REQ-209 resolution) — never re-typed by the player in
// this form. Follows submitGuess's exact fetch/ApiError/auth-header
// convention above. A guest is rejected server-side with 403 ("Guest
// accounts cannot submit suggestions") regardless of what the client UI
// shows (REQ-215's server-enforced guest restriction) — left to throw as an
// ApiError like any other failure here, same as every other call in this
// file; GuessInput/SuggestionEntry never special-case that status since the
// UI already disables the entry point for a guest before this call could
// ever be made through it.
export async function submitSuggestion(
  accessToken: string,
  roundId: string,
  cellId: string,
  playerName: string,
  clubs: string[],
  nationality: string,
): Promise<SubmitSuggestionResponse> {
  const response = await fetch(
    `${API_BASE_URL}/rounds/${roundId}/cells/${cellId}/suggestions`,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${accessToken}`,
      },
      body: JSON.stringify({ playerName, clubs, nationality }),
    },
  );
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as SubmitSuggestionResponse;
}

// REQ-401/404/607/410 (S-087): the global leaderboard (SCREEN-03) — the only
// league Tier 0 has (custom leagues are deferred, MVP-SCOPE.md). `gameKey`
// is optional (the backend defaults to xg-grid when omitted, ADR-0043) but
// the leaderboard screen now always passes one explicitly once it has a
// selected game — see SCREEN-03's "Game switcher" addition. `cursor`/
// `pageSize` are optional and only appended as query params when provided —
// omitting both fetches the first page at the backend's default pageSize,
// which is what the initial load and the 15s poll both do; SCREEN-03's
// "Load more" passes the previous response's `nextCursor` explicitly.
export async function fetchLeaderboard(
  accessToken: string,
  gameKey?: string,
  cursor?: number,
  pageSize?: number,
): Promise<LeaderboardResponse> {
  const params = new URLSearchParams();
  if (gameKey !== undefined) params.set('gameKey', gameKey);
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
  gameKey?: string,
  cursor?: number,
  pageSize?: number,
): Promise<LeaderboardResponse> {
  const params = new URLSearchParams();
  if (gameKey !== undefined) params.set('gameKey', gameKey);
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
  gameKey?: string,
  cursor?: number,
  pageSize?: number,
): Promise<ClosedRoundListResponse> {
  const params = new URLSearchParams();
  if (gameKey !== undefined) params.set('gameKey', gameKey);
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
// as fetchActiveRoundLeaderboard's 404 above. Deliberately has no `gameKey`
// param (S-087): unlike the other four leaderboard reads, this one resolves
// by `roundId` alone — a round already belongs to exactly one game, so
// there's no ambiguity a gameKey filter could resolve.
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
// (SCREEN-03's "Time Windows" scope) — same optional `gameKey`/cursor/
// pageSize/response shape as fetchLeaderboard/fetchActiveRoundLeaderboard/
// fetchClosedRoundLeaderboard above (REQ-410/S-087), summing only locked
// `FinalPoints` (never live/provisional points,
// unlike fetchActiveRoundLeaderboard). An empty ranked list is a real,
// expected state (nothing has happened in that window yet) — the response
// still resolves normally with `rows: []`, not a 404, so there's no
// empty-as-null handling needed here the way fetchCurrentRound has for its
// own different "empty" meaning.
export async function fetchWindowedLeaderboard(
  accessToken: string,
  resolution: WindowResolution,
  gameKey?: string,
  cursor?: number,
  pageSize?: number,
): Promise<LeaderboardResponse> {
  const params = new URLSearchParams();
  if (gameKey !== undefined) params.set('gameKey', gameKey);
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
  signal?: AbortSignal,
): Promise<PlayerAutocompleteSuggestion[]> {
  const params = new URLSearchParams();
  params.set('query', query);
  if (limit !== undefined) params.set('limit', String(limit));
  const response = await fetch(
    `${API_BASE_URL}/players/autocomplete?${params.toString()}`,
    { headers: { Authorization: `Bearer ${accessToken}` }, signal },
  );
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as PlayerAutocompleteSuggestion[];
}

// REQ-710 (S-039): the server re-verifies `password` against Supabase Auth
// before deleting anything — a wrong password throws (401, title "Incorrect
// password") rather than resolving. Success is 204 No Content, nothing to parse.
//
// REQ-710's 2026-07-25 addition / ADR-0037's second amendment: this
// re-verification call is the same `SignInWithPasswordAsync` call `login`
// above uses, so it now requires a `captchaToken` too — a Cloudflare
// Turnstile token the caller obtains client-side via `lib/turnstile.ts`'s
// `getTurnstileToken()` *before* ever calling this function. This function
// forwards the token unmodified; it performs no captcha verification of its
// own (same "mediate, don't reimplement" boundary as login/signup/
// playAsGuest — Supabase verifies it against Cloudflare server-side). A
// captcha-specific rejection comes back as the same distinct 400 with
// `title === 'Captcha verification failed'` used by every other call site —
// checked server-side before the password check, so it can never collide
// with the 401 "Incorrect password" response above — left to throw as an
// ApiError like any other failure here; the caller (DeleteAccountScreen.tsx)
// branches on `error.title` to decide whether to reset the Turnstile widget.
export async function deleteAccount(
  accessToken: string,
  password: string,
  captchaToken: string,
): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/auth/account`, {
    method: 'DELETE',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ password, captchaToken }),
  });
  if (!response.ok) await throwApiError(response);
}

// REQ-718/ADR-0038: the first backend logout call this app has ever made —
// deletes the caller's account only if it's an unclaimed guest, a no-op for
// every other account (mirrors REQ-715's existing frontend-only logout
// behavior for those). App.tsx's handleLogout treats this as fire-and-forget
// best-effort: never awaited in a way that would delay or block the
// existing instant local logout (clearing localStorage), and any failure
// here is caught and logged rather than surfaced to the person logging
// out — rule 3's 7-day inactivity purge (ADR-0038) is the safety net if
// this call never completes.
export async function logout(accessToken: string): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/auth/logout`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${accessToken}` },
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

// REQ-507: always registered, in every environment (including Production) —
// no 404-as-hidden handling needed here the way fetchActiveAdminRound has for
// its own Non-Production-only probe, since this endpoint's whole point is
// live visibility into real account counts, not test-data management. A 403
// (non-admin token) is left to throw like any other admin endpoint; the
// caller decides how to degrade (AdminScreen's AccountMetricsSection hides
// itself rather than flipping the whole page to access-denied, since
// REQ-501/502/503's unverified-data fetch already owns that page-level
// decision).
export async function fetchAdminAccountMetrics(accessToken: string): Promise<AdminAccountMetrics> {
  const response = await fetch(`${API_BASE_URL}/admin/accounts/metrics`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as AdminAccountMetrics;
}

// REQ-508 step 1: the dry-run count shown before the bulk force-clear-guests
// action's confirm step — a live count, not an estimate, of every account
// currently matching IsGuest = true. Left to throw on any failure (401/403/
// other), same as every other admin call in this file.
export async function fetchGuestAccountCount(accessToken: string): Promise<number> {
  const response = await fetch(`${API_BASE_URL}/admin/accounts/guests/count`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) await throwApiError(response);
  const body = (await response.json()) as GuestAccountCountResponse;
  return body.count;
}

// REQ-508 step 2: deletes every account currently matching IsGuest = true,
// each via IAccountDeletionService.DeleteAccountAsync (ADR-0038 — no second/
// raw bulk-delete path). No request body — the dry-run count from
// fetchGuestAccountCount above is a separate call, not a parameter re-sent
// here (the endpoint re-selects matching ids fresh at execution time). Always
// resolves (never throws for a partial failure) with one result per matching
// account, same per-row-outcome discipline as approvePlayerData/
// removePlayerData above.
export async function clearGuestAccounts(accessToken: string): Promise<ClearGuestAccountsResponse> {
  const response = await fetch(`${API_BASE_URL}/admin/accounts/guests/clear`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as ClearGuestAccountsResponse;
}

// REQ-1209/ADR-0058: always registered, in every environment (including
// Production) — mirrors fetchAdminAccountMetrics's own reasoning, this
// reads real, always-relevant operational state (REQ-1208's persisted
// xG Path cycle), not seeded/test data, so there's no 404-as-hidden probe
// the way fetchActiveAdminRound has for the Non-Production-only
// round-control feature. `hasData: false` is a normal 200 body (REQ-1209's
// "no xG Path round has ever generated yet" case) — never a thrown error.
// A 403 (non-admin token) is left to throw like every other admin call in
// this file; the caller (AdminScreen's XGPathCycleSection) decides how to
// degrade, mirroring AccountMetricsSection's own hide-not-page-wide-deny
// choice.
export async function fetchAdminXGPathCycle(accessToken: string): Promise<AdminXGPathCycleState> {
  const response = await fetch(`${API_BASE_URL}/admin/xg-path/cycle`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as AdminXGPathCycleState;
}

// REQ-509 (S-090)/ADR-0053: the pending-suggestion queue for
// SuggestionsScreen — its own endpoint, deliberately never merged with
// fetchUnverifiedPlayerData above (see that ADR's "never a shared row shape"
// rule). Always registered, same as every other admin call in this file; a
// 403 (non-admin token) is left to throw like every other admin endpoint.
export async function fetchPendingSuggestions(accessToken: string): Promise<PendingSuggestion[]> {
  const response = await fetch(`${API_BASE_URL}/admin/suggestions`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as PendingSuggestion[];
}

// REQ-509: triggers a fresh, admin-initiated Wikidata lookup for one pending
// suggestion's own player name (the suggestion, not any caller-supplied
// name, is the source of truth for which name gets looked up — this call
// takes no name parameter). A 409 (another admin already resolved this
// suggestion) and a 503 (ADR-0046's "lookup unavailable, try again" —
// distinct from a normal `found: false` no-match result) are both left to
// throw as an ApiError so the caller (SuggestionsScreen's review panel) can
// branch on `error.status` and render each as its own distinct state, never
// conflated with one another or with a generic failure.
export async function lookupSuggestionPlayer(
  accessToken: string,
  suggestionId: string,
): Promise<WikidataPlayerLookupResult> {
  const response = await fetch(`${API_BASE_URL}/admin/suggestions/${suggestionId}/lookup`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as WikidataPlayerLookupResult;
}

// REQ-509: commits the admin's reviewed/confirmed values for one pending
// suggestion — writes only through PlayerAttribute/PlayerOverride
// (ADR-0007/ADR-0053: never PlayerNameIndex) and moves the suggestion's own
// state to Committed server-side. A 400 (missing wikidataQid/fullName/reason,
// or neither nationality nor clubs provided) and a 409 (already resolved) are
// both left to throw so the caller shows the server's own detail text
// inline, same convention as createPlayerOverride above.
export async function commitSuggestion(
  accessToken: string,
  suggestionId: string,
  payload: CommitPlayerDataPayload,
): Promise<CommitPlayerDataResult> {
  const response = await fetch(`${API_BASE_URL}/admin/suggestions/${suggestionId}/commit`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify(payload),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as CommitPlayerDataResult;
}

// REQ-509: rejects one pending suggestion — no request body (mirrors
// approvePlayerData/removePlayerData's own "no reason field" precedent for
// a non-Correct admin action), no PlayerAttribute/PlayerOverride/
// PlayerNameIndex write of any kind. Success is 204 No Content. A 409
// (already resolved by another admin) is left to throw, same as every other
// suggestion-review call above.
export async function rejectSuggestion(accessToken: string, suggestionId: string): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/admin/suggestions/${suggestionId}/reject`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) await throwApiError(response);
}

// REQ-510/ADR-0053: the standalone variant of lookupSuggestionPlayer above —
// identical live-fetch mechanism, but keyed on a name the admin types
// directly rather than an existing suggestion's own PlayerName, and touches
// no PlayerSuggestion row at all. A 400 (blank playerName) is left to throw;
// the caller (ManualSearchSection) also disables the search action
// client-side on an empty/whitespace-only name, so this is defense in depth,
// not the primary guard.
export async function lookupPlayerByName(
  accessToken: string,
  playerName: string,
): Promise<WikidataPlayerLookupResult> {
  const response = await fetch(`${API_BASE_URL}/admin/player-search/lookup`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify({ playerName }),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as WikidataPlayerLookupResult;
}

// REQ-510/ADR-0053: the standalone variant of commitSuggestion above —
// identical write path (PlayerAttribute/PlayerOverride only, same 400/omit
// validation), but never reads, creates, or touches a PlayerSuggestion row.
export async function commitPlayerSearch(
  accessToken: string,
  payload: CommitPlayerDataPayload,
): Promise<CommitPlayerDataResult> {
  const response = await fetch(`${API_BASE_URL}/admin/player-search/commit`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify(payload),
  });
  if (!response.ok) await throwApiError(response);
  return (await response.json()) as CommitPlayerDataResult;
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

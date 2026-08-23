import type { CurrentUser, LoginResponse, SignupResponse, UpdateDisplayNameResponse } from './types';
import { apiRequest } from './apiClient';

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
  return apiRequest<SignupResponse>(null, '/auth/signup', {
    method: 'POST',
    body: JSON.stringify({ email, password, confirmPassword, displayName, ageConfirmed, captchaToken }),
  });
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
  return apiRequest<LoginResponse>(null, '/auth/login', {
    method: 'POST',
    body: JSON.stringify({ email, password, captchaToken }),
  });
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
  return apiRequest<LoginResponse>(null, '/auth/guest', {
    method: 'POST',
    body: JSON.stringify({ captchaToken }),
  });
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
  return apiRequest<CurrentUser>(accessToken, '/auth/claim', {
    method: 'POST',
    body: JSON.stringify({ email, password, confirmPassword }),
  });
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
  return apiRequest<LoginResponse>(null, '/auth/refresh', {
    method: 'POST',
    body: JSON.stringify({ refreshToken }),
  });
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
  await apiRequest<void>(accessToken, '/auth/account', {
    method: 'DELETE',
    body: JSON.stringify({ password, captchaToken }),
  });
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
  await apiRequest<void>(accessToken, '/auth/logout', { method: 'POST' });
}

// REQ-504: nothing calls this before S-026 — it's the only source of
// `isAdmin`, used solely to decide whether to show the admin nav entry
// point (App.tsx). A 401 here means the token itself is dead, same meaning
// as everywhere else in this app.
export async function fetchMe(accessToken: string): Promise<CurrentUser> {
  return apiRequest<CurrentUser>(accessToken, '/auth/me');
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
  return apiRequest<UpdateDisplayNameResponse>(accessToken, '/auth/display-name', {
    method: 'PUT',
    body: JSON.stringify({ displayName }),
  });
}

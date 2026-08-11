import type { ClosedRoundListResponse, LeaderboardResponse } from './types';
import { API_BASE_URL, throwApiError } from './apiClient';

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

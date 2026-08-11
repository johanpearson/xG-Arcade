import type { CustomLeague } from './types';
import { API_BASE_URL, throwApiError } from './apiClient';

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

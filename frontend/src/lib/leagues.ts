import type { CustomLeague } from './types';
import { apiRequest } from './apiClient';

// REQ-402: creates a custom league and automatically enrolls the caller as
// its first member (XGArcade.Api.Leagues.LeagueEndpoints — POST /leagues).
export async function createLeague(accessToken: string, name: string): Promise<CustomLeague> {
  return apiRequest<CustomLeague>(accessToken, '/leagues', {
    method: 'POST',
    body: JSON.stringify({ name }),
  });
}

// REQ-403: joins the caller to the league identified by inviteCode
// (POST /leagues/join). An unrecognized code throws (404, title "Invalid
// invite code") — left to throw (not swallowed to null/empty) so the
// caller shows the server's own specific detail text inline, same
// "server's own detail text shown inline" convention SettingsScreen's
// display-name conflict already uses.
export async function joinLeague(accessToken: string, inviteCode: string): Promise<CustomLeague> {
  return apiRequest<CustomLeague>(accessToken, '/leagues/join', {
    method: 'POST',
    body: JSON.stringify({ inviteCode }),
  });
}

// This story's "simple list" of the caller's own custom leagues
// (GET /leagues/mine) — no per-league leaderboard data, just enough to
// show which league(s) exist and their invite code for re-sharing.
export async function fetchMyLeagues(accessToken: string): Promise<CustomLeague[]> {
  return apiRequest<CustomLeague[]>(accessToken, '/leagues/mine');
}

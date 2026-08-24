import type { UserStatsResponse } from './types';
import { apiRequest } from './apiClient';

// REQ-411 (S-179): SCREEN-13's stats/profile view — GET /users/{userId}/stats.
// `gameKey` is required here (rather than optional like leaderboard.ts's own
// fetch functions) because the backend's default-to-xg-grid behavior exists
// only so callers that genuinely have no game context yet aren't forced to
// pick one — UserStatsScreen always has a selected game-tab by the time it
// fetches, so it always passes one explicitly, same convention this file's
// leaderboard.ts sibling follows for every one of its own scoped reads.
export async function fetchUserStats(
  accessToken: string,
  userId: string,
  gameKey: string,
): Promise<UserStatsResponse> {
  const params = new URLSearchParams();
  params.set('gameKey', gameKey);
  return apiRequest<UserStatsResponse>(accessToken, `/users/${userId}/stats?${params.toString()}`);
}

import type { CurrentHigherLowerResponse, HigherLowerDirection, SubmitHigherLowerGuessResponse } from './types';
import { ApiError, apiRequest } from './apiClient';

// REQ-1504/1505 (S-227/S-228): mirrors fetchCurrentPredict's/
// fetchCurrentPath's exact pattern — same 404-as-null idiom (no active
// xg-higher-lower round is a real, expected empty state, not an error) and
// the same bearer-auth header handling. Returns the active round's own
// StatCategory/ComparatorCount plus this specific player's current attempt
// state (a missing attempt is represented server-side as "streak 0,
// baseline = the round's own fixed starting baseline, not ended" — see
// HigherLowerEndpoints.cs's own comment — so this function never needs a
// separate "start attempt" call).
export async function fetchCurrentHigherLower(
  accessToken: string,
): Promise<CurrentHigherLowerResponse | null> {
  try {
    return await apiRequest<CurrentHigherLowerResponse>(accessToken, '/higher-lower/current');
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  }
}

// REQ-1504: submits a Higher/Lower guess against the caller's own current
// attempt (there is no separate attempt/cell identifier to pass — progression
// is strictly sequential and entirely server-determined, mirroring
// `HigherLowerSubmission`'s own "deliberately no CellId" doc comment). Left
// to throw (ApiError) on every failure — a 409 ("Attempt has ended," this
// player's attempt was already finished by an earlier guess) or a 404 (no
// active round) are both real, expected outcomes the caller
// (HigherLowerScreen.tsx) must branch on via error.status, not something
// this function itself interprets or swallows. `direction` is sent as the
// raw numeric value (see HigherLowerDirection's own doc comment in types.ts
// for why this enum has no JsonStringEnumConverter server-side).
export async function submitHigherLowerGuess(
  accessToken: string,
  direction: HigherLowerDirection,
): Promise<SubmitHigherLowerGuessResponse> {
  return apiRequest<SubmitHigherLowerGuessResponse>(accessToken, '/higher-lower/guesses', {
    method: 'POST',
    body: JSON.stringify({ direction }),
  });
}

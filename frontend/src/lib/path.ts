import type { CurrentPathResponse } from './types';
import { API_BASE_URL, throwApiError } from './apiClient';

// REQ-1201/1202/1203 (S-086): mirrors fetchCurrentRound's exact pattern —
// same 404-as-null idiom (no active xg-path round is a real, expected empty
// state, not an error) and the same bearer-auth header handling. Returns the
// whole round's puzzle list at once, each puzzle carrying only the clue
// turns unlocked so far for the requesting player — GET /path/current is
// also what PathScreen re-calls after every guess submission to pick up the
// newly-revealed turn, since POST .../guesses' own response carries no clue
// data (see PathScreen.tsx's own comment on why a re-fetch, not a local
// patch, is the mechanism here). Guess submission and autocomplete
// themselves are generic round/cell endpoints shared with xG Grid — see
// submitGuess/fetchPlayerAutocomplete in rounds.ts.
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

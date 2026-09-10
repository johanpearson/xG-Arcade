import type { CurrentHigherLowerResponse, HigherLowerDirection, SubmitHigherLowerGuessResponse } from './types';
import { ApiError, apiRequest } from './apiClient';

// Gap-fill (2026-09-10, user-tester report): `StatCategory` on the wire is
// the raw PlayerAttribute.AttributeType string ADR-0111 derives counts
// from ("club" | "trophy", see HigherLowerGenerationService's own
// CandidateStatCategories) — never human copy. HigherLowerScreen.tsx used
// to render it and its bare numeric value verbatim ("Comparing club" / a
// lone "1"), which a real player correctly read as meaningless. This map
// is the only place that knows the two current category strings; an
// unrecognized one (a future category added to CandidateStatCategories
// without a matching entry here) still renders — the raw string / bare
// number — rather than blocking the screen, same "never a hard block on
// an unknown value" posture as countryFlags.tsx's unknown-country case.
const STAT_CATEGORY_UNITS: Record<string, { comparing: string; singular: string; plural: string }> = {
  club: { comparing: 'number of clubs played for', singular: 'club', plural: 'clubs' },
  trophy: { comparing: 'number of trophies won', singular: 'trophy', plural: 'trophies' },
};

export function higherLowerCategoryLabel(statCategory: string): string {
  return STAT_CATEGORY_UNITS[statCategory]?.comparing ?? statCategory;
}

// Matches RoundCompletionBanner's own "N pts" convention (a single
// unit-suffixed string, not a bare number) rather than inventing a new
// value/unit layout.
export function higherLowerValueLabel(statCategory: string, value: number): string {
  const unit = STAT_CATEGORY_UNITS[statCategory];
  if (!unit) return String(value);
  return `${value} ${value === 1 ? unit.singular : unit.plural}`;
}

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

import type { ConfirmPredictionsResponse, CurrentPredictResponse, SubmitPredictionResponse } from './types';
import { ApiError, apiRequest } from './apiClient';

// REQ-1301/1303/1306: mirrors fetchCurrentPath's/fetchCurrentRound's exact
// pattern — same 404-as-null idiom (no active xg-predict round is a real,
// expected empty state, not an error) and the same bearer-auth header
// handling. Returns the whole round's 5-match slate at once, each carrying
// only this player's own stored prediction (null until submitted) — see
// CurrentPredictResponse's own doc comment in types.ts.
export async function fetchCurrentPredict(
  accessToken: string,
): Promise<CurrentPredictResponse | null> {
  try {
    return await apiRequest<CurrentPredictResponse>(accessToken, '/predict/current');
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null;
    throw error;
  }
}

// REQ-1302: submits or resubmits a prediction for one match. Left to throw
// (ApiError) on every failure — a 400 ("Invalid prediction"), a 409 ("Round
// is locked" or "Predictions already confirmed and locked"), or a 404
// (unknown match/no active round) are all real, expected outcomes the caller
// (PredictMatchInput.tsx) must branch on via error.status/error.detail, not
// something this function itself interprets or swallows.
export async function submitPrediction(
  accessToken: string,
  matchId: string,
  homeGoals: number,
  awayGoals: number,
): Promise<SubmitPredictionResponse> {
  return apiRequest<SubmitPredictionResponse>(
    accessToken,
    `/predict/matches/${matchId}/predictions`,
    { method: 'POST', body: JSON.stringify({ homeGoals, awayGoals }) },
  );
}

// REQ-1306: the explicit "confirm and lock" action — only ever called after
// PredictConfirmDialog.tsx's own second affirmation, never directly from the
// "Confirm and lock my predictions" button click. No body. Left to throw on
// every failure (409 "Not all predictions submitted"/"Round is locked"/
// "Predictions already confirmed and locked", or 404 no active round) — same
// reasoning as submitPrediction above.
export async function confirmPredictions(accessToken: string): Promise<ConfirmPredictionsResponse> {
  return apiRequest<ConfirmPredictionsResponse>(accessToken, '/predict/confirm', { method: 'POST' });
}

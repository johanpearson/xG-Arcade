import type {
  CurrentRoundResponse,
  PlayerAutocompleteSuggestion,
  SubmitGuessResponse,
  SubmitSuggestionResponse,
} from './types';
import { API_BASE_URL, throwApiError } from './apiClient';

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

// REQ-209: `chosenPlayerId` is only ever sent on a resubmission answering a
// disambiguation prompt (the player GUID they picked from
// SubmitGuessResponse.candidates) — omitted entirely (not sent as
// undefined/null) on every ordinary submission, matching the backend
// contract's "optional field, only present on a resubmission" shape.
//
// Shared by both xG Grid (GridScreen.tsx) and xG Path (PathScreen.tsx) —
// the underlying `POST /rounds/{roundId}/cells/{cellId}/guesses` endpoint
// is generic to any round/cell pair, not xG Grid-specific, so it lives here
// rather than in path.ts.
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

// REQ-207/ADR-0007 (S-032): sourced from PlayerNameIndex only, never
// PlayerAttribute/PlayerOverride (see PlayerAutocompleteSuggestion's own
// comment in types.ts) — GuessInput treats a failed/empty result as "no
// suggestions," never as a reason to block guess submission.
//
// Shared by both xG Grid (GuessInput.tsx) and xG Path (PathGuessInput.tsx)
// — same reasoning as submitGuess above.
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

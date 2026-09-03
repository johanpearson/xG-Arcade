import type {
  ConnectChatMessage,
  ConnectMatchDetail,
  ConnectMatchListItem,
  ConnectSubmitChainStepResponse,
  ConnectTargetPickSubmitResponse,
} from './types';
import { apiRequest } from './apiClient';

// S-218 (design-document.md SCREEN-16): typed fetch wrappers for every xG
// Connect match/gameplay endpoint (backend/src/XGArcade.Api/Connect/*.cs),
// same one-domain-file-per-area convention as challenges.ts/friends.ts/
// matchmaking.ts. Errors are left to throw as ApiError throughout — every
// caller shows the server's own detail text inline, same convention every
// other domain file in this directory already uses.

// REQ-1411/S-218-prep: every match (open or resolved) the caller
// participates in, in the caller's own perspective (GET /matches). This is
// the only way a client discovers which matchIds belong to it — see
// design-document.md SCREEN-16's "Matches tab" note.
export async function fetchConnectMatches(accessToken: string): Promise<ConnectMatchListItem[]> {
  return apiRequest<ConnectMatchListItem[]>(accessToken, '/matches');
}

// REQ-1404/1405/1406/1409/S-218-prep: full single-match detail for the
// gameplay screen (GET /matches/{matchId}).
export async function fetchConnectMatchDetail(accessToken: string, matchId: string): Promise<ConnectMatchDetail> {
  return apiRequest<ConnectMatchDetail>(accessToken, `/matches/${matchId}`);
}

// REQ-1404: either player selects (or, before the match officially starts,
// replaces) their own target pick (POST /matches/{matchId}/target-pick).
// Errors (404 not-found, 403 not-a-participant, 409 already-locked, 409
// trivially-connected, 503 live-lookup-unavailable) left to throw — see
// design-document.md SCREEN-16's "Target-pick phase" note for how each is
// shown.
export async function submitConnectTargetPick(
  accessToken: string,
  matchId: string,
  targetPlayerId: string,
): Promise<ConnectTargetPickSubmitResponse> {
  return apiRequest<ConnectTargetPickSubmitResponse>(accessToken, `/matches/${matchId}/target-pick`, {
    method: 'POST',
    body: JSON.stringify({ targetPlayerId }),
  });
}

// REQ-1406/1407: submits one incremental chain-connector claim (POST
// /matches/{matchId}/chain-steps). Always resolves to a normal 200 body —
// a wrong or unresolvable guess is never an ApiError, mirroring
// GuessEndpoints'/rounds.ts's own "wrong guess is not an error" precedent.
// Only genuine precondition failures (404/403/409 not-active/409
// chain-complete/409 already-forfeited) or a technical 503 live-lookup
// failure are left to throw.
export async function submitConnectChainStep(
  accessToken: string,
  matchId: string,
  candidatePlayerName: string,
  claimedClubName: string,
): Promise<ConnectSubmitChainStepResponse> {
  return apiRequest<ConnectSubmitChainStepResponse>(accessToken, `/matches/${matchId}/chain-steps`, {
    method: 'POST',
    body: JSON.stringify({ candidatePlayerName, claimedClubName }),
  });
}

// REQ-1410: sends a chat message (POST /matches/{matchId}/chat-messages).
// The server trims and enforces the 1000-char ceiling itself (400 on
// empty/whitespace-only or over-length) — this wrapper does not pre-trim,
// but MatchChat.tsx gives a client-side length hint per that same ceiling.
export async function sendConnectChatMessage(
  accessToken: string,
  matchId: string,
  messageText: string,
): Promise<ConnectChatMessage> {
  return apiRequest<ConnectChatMessage>(accessToken, `/matches/${matchId}/chat-messages`, {
    method: 'POST',
    body: JSON.stringify({ messageText }),
  });
}

// REQ-1410: every chat message for this match, ordered by sentAt (GET
// /matches/{matchId}/chat-messages) — no pagination, per that REQ's own
// acceptance criteria. Polled by MatchChat.tsx, mirroring
// useNotificationSummary.ts's own 15s self-rescheduling poll shape (REQ-1410
// "does not require a live push update").
export async function fetchConnectChatMessages(accessToken: string, matchId: string): Promise<ConnectChatMessage[]> {
  return apiRequest<ConnectChatMessage[]>(accessToken, `/matches/${matchId}/chat-messages`);
}

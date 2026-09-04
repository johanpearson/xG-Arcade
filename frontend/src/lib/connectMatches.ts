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

// Design change (2026-09-04, REQ-1406, ADR-0104): the club a chain step
// connects through is now a server-computed fact, not a player-typed claim
// — this formats it alongside the overlapping years, e.g.
// "Chelsea, 2012-2019" or "Chelsea, 2012-present" when the overlap is
// still ongoing (a null matchedOverlapEndYear — see ConnectChainStep's own
// doc comment, backend/src/XGArcade.Data/Entities/ConnectChainStep.cs).
// Lives here (a lib file), not in ChainStepsList.tsx (a component file),
// purely so both ChainStepsList.tsx and ChainBuilder.tsx's own post-submit
// feedback can share identical wording without one importing a named
// export from the other's component file.
export function formatMatchedClub(
  matchedClubName: string | null, matchedOverlapStartYear: number | null, matchedOverlapEndYear: number | null,
): string {
  if (matchedClubName === null || matchedOverlapStartYear === null) return '';
  const endLabel = matchedOverlapEndYear === null ? 'present' : String(matchedOverlapEndYear);
  return `${matchedClubName}, ${matchedOverlapStartYear}-${endLabel}`;
}

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
// Takes a player NAME, resolved server-side against Player (COMP-06) —
// never a client-supplied id, since the only search UI available
// (/players/autocomplete, COMP-10) returns PlayerNameIndex.PlayerId, a
// different, unreconciled id space (ADR-0007). Mirrors
// submitConnectChainStep's own candidatePlayerName precedent below.
// Errors (404 not-found "Target player not found", 403 not-a-participant,
// 409 already-locked, 409 trivially-connected, 503 live-lookup-unavailable)
// left to throw — see design-document.md SCREEN-16's "Target-pick phase"
// note for how each is shown.
export async function submitConnectTargetPick(
  accessToken: string,
  matchId: string,
  targetPlayerName: string,
): Promise<ConnectTargetPickSubmitResponse> {
  return apiRequest<ConnectTargetPickSubmitResponse>(accessToken, `/matches/${matchId}/target-pick`, {
    method: 'POST',
    body: JSON.stringify({ targetPlayerName }),
  });
}

// REQ-1406/1407: submits one incremental chain-connector step (POST
// /matches/{matchId}/chain-steps). Always resolves to a normal 200 body —
// a wrong or unresolvable guess is never an ApiError, mirroring
// GuessEndpoints'/rounds.ts's own "wrong guess is not an error" precedent.
// Only genuine precondition failures (404/403/409 not-active/409
// chain-complete/409 already-forfeited) or a technical 503 live-lookup
// failure are left to throw.
//
// Design change (2026-09-04, REQ-1406, ADR-0104): no longer takes a
// claimedClubName — the caller names only a candidate player; the server
// computes which club(s) actually connect them.
export async function submitConnectChainStep(
  accessToken: string,
  matchId: string,
  candidatePlayerName: string,
): Promise<ConnectSubmitChainStepResponse> {
  return apiRequest<ConnectSubmitChainStepResponse>(accessToken, `/matches/${matchId}/chain-steps`, {
    method: 'POST',
    body: JSON.stringify({ candidatePlayerName }),
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

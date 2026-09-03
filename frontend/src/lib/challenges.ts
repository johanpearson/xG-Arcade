import type { ChallengeResponse } from './types';
import { apiRequest } from './apiClient';

// REQ-1402 (S-217): sends a Pending Challenge from the caller to
// challengedUserId — only allowed between existing friends
// (XGArcade.Api.Social.ChallengeEndpoints — POST /challenges). Errors (403
// "Not friends", 409 "Duplicate pending challenge") left to throw as
// ApiError, same "server's own detail text shown inline" convention every
// other domain file in this directory already uses.
export async function sendChallenge(accessToken: string, challengedUserId: string): Promise<ChallengeResponse> {
  return apiRequest<ChallengeResponse>(accessToken, '/challenges', {
    method: 'POST',
    body: JSON.stringify({ challengedUserId }),
  });
}

// REQ-1402: the challenged user accepts — resultingMatchId is populated on
// the response the instant this succeeds (a new ConnectMatch was just
// created server-side; see design-document.md SCREEN-15's "Challenges tab"
// note for what this screen does, and deliberately does not do, with that
// id). Errors (404 "Challenge not found", 403 "Not your challenge", 409
// "Already resolved") left to throw.
export async function acceptChallenge(accessToken: string, id: string): Promise<ChallengeResponse> {
  return apiRequest<ChallengeResponse>(accessToken, `/challenges/${id}/accept`, {
    method: 'POST',
  });
}

// REQ-1402: the challenged user declines — same error set as
// acceptChallenge above.
export async function declineChallenge(accessToken: string, id: string): Promise<ChallengeResponse> {
  return apiRequest<ChallengeResponse>(accessToken, `/challenges/${id}/decline`, {
    method: 'POST',
  });
}

// REQ-1402: every challenge currently Pending where the caller is the
// challenged party (GET /challenges/pending).
export async function fetchPendingChallenges(accessToken: string): Promise<ChallengeResponse[]> {
  return apiRequest<ChallengeResponse[]>(accessToken, '/challenges/pending');
}

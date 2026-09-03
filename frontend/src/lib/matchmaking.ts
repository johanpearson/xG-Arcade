import type { MatchmakingOptInResponse } from './types';
import { apiRequest } from './apiClient';

// REQ-1403 (S-217): opts the caller into random matchmaking for the next
// 12 hours (XGArcade.Api.Social.MatchmakingEndpoints — POST
// /matchmaking/opt-in). Opting in IS the consent — there is no
// accept/decline step, and no GET/listing endpoint exists for this
// resource (see design-document.md SCREEN-15's "Matchmaking tab" note for
// the resulting, deliberately session-local-only UI treatment).
export async function optInToMatchmaking(accessToken: string): Promise<MatchmakingOptInResponse> {
  return apiRequest<MatchmakingOptInResponse>(accessToken, '/matchmaking/opt-in', {
    method: 'POST',
  });
}

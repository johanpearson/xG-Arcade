import type { FriendRequestResponse, FriendshipResponse } from './types';
import { apiRequest } from './apiClient';

// REQ-1401 (S-217): sends a Pending FriendRequest from the caller to
// recipientUserId (XGArcade.Api.Social.FriendEndpoints — POST
// /friends/requests). Errors are left to throw as ApiError (400 "Cannot
// friend yourself", 404 "Recipient not found", 409 "Already friends" /
// "Duplicate pending request") so the caller shows the server's own detail
// text inline, same convention every other domain file in this directory
// already uses.
export async function sendFriendRequest(
  accessToken: string,
  recipientUserId: string,
): Promise<FriendRequestResponse> {
  return apiRequest<FriendRequestResponse>(accessToken, '/friends/requests', {
    method: 'POST',
    body: JSON.stringify({ recipientUserId }),
  });
}

// REQ-1401: the recipient accepts a pending request
// (POST /friends/requests/{id}/accept). Errors (404 "Friend request not
// found", 403 "Not your request", 409 "Already resolved") left to throw.
export async function acceptFriendRequest(accessToken: string, id: string): Promise<FriendRequestResponse> {
  return apiRequest<FriendRequestResponse>(accessToken, `/friends/requests/${id}/accept`, {
    method: 'POST',
  });
}

// REQ-1401: the recipient declines a pending request — same error set as
// acceptFriendRequest above.
export async function declineFriendRequest(accessToken: string, id: string): Promise<FriendRequestResponse> {
  return apiRequest<FriendRequestResponse>(accessToken, `/friends/requests/${id}/decline`, {
    method: 'POST',
  });
}

// REQ-1401: every request currently Pending where the caller is the
// recipient (GET /friends/requests/pending).
export async function fetchPendingFriendRequests(accessToken: string): Promise<FriendRequestResponse[]> {
  return apiRequest<FriendRequestResponse[]>(accessToken, '/friends/requests/pending');
}

// REQ-1401: every current friendship of the caller's (GET /friends) —
// friendUserId is always "the other person," never a raw UserAId/UserBId
// pair (see FriendshipResponse's own doc comment in lib/types.ts).
export async function fetchFriends(accessToken: string): Promise<FriendshipResponse[]> {
  return apiRequest<FriendshipResponse[]>(accessToken, '/friends');
}

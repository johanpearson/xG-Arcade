// REQ-1401 (S-217, extended 2026-09-03 REQ-1401/1402 display-name follow-up):
// mirrors FriendRequestResponse exactly
// (backend/src/XGArcade.Api/Social/FriendEndpoints.cs) — `status` is the
// backend's FriendRequestStatus enum serialized as its string name
// ("Pending" | "Accepted" | "Declined"). `resolvedAt` is null exactly while
// `status` is "Pending". `requesterDisplayName`/`recipientDisplayName` were
// added alongside the raw ids (batch-resolved server-side via
// IUserRepository.GetByIdsAsync, never null/optional) — this closes
// design-document.md SCREEN-15's former "Identity gap" note; every list
// built from this shape now renders a real display name instead of
// `shortUserId()`'s truncated-id stand-in.
export interface FriendRequestResponse {
  id: string;
  requesterUserId: string;
  requesterDisplayName: string;
  recipientUserId: string;
  recipientDisplayName: string;
  status: string;
  createdAt: string;
  resolvedAt: string | null;
}

// REQ-1401 (S-217, extended 2026-09-03): mirrors FriendshipResponse exactly.
// `friendUserId` is always the *other* user relative to the caller (never a
// raw UserAId/UserBId pair) — the backend already normalizes this, so the
// frontend never needs to know which side of the pair the caller was.
// `friendDisplayName` is that same other user's display name (never null/
// optional) — see FriendRequestResponse's own comment above for the same
// closed "Identity gap" note.
export interface FriendshipResponse {
  id: string;
  friendUserId: string;
  friendDisplayName: string;
  createdAt: string;
}

// REQ-1402 (S-217, extended 2026-09-03): mirrors ChallengeResponse exactly
// (backend/src/XGArcade.Api/Social/ChallengeEndpoints.cs) — `status` is the
// backend's ChallengeStatus enum serialized as its string name ("Pending" |
// "Accepted" | "Declined"). `resultingMatchId` is null until a successful
// accept creates the real `ConnectMatch` row server-side (S-218's separate,
// not-yet-built scope owns whatever happens with that id next — this
// story never navigates anywhere with it, see SCREEN-15's own "Challenges
// tab" note). `challengerDisplayName`/`challengedDisplayName` were added
// alongside the raw ids (never null/optional) — same closed "Identity gap"
// note as FriendRequestResponse/FriendshipResponse above.
export interface ChallengeResponse {
  id: string;
  challengerUserId: string;
  challengerDisplayName: string;
  challengedUserId: string;
  challengedDisplayName: string;
  status: string;
  createdAt: string;
  resolvedAt: string | null;
  resultingMatchId: string | null;
}

// REQ-1403 (S-217): mirrors MatchmakingOptInResponse exactly
// (backend/src/XGArcade.Api/Social/MatchmakingEndpoints.cs). `status` is
// the backend's MatchmakingOptInStatus enum serialized as its string name
// (e.g. "Waiting"). `resultingMatchId` is always null on this response in
// practice — pairing happens later, in a separate sweep job, never
// synchronously with the opt-in call itself — but is typed nullable rather
// than omitted since the shape genuinely allows it. There is no GET/list
// endpoint for this resource (see SCREEN-15's own "Known limitation" note)
// — this response is the only source of truth the frontend ever has for a
// given opt-in, and only for the lifetime of the page it was created on.
export interface MatchmakingOptInResponse {
  id: string;
  optedInAt: string;
  expiresAt: string;
  status: string;
  resultingMatchId: string | null;
}

// REQ-1411 (S-217): mirrors NotificationSummaryResponse exactly
// (backend/src/XGArcade.Api/Notifications/NotificationEndpoints.cs) — the
// header nav's own "Friends" badge count is
// `pendingFriendRequestCount + pendingChallengeCount +
// matchesAwaitingActionCount` (see design-document.md SCREEN-07's own
// 2026-09-03 status note for why a combined count, not `hasPending` alone,
// was chosen for that badge). `hasPending` is carried through unused by
// the badge itself (the three counts already imply it) but kept on this
// type since it's a real response field, not to be silently dropped.
export interface NotificationSummaryResponse {
  pendingFriendRequestCount: number;
  pendingChallengeCount: number;
  matchesAwaitingActionCount: number;
  hasPending: boolean;
}

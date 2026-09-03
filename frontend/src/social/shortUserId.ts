// REQ-1401/1402 (S-217, design-document.md SCREEN-15's "Identity gap"
// note): no backend endpoint resolves an arbitrary userId to a
// displayName — FriendRequestResponse/ChallengeResponse/FriendshipResponse
// only ever carry the other party's raw userId. Rather than fabricate a
// name (or reach into PlayerNameIndex/PlayerData, which would violate
// ADR-0007's boundary for an unrelated reason — those tables are
// football-player data, not xG Arcade account data), every list built from
// one of those three shapes renders this short, stable, deterministic
// label instead of a real name: "Player " + the id's first 8 characters,
// uppercased (a GUID's own first hyphen-delimited segment). This is a
// known, flagged UX gap, not a design choice worth keeping — see the
// SCREEN-15 status note for the real fix (extending those response shapes
// with a displayName field, mirroring PendingSuggestion's own
// submittingUserDisplayName).
export function shortUserId(userId: string): string {
  return `Player ${userId.slice(0, 8).toUpperCase()}`;
}

// S-218 (design-document.md SCREEN-16): a handful of xG Connect response
// shapes (ConnectMatchListItem.opponentUserId, ConnectMatchDetail.
// opponentUserId, ConnectChatMessage.senderUserId) are nullable — they go
// null once REQ-710 account-deletion anonymization has run for that user,
// unlike FriendRequestResponse/ChallengeResponse/FriendshipResponse above,
// none of which can currently go null. Rather than every one of those
// call sites re-deriving the same `userId ? shortUserId(userId) : '…'`
// branch, this wraps it once: a null id renders as "Deleted account" — a
// plain, factual label (not a fabricated name), consistent with this
// file's own "never fabricate a name" principle.
export function shortUserIdOrDeleted(userId: string | null): string {
  return userId ? shortUserId(userId) : 'Deleted account';
}

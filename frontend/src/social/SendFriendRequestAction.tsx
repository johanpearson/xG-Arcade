import { useCallback, useState } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { fetchFriends, fetchPendingFriendRequests, sendFriendRequest } from '../lib/friends';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import './SendFriendRequestAction.css';

export interface SendFriendRequestActionProps {
  accessToken: string;
  // The account currently signed in — used only to decide which of this
  // component's own three states to render (see the doc comment below);
  // never sent as part of the actual request body.
  viewerUserId: string;
  targetUserId: string;
  onAuthError: () => void;
  // Optional: lets this component link out to SCREEN-15 when the viewed
  // player has already sent the viewer a pending request — see
  // design-document.md SCREEN-13's own 2026-09-03 status note.
  onOpenFriends?: () => void;
}

// REQ-1401 (S-217, design-document.md SCREEN-13's 2026-09-03 status note):
// SCREEN-13's "Send friend request" action — the one deliberately narrow
// entry point into REQ-1401's friend-request flow this app has (there is
// no user-search-by-name endpoint, and none should be added against
// PlayerNameIndex/PlayerData — ADR-0007's boundary is about a different
// concept). Mounted by UserStatsScreen only when viewerUserId differs from
// the userId being viewed (own-profile hidden entirely).
//
// Renders nothing (not even a loading flicker) until both underlying
// fetches resolve, and nothing at all on a fetch failure other than a 401
// — this action is supplementary to the screen's real purpose, never
// allowed to block the rest of it from rendering, mirroring
// PlayerAvatar's own "quiet degrade" precedent.
export function SendFriendRequestAction({
  accessToken,
  viewerUserId,
  targetUserId,
  onAuthError,
  onOpenFriends,
}: SendFriendRequestActionProps) {
  const friendsFetchFn = useCallback(() => fetchFriends(accessToken), [accessToken]);
  const { data: friends, loadError: friendsError } = useAuthedFetch(friendsFetchFn, { onAuthError });

  const pendingFetchFn = useCallback(() => fetchPendingFriendRequests(accessToken), [accessToken]);
  const { data: pendingRequests, loadError: pendingError } = useAuthedFetch(pendingFetchFn, { onAuthError });

  const [submitting, setSubmitting] = useState(false);
  const [sent, setSent] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);

  if (viewerUserId === targetUserId) return null;
  // Quiet degrade: a load failure here (other than 401, already escalated
  // by useAuthedFetch) never blocks or errors the rest of UserStatsScreen —
  // it just leaves this action unrendered.
  if (friendsError || pendingError) return null;
  if (friends === null || pendingRequests === null) return null;

  const isFriend = friends.some((friend) => friend.friendUserId === targetUserId);
  const incomingFromTarget = pendingRequests.find((request) => request.requesterUserId === targetUserId);

  async function handleSend() {
    setSendError(null);
    setSubmitting(true);
    try {
      await sendFriendRequest(accessToken, targetUserId);
      setSent(true);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setSendError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  if (isFriend) {
    return (
      <p className="send-friend-request-action__status">You&apos;re already friends.</p>
    );
  }

  if (incomingFromTarget) {
    return (
      <div className="send-friend-request-action">
        <p className="send-friend-request-action__status">This player already sent you a friend request.</p>
        {onOpenFriends && (
          <button type="button" className="send-friend-request-action__link" onClick={onOpenFriends}>
            Respond in Friends &amp; Challenges
          </button>
        )}
      </div>
    );
  }

  if (sent) {
    return <p className="send-friend-request-action__success">Friend request sent.</p>;
  }

  return (
    <div className="send-friend-request-action">
      <button type="button" disabled={submitting} onClick={handleSend}>
        {submitting ? 'Sending…' : 'Send friend request'}
      </button>
      {sendError && (
        <p className="send-friend-request-action__error" role="alert">
          {sendError}
        </p>
      )}
    </div>
  );
}

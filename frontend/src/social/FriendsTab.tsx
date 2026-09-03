import { useCallback, useState } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import {
  acceptFriendRequest,
  declineFriendRequest,
  fetchFriends,
  fetchPendingFriendRequests,
} from '../lib/friends';
import { sendChallenge } from '../lib/challenges';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import type { FriendRequestResponse, FriendshipResponse } from '../lib/types';
import { shortUserId } from './shortUserId';

export interface FriendsTabProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-1401/1402 (S-217, design-document.md SCREEN-15's "Friends tab"): two
// independent GETs (pending friend requests where the caller is recipient,
// and the caller's own friendships), each with its own refetch — mirrors
// AdminScreen.tsx's own "two independent useAuthedFetch instances" pattern
// rather than one combined fetch, since accepting/declining a request only
// needs to refresh the pending list (and, on accept, the friends list too),
// never the other way around.
export function FriendsTab({ accessToken, onAuthError }: FriendsTabProps) {
  const pendingFetchFn = useCallback(() => fetchPendingFriendRequests(accessToken), [accessToken]);
  const {
    data: pendingRequests,
    loadError: pendingError,
    refetch: refetchPending,
  } = useAuthedFetch(pendingFetchFn, { onAuthError });

  const friendsFetchFn = useCallback(() => fetchFriends(accessToken), [accessToken]);
  const { data: friends, loadError: friendsError, refetch: refetchFriends } = useAuthedFetch(friendsFetchFn, {
    onAuthError,
  });

  async function handleRequestResolved() {
    await Promise.all([refetchPending(), refetchFriends()]);
  }

  return (
    <div className="friends-screen__tab-panel">
      <section className="friends-screen__section">
        <h3 className="friends-screen__section-title">
          Friend requests
          {pendingRequests && pendingRequests.length > 0 ? ` (${pendingRequests.length})` : ''}
        </h3>
        {pendingError && (
          <p className="friends-screen__error" role="alert">
            {pendingError}
          </p>
        )}
        {pendingRequests === null && !pendingError && <p className="friends-screen__status">Loading…</p>}
        {pendingRequests !== null && pendingRequests.length === 0 && (
          <p className="friends-screen__empty">No pending friend requests.</p>
        )}
        {pendingRequests !== null && pendingRequests.length > 0 && (
          <ul className="friends-screen__list">
            {pendingRequests.map((request) => (
              <PendingFriendRequestRow
                key={request.id}
                accessToken={accessToken}
                request={request}
                onAuthError={onAuthError}
                onResolved={handleRequestResolved}
              />
            ))}
          </ul>
        )}
      </section>

      <section className="friends-screen__section">
        <h3 className="friends-screen__section-title">My friends</h3>
        {friendsError && (
          <p className="friends-screen__error" role="alert">
            {friendsError}
          </p>
        )}
        {friends === null && !friendsError && <p className="friends-screen__status">Loading…</p>}
        {friends !== null && friends.length === 0 && (
          // design-document.md §5: empty states are invitations.
          <p className="friends-screen__empty">
            You don&apos;t have any friends yet. Visit a player&apos;s stats page to send a friend request.
          </p>
        )}
        {friends !== null && friends.length > 0 && (
          <ul className="friends-screen__list">
            {friends.map((friend) => (
              <FriendRow key={friend.id} accessToken={accessToken} friend={friend} onAuthError={onAuthError} />
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

interface PendingFriendRequestRowProps {
  accessToken: string;
  request: FriendRequestResponse;
  onAuthError: () => void;
  onResolved: () => Promise<void>;
}

function PendingFriendRequestRow({ accessToken, request, onAuthError, onResolved }: PendingFriendRequestRowProps) {
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function resolve(action: (accessToken: string, id: string) => Promise<FriendRequestResponse>) {
    setError(null);
    setSubmitting(true);
    try {
      await action(accessToken, request.id);
      await onResolved();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <li className="friends-screen__row">
      <span className="friends-screen__row-name">{shortUserId(request.requesterUserId)}</span>
      <span className="friends-screen__row-actions">
        <button type="button" disabled={submitting} onClick={() => resolve(acceptFriendRequest)}>
          Accept
        </button>
        <button type="button" disabled={submitting} onClick={() => resolve(declineFriendRequest)}>
          Decline
        </button>
      </span>
      {error && (
        <p className="friends-screen__error" role="alert">
          {error}
        </p>
      )}
    </li>
  );
}

interface FriendRowProps {
  accessToken: string;
  friend: FriendshipResponse;
  onAuthError: () => void;
}

// REQ-1402: "challenging a friend" lives here, on the friends list itself
// — design-document.md SCREEN-15's own framing ("friends list is also
// where you'd challenge a friend").
function FriendRow({ accessToken, friend, onAuthError }: FriendRowProps) {
  const [submitting, setSubmitting] = useState(false);
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleChallenge() {
    setError(null);
    setSubmitting(true);
    try {
      await sendChallenge(accessToken, friend.friendUserId);
      setSent(true);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <li className="friends-screen__row">
      <span className="friends-screen__row-name">{shortUserId(friend.friendUserId)}</span>
      {sent ? (
        <span className="friends-screen__success">Challenge sent.</span>
      ) : (
        <button type="button" disabled={submitting} onClick={handleChallenge}>
          Challenge
        </button>
      )}
      {error && (
        <p className="friends-screen__error" role="alert">
          {error}
        </p>
      )}
    </li>
  );
}

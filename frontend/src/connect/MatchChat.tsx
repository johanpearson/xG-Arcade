import { useCallback, useEffect, useState } from 'react';
import { fetchConnectChatMessages, sendConnectChatMessage } from '../lib/connectMatches';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import { useSubmitAction } from '../lib/useSubmitAction';
import { shortUserIdOrDeleted } from '../social/shortUserId';

export interface MatchChatProps {
  matchId: string;
  accessToken: string;
  viewerUserId?: string;
  onAuthError: () => void;
}

const POLL_INTERVAL_MS = 15_000;
const MAX_MESSAGE_LENGTH = 1000;

// REQ-1410 (design-document.md SCREEN-16's "In-match chat"): visible
// regardless of match phase — mounted by MatchScreen unconditionally, never
// gated on `status`. Polled on the same 15s self-rescheduling cadence as
// useNotificationSummary.ts, since REQ-1410's own acceptance criteria says
// this "does not require a live push update."
export function MatchChat({ matchId, accessToken, viewerUserId, onAuthError }: MatchChatProps) {
  // useCallback here is load-bearing, not stylistic — useAuthedFetch's own
  // mount effect depends on this function's identity; an unmemoized
  // function recreated every render would retrigger that effect (and a
  // fresh fetch) on every render, including the render its own successful
  // fetch just caused, producing a runaway fetch loop instead of a plain
  // 15s poll. Mirrors FriendsTab.tsx's/ChallengesTab.tsx's own
  // `useCallback(() => fetchX(accessToken), [accessToken])` shape.
  const fetchFn = useCallback(() => fetchConnectChatMessages(accessToken, matchId), [accessToken, matchId]);
  const { data: messages, loadError, refetch } = useAuthedFetch(fetchFn, { onAuthError });
  const [messageText, setMessageText] = useState('');
  const { submitting, error, run } = useSubmitAction<void>({ onAuthError });

  useEffect(() => {
    let cancelled = false;
    let timeoutId: number | undefined;

    function scheduleNext() {
      timeoutId = window.setTimeout(async () => {
        if (cancelled) return;
        await refetch();
        if (!cancelled) scheduleNext();
      }, POLL_INTERVAL_MS);
    }
    scheduleNext();

    return () => {
      cancelled = true;
      if (timeoutId !== undefined) window.clearTimeout(timeoutId);
    };
  }, [refetch]);

  function handleSend() {
    const trimmed = messageText.trim();
    if (!trimmed) return;
    run(
      () => sendConnectChatMessage(accessToken, matchId, trimmed).then(() => undefined),
      async () => {
        setMessageText('');
        await refetch();
      },
    );
  }

  return (
    <section className="connect-match__section connect-match__chat">
      <h3 className="connect-match__section-title">Chat</h3>
      {loadError && (
        <p className="connect-match__error" role="alert">
          {loadError}
        </p>
      )}
      {messages === null && !loadError && <p className="connect-match__status">Loading…</p>}
      {messages && messages.length === 0 && <p className="connect-match__status">No messages yet — say hello.</p>}
      {messages && messages.length > 0 && (
        <ul className="connect-match__chat-messages">
          {messages.map((message) => (
            <li key={message.id} className="connect-match__chat-message">
              <span className="connect-match__chat-sender">
                {message.senderUserId === viewerUserId ? 'You' : shortUserIdOrDeleted(message.senderUserId)}
              </span>
              <span className="connect-match__chat-text">{message.messageText}</span>
              <span className="connect-match__chat-time mono-figure">{new Date(message.sentAt).toLocaleTimeString()}</span>
            </li>
          ))}
        </ul>
      )}

      <div className="connect-match__chat-form">
        <textarea
          className="connect-match__chat-input"
          value={messageText}
          onChange={(event) => setMessageText(event.target.value)}
          maxLength={MAX_MESSAGE_LENGTH}
          placeholder="Say something…"
          aria-label="Chat message"
          disabled={submitting}
        />
        <span className="connect-match__hint mono-figure">
          {messageText.length}/{MAX_MESSAGE_LENGTH}
        </span>
        <button
          type="button"
          className="connect-match__button"
          disabled={submitting || messageText.trim().length === 0}
          onClick={handleSend}
        >
          {submitting ? 'Sending…' : 'Send message'}
        </button>
        {error && (
          <p className="connect-match__error" role="alert">
            {error}
          </p>
        )}
      </div>
    </section>
  );
}

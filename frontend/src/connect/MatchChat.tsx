import { useCallback, useState } from 'react';
import { ApiError } from '../lib/apiClient';
import { fetchConnectChatMessages, sendConnectChatMessage } from '../lib/connectMatches';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import { usePolling } from '../lib/usePolling';
import { useSubmitAction } from '../lib/useSubmitAction';

export interface MatchChatProps {
  matchId: string;
  accessToken: string;
  viewerUserId?: string;
  onAuthError: () => void;
  // REQ-1419: computed by MatchScreen.tsx from `resolvedAt + 1h` — true
  // means the send path is closed. The read path (message history below)
  // is completely unaffected either way, matching REQ-1410's unchanged
  // "stays visible/readable indefinitely" rule.
  chatClosed: boolean;
}

// REQ-1419: shown in place of the send form once `chatClosed` is true —
// text, not color-only, states plainly what happened (the chat closed) and
// implicitly why (the one-hour-after-resolution window), matching this
// codebase's "explain what happened" copy convention for closed/ended
// states.
const CHAT_CLOSED_NOTICE = "This match's chat closed one hour after it ended.";

const POLL_INTERVAL_MS = 15_000;
const MAX_MESSAGE_LENGTH = 1000;

// REQ-1410 (design-document.md SCREEN-16's "In-match chat"): visible
// regardless of match phase — mounted by MatchScreen unconditionally, never
// gated on `status`. Polled on the same 15s self-rescheduling cadence as
// useNotificationSummary.ts, since REQ-1410's own acceptance criteria says
// this "does not require a live push update." The poll loop itself is
// `usePolling` (lib/usePolling.ts, S-218 quality-gate follow-up) — this
// file used to hand-roll its own self-rescheduling `setTimeout` effect
// here, byte-for-byte identical to MatchScreen.tsx's own, until that
// duplication was extracted.
export function MatchChat({ matchId, accessToken, viewerUserId, onAuthError, chatClosed }: MatchChatProps) {
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
  // REQ-1419: the client-computed `chatClosed` prop is derived from
  // `resolvedAt` at render time — a real race is possible right at the
  // boundary (the prop said "still open" a moment ago, the server disagrees
  // by the time the request lands). This local flag lets a 409 from the
  // send itself immediately switch to the same read-only notice, rather
  // than requiring a refetch/re-render of `detail` to notice.
  const [closedByRace, setClosedByRace] = useState(false);
  const effectivelyClosed = chatClosed || closedByRace;

  usePolling(refetch, POLL_INTERVAL_MS);

  function handleSend() {
    const trimmed = messageText.trim();
    if (!trimmed) return;
    run(
      async () => {
        try {
          await sendConnectChatMessage(accessToken, matchId, trimmed);
        } catch (err) {
          // REQ-1419: a 409 here means the one-hour window closed between
          // this component's last render and the request landing — treat it
          // the same as the proactive `chatClosed` prop from then on, so a
          // retried send isn't offered. The server's own detail text still
          // surfaces via `error` below (describeError/useSubmitAction), same
          // convention TargetPickPanel.tsx's "already connected" 409 uses.
          if (err instanceof ApiError && err.status === 409) {
            setClosedByRace(true);
          }
          throw err;
        }
      },
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
                {message.senderUserId === viewerUserId ? 'You' : message.senderDisplayName ?? 'a deleted user'}
              </span>
              <span className="connect-match__chat-text">{message.messageText}</span>
              <span className="connect-match__chat-time mono-figure">{new Date(message.sentAt).toLocaleTimeString()}</span>
            </li>
          ))}
        </ul>
      )}

      {/* REQ-1419: the read path above (message history) is unaffected
          either way — only the send path is gated on `effectivelyClosed`.
          A plain text notice, never color-only, replaces the form entirely
          rather than merely disabling it, so it's unambiguous why no new
          message can be sent. */}
      {effectivelyClosed ? (
        <p className="connect-match__status" role="status">
          {CHAT_CLOSED_NOTICE}
        </p>
      ) : (
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
        </div>
      )}
      {/* Kept outside the form/notice branch above so the server's own 409
          detail text (the race case above) still surfaces for the one
          render where `error` is set, even though `effectivelyClosed` has
          already flipped the form itself away by then. */}
      {error && (
        <p className="connect-match__error" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}

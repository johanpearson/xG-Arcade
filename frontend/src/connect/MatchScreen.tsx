import { useCallback, useState } from 'react';
import { fetchConnectMatchDetail } from '../lib/connectMatches';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import { usePolling } from '../lib/usePolling';
import { ChainBuilder } from './ChainBuilder';
import { ConnectScoringExplainer } from './ConnectScoringExplainer';
import { DisputeReview } from './DisputeReview';
import { MatchChat } from './MatchChat';
import { MatchResolution } from './MatchResolution';
import { TargetPickPanel } from './TargetPickPanel';
import './ConnectMatchScreen.css';

export interface MatchScreenProps {
  matchId: string;
  accessToken: string;
  viewerUserId?: string;
  onAuthError: () => void;
  onBack: () => void;
}

// REQ-1404/1405/1406/1407/1408/1409/1410 (design-document.md SCREEN-16): the
// single-match gameplay screen, driven entirely by GET
// /matches/{matchId} — renders the right sub-UI for whichever phase the
// match is actually in (`status`). Polls that same endpoint on the same
// 15s cadence MatchChat.tsx/useNotificationSummary.ts already use, but only
// while the match hasn't resolved yet (usePolling's own `enabled` option
// below) — a plain poll, not a live countdown, since REQ-1405's deadline
// display (the `deadlineUtc` passed to ChainBuilder) only needs an
// eventually-accurate static timestamp, never a ticking clock.
const POLL_INTERVAL_MS = 15_000;

// REQ-1419: the send path (never the read path — chat history stays fully
// visible/readable indefinitely, REQ-1410 unchanged) closes one hour after
// a match resolves. Computed client-side purely so the UI can show the
// read-only notice proactively, before ever attempting a send — the server
// is still the real source of truth (a 409 rejects a send that lands right
// at/after the boundary regardless of what the client computed a moment
// earlier).
const CHAT_CLOSE_WINDOW_MS = 60 * 60 * 1000;

export function MatchScreen({ matchId, accessToken, viewerUserId, onAuthError, onBack }: MatchScreenProps) {
  // useCallback is load-bearing here — see MatchChat.tsx's identical
  // comment for why an unmemoized fetchFn would retrigger useAuthedFetch's
  // own mount effect on every render, not just every real poll tick.
  const fetchFn = useCallback(() => fetchConnectMatchDetail(accessToken, matchId), [accessToken, matchId]);
  const { data: detail, loadError, refetch } = useAuthedFetch(fetchFn, { onAuthError });

  // usePolling (lib/usePolling.ts, S-218 quality-gate follow-up): stops
  // polling once the match has resolved, via `enabled`, rather than the
  // hand-rolled `if (!detail || detail.status === 'Resolved') return;`
  // early-return this file's own poll effect used to open with (before that
  // effect's exact shape, duplicated byte-for-byte in MatchChat.tsx, was
  // extracted into this shared hook).
  usePolling(refetch, POLL_INTERVAL_MS, { enabled: detail !== null && detail.status !== 'Resolved' });

  // REQ-1416: gates ConnectScoringExplainer — same "own local disclosure
  // state" shape GridScreen.tsx/PathScreen.tsx already use for their own
  // (ⓘ) toggles.
  const [explainerOpen, setExplainerOpen] = useState(false);

  return (
    <div className="connect-match">
      <div className="connect-match__title-row">
        <button type="button" className="connect-match__back" onClick={onBack}>
          &larr; Back to matches
        </button>
        {/* REQ-1416: opens SCREEN-17's own rules explainer — reachable at
            any point in an active match, mirroring GridScreen.tsx's/
            PathScreen.tsx's own (ⓘ) placement next to their own title-row
            content, and reusing their "How scoring works" label (see
            ConnectScoringExplainer.tsx's own doc comment for why). Also
            reachable, before any match exists, from ConnectEntryScreen. */}
        <button
          type="button"
          className="connect-match__info-toggle"
          onClick={() => setExplainerOpen(true)}
          aria-label="How scoring works"
        >
          ⓘ
        </button>
      </div>

      {loadError && (
        <p className="connect-match__error" role="alert">
          {loadError}
        </p>
      )}
      {detail === null && !loadError && <p className="connect-match__status">Loading…</p>}

      {detail && (
        <>
          <p className="connect-match__opponent">Opponent: {detail.opponentDisplayName ?? 'a deleted user'}</p>

          {detail.status === 'AwaitingTargetPicks' && (
            <TargetPickPanel
              matchId={matchId}
              accessToken={accessToken}
              myTargetPick={detail.myTargetPick}
              onAuthError={onAuthError}
              onSubmitted={refetch}
            />
          )}

          {detail.status === 'Active' && detail.myTargetPick && detail.opponentTargetPick && (
            <ChainBuilder
              matchId={matchId}
              accessToken={accessToken}
              myTargetPick={detail.myTargetPick}
              opponentTargetPick={detail.opponentTargetPick}
              myChainSteps={detail.myChainSteps}
              myTerminalState={detail.myTerminalState}
              opponentTerminalState={detail.opponentTerminalState}
              deadlineUtc={detail.deadlineUtc}
              onAuthError={onAuthError}
              onChanged={refetch}
            />
          )}

          {/* REQ-1412/1413: the opponent-review half of the dispute flow —
              own independent fetch/poll (DisputeReview.tsx's own top-of-file
              comment), same "only while Active" gate ChainBuilder itself
              renders under, since a dispute can only exist against a step
              submitted during that same phase. */}
          {detail.status === 'Active' && detail.myTargetPick && detail.opponentTargetPick && (
            <DisputeReview matchId={matchId} accessToken={accessToken} onAuthError={onAuthError} onReviewed={refetch} />
          )}

          {detail.status === 'Resolved' && <MatchResolution detail={detail} />}

          <MatchChat
            matchId={matchId}
            accessToken={accessToken}
            viewerUserId={viewerUserId}
            onAuthError={onAuthError}
            // REQ-1419: null resolvedAt (never resolved) never triggers the
            // cutoff — see CHAT_CLOSE_WINDOW_MS's own comment above.
            chatClosed={detail.resolvedAt !== null && Date.now() - new Date(detail.resolvedAt).getTime() > CHAT_CLOSE_WINDOW_MS}
          />
        </>
      )}

      {explainerOpen && <ConnectScoringExplainer onClose={() => setExplainerOpen(false)} />}
    </div>
  );
}

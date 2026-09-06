import { useCallback, useState } from 'react';
import { approveChainStepDispute, denyChainStepDispute, fetchConnectChainStepDisputes } from '../lib/connectMatches';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import { usePolling } from '../lib/usePolling';
import { useSubmitAction } from '../lib/useSubmitAction';

export interface DisputeReviewProps {
  matchId: string;
  accessToken: string;
  onAuthError: () => void;
  // A review (approve/deny) can change the match's own resolved state — it
  // may resolve a match that was only waiting on this dispute (REQ-1413's
  // resolution gate), or reopen the caller's own gameplay after an
  // Approve clears their provisional bust — so this always fires alongside
  // this component's own dispute-list refetch, mirroring how ChainBuilder's
  // onChanged prop is called after any outcome that changed persisted
  // state. Named distinctly from ChainBuilder's onChanged only because
  // MatchScreen.tsx passes its own refetch to both, under each one's own
  // prop name, for readability at the call site.
  onReviewed: () => void;
}

const POLL_INTERVAL_MS = 15_000;

// REQ-1412/1413 (design-document.md SCREEN-16 addendum, ADR-0109): the
// opponent-review half of the dispute flow. Rendered as a sibling to
// ChainBuilder.tsx, not folded into it — this needs its own data (GET
// /matches/{matchId}/disputes), which ChainBuilder never fetches (see that
// file's own `raisedDispute` comment for why it deliberately doesn't).
// Follows MatchChat.tsx's exact established pattern for a sibling
// component with its own independent fetch+poll: useAuthedFetch for the
// mount fetch, usePolling for the same 15s cadence MatchScreen.tsx/
// MatchChat.tsx already use, rather than threading dispute data through
// ChainBuilder's props or MatchScreen's own match-detail fetch. This is the
// only durable, always-accurate source of "is there a Pending dispute, and
// whose is it" — unlike ChainBuilder's own ephemeral, component-local
// acknowledgment of a dispute IT just raised.
export function DisputeReview({ matchId, accessToken, onAuthError, onReviewed }: DisputeReviewProps) {
  const fetchFn = useCallback(() => fetchConnectChainStepDisputes(accessToken, matchId), [accessToken, matchId]);
  const { data: disputes, loadError, refetch } = useAuthedFetch(fetchFn, { onAuthError });
  usePolling(refetch, POLL_INTERVAL_MS);

  // Tracks which single dispute a review action is currently in flight for
  // — disables only that row's own two buttons, not every row's, while an
  // approve/deny call is pending.
  const [actioningId, setActioningId] = useState<string | null>(null);
  const { error: reviewError, run: runReview } = useSubmitAction<void>({ onAuthError });

  function handleReview(disputeId: string, approve: boolean) {
    setActioningId(disputeId);
    runReview(
      () =>
        (approve ? approveChainStepDispute(accessToken, matchId, disputeId) : denyChainStepDispute(accessToken, matchId, disputeId)).then(
          () => undefined,
        ),
      async () => {
        setActioningId(null);
        await refetch();
        onReviewed();
      },
    );
  }

  if (loadError) {
    return (
      <section className="connect-match__section">
        <p className="connect-match__error" role="alert">
          {loadError}
        </p>
      </section>
    );
  }

  if (disputes === null) {
    return null;
  }

  const myPendingDisputes = disputes.filter((dispute) => dispute.raisedByMe && dispute.status === 'Pending');
  // REQ-1413: only the OTHER participant may review a dispute — filtering
  // to `!raisedByMe` here means this section can never render an
  // "Approve"/"Deny" pair for the caller's own dispute, mirroring the
  // server's own CannotReviewOwnDispute check as a UI-level guard, not a
  // substitute for it (the endpoint still enforces this regardless).
  // Resolved (Approved/Denied) disputes are deliberately dropped from both
  // lists once reviewed — REQ-1414's own admin suggestion queue is where an
  // Approved dispute's durable record lives; there is no "resolved dispute
  // history" view here (kept simple, per this story's own scope).
  const reviewableDisputes = disputes.filter((dispute) => !dispute.raisedByMe && dispute.status === 'Pending');

  if (myPendingDisputes.length === 0 && reviewableDisputes.length === 0) {
    return null;
  }

  return (
    <section className="connect-match__section">
      <h3 className="connect-match__section-title">Disputes</h3>

      {myPendingDisputes.map((dispute) => (
        <p key={dispute.disputeId} className="connect-match__status" role="status">
          Waiting for your opponent to review your dispute on step {dispute.position}: you claimed{' '}
          {dispute.claimedClubName}.
        </p>
      ))}

      {reviewableDisputes.map((dispute) => (
        <div key={dispute.disputeId} className="connect-match__dispute-card">
          <p className="connect-match__status">
            Your opponent disputed step {dispute.position}, claiming they played together at{' '}
            <strong>{dispute.claimedClubName}</strong>.
          </p>
          <div className="connect-match__dispute-actions">
            <button
              type="button"
              className="connect-match__button"
              disabled={actioningId === dispute.disputeId}
              onClick={() => handleReview(dispute.disputeId, true)}
            >
              {actioningId === dispute.disputeId ? 'Reviewing…' : 'Approve'}
            </button>
            <button
              type="button"
              className="connect-match__button connect-match__button--secondary"
              disabled={actioningId === dispute.disputeId}
              onClick={() => handleReview(dispute.disputeId, false)}
            >
              {actioningId === dispute.disputeId ? 'Reviewing…' : 'Deny'}
            </button>
          </div>
        </div>
      ))}

      {reviewError && (
        <p className="connect-match__error" role="alert">
          {reviewError}
        </p>
      )}
    </section>
  );
}

import type { ConnectMatchDetail } from '../lib/types';
import { ChainStepsList } from './ChainStepsList';

export interface MatchResolutionProps {
  detail: ConnectMatchDetail;
}

function outcomeHeading(outcome: string): string {
  switch (outcome) {
    case 'Win':
      return 'You won!';
    case 'Loss':
      return 'You lost.';
    case 'Draw':
      return "It's a draw.";
    default:
      return 'Match resolved.';
  }
}

function scoreText(score: number | null): string {
  // REQ-1408: a null score means a forfeit (bust or timeout) — shown
  // plainly as "Forfeited," never as "0," since 0 is a real (if
  // impossible-in-practice) score value and would misread as "a perfect
  // score" rather than "no valid score."
  return score === null ? 'Forfeited — no valid score' : String(score);
}

// REQ-1408/1409 (design-document.md SCREEN-16's "Resolved phase"): outcome,
// both scores (translated to the caller's own perspective server-side),
// and — for context, since it costs little once ChainStepsList already
// exists for ChainBuilder — the caller's own completed chain.
export function MatchResolution({ detail }: MatchResolutionProps) {
  return (
    <section className="connect-match__section">
      <h3 className="connect-match__section-title">{outcomeHeading(detail.outcome)}</h3>
      {/* S-218 bugfix: when the viewer's OWN closing chain-step is also the
          one that completes match resolution (their opponent had already
          reached a terminal state first), ConnectChainStepService resolves
          the match inline in that same request — MatchScreen.tsx swaps
          ChainBuilder straight out for this component, so a player never
          sees ChainBuilder's own "Connected! Your chain is complete."
          feedback at all in that case. Rather than trying to flash that
          message for one frame before an immediate unmount, this
          acknowledgment is folded into the screen the player actually
          lands on — correct UX is "you completed your chain, and here's
          the final result" as one screen, not two. Shown whenever the
          viewer's own chain reached a genuine completion (never for a bust
          or timeout, which report through the score/outcome text instead),
          not only for the specific race above — the statement is equally
          true for a player who completed their chain earlier and is only
          now seeing the resolution after their opponent finished. */}
      {detail.myTerminalState.completed && (
        <p className="connect-match__success" role="status">
          Connected! Your chain is complete.
        </p>
      )}
      <dl className="connect-match__score-list">
        <div className="connect-match__score-row">
          <dt>Your score</dt>
          <dd className="mono-figure">{scoreText(detail.myScore)}</dd>
        </div>
        <div className="connect-match__score-row">
          <dt>Opponent score</dt>
          <dd className="mono-figure">{scoreText(detail.opponentScore)}</dd>
        </div>
      </dl>
      {detail.resolvedAt && (
        <p className="connect-match__description">Resolved {new Date(detail.resolvedAt).toLocaleString()}.</p>
      )}
      {detail.myTargetPick && detail.opponentTargetPick && (
        <>
          <h4 className="connect-match__section-title">Your chain</h4>
          <ChainStepsList
            targetPlayerName={detail.myTargetPick.targetPlayerName}
            otherTargetPlayerName={detail.opponentTargetPick.targetPlayerName}
            steps={detail.myChainSteps}
          />
        </>
      )}
    </section>
  );
}

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

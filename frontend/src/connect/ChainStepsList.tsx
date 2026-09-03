import type { ConnectChainStepView } from '../lib/types';

export interface ChainStepsListProps {
  targetPlayerName: string;
  steps: ConnectChainStepView[];
  otherTargetPlayerName: string;
}

// S-218 (design-document.md SCREEN-16): the shared "your chain so far"
// render, used by both ChainBuilder (mid-match) and MatchResolution
// (post-match, for context). Deliberately shows only the VALID steps, in
// position order — `steps` (ConnectMatchDetail.myChainSteps) also includes
// failed attempts (REQ-1407), but this list renders the actual built chain,
// not an attempt history; a failed attempt's own inline feedback is shown
// separately, at submission time, by whichever caller is still accepting
// submissions (ChainBuilder only — MatchResolution never has a form to
// attach that feedback to).
export function ChainStepsList({ targetPlayerName, steps, otherTargetPlayerName }: ChainStepsListProps) {
  const validSteps = steps.filter((step) => step.isValid).sort((a, b) => a.position - b.position);

  return (
    <ol className="connect-match__chain">
      <li className="connect-match__chain-item connect-match__chain-item--target">{targetPlayerName}</li>
      {validSteps.map((step) => (
        <li key={step.position} className="connect-match__chain-item">
          {step.candidatePlayerName}
          <span className="connect-match__chain-club"> ({step.claimedClubName})</span>
          {step.closesChain && <span className="connect-match__chain-closes"> — connects to your target</span>}
        </li>
      ))}
      {validSteps.some((step) => step.closesChain) ? (
        <li className="connect-match__chain-item connect-match__chain-item--target">{otherTargetPlayerName}</li>
      ) : (
        <li className="connect-match__chain-item connect-match__chain-item--pending">{otherTargetPlayerName} (not yet connected)</li>
      )}
    </ol>
  );
}

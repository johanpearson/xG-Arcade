import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { MatchResolution } from './MatchResolution';
import type { ConnectMatchDetail } from '../lib/types';

function detail(overrides: Partial<ConnectMatchDetail> = {}): ConnectMatchDetail {
  return {
    status: 'Resolved',
    createdAt: '2026-09-01T00:00:00Z',
    startedAt: '2026-09-01T01:00:00Z',
    deadlineUtc: '2026-09-01T07:00:00Z',
    resolvedAt: '2026-09-01T05:00:00Z',
    outcome: 'Win',
    opponentUserId: 'b2c3d4e5-0000-0000-0000-000000000000',
    myTargetPick: { targetPlayerId: 't1', targetPlayerName: 'Lionel Messi', locked: true },
    opponentTargetPick: { targetPlayerId: 't2', targetPlayerName: 'Cristiano Ronaldo', locked: true },
    myChainSteps: [],
    myTerminalState: { busted: false, timedOut: false, completed: true },
    opponentTerminalState: { busted: true, timedOut: false, completed: false },
    myScore: 2,
    opponentScore: null,
    opponentChainSteps: null,
    ...overrides,
  };
}

// REQ-1408/1409 (design-document.md SCREEN-16's "Resolved phase").
describe('MatchResolution', () => {
  it('REQ-1409: shows "You won!" for a Win outcome, with both scores', () => {
    render(<MatchResolution detail={detail()} />);

    expect(screen.getByText('You won!')).toBeInTheDocument();
    expect(screen.getByText('2')).toBeInTheDocument();
    expect(screen.getByText('Forfeited — no valid score')).toBeInTheDocument();
  });

  it('REQ-1409: shows "You lost." for a Loss outcome', () => {
    render(<MatchResolution detail={detail({ outcome: 'Loss', myScore: null, opponentScore: 1 })} />);

    expect(screen.getByText('You lost.')).toBeInTheDocument();
  });

  it('REQ-1409: shows "It\'s a draw." for a Draw outcome', () => {
    render(<MatchResolution detail={detail({ outcome: 'Draw', myScore: null, opponentScore: null })} />);

    expect(screen.getByText("It's a draw.")).toBeInTheDocument();
    // REQ-1408: a null score is never rendered as "0" — that would misread
    // as a real, perfect score rather than "no valid score."
    expect(screen.queryByText('0')).not.toBeInTheDocument();
  });

  it('S-218 bugfix: acknowledges the viewer\'s own completed chain on the resolution screen', () => {
    // Real production bug (not test-only): when the viewer's OWN closing
    // chain-step is also the submission that completes match resolution
    // (their opponent had already reached a terminal state), the backend
    // resolves inline in that same request and MatchScreen.tsx swaps
    // ChainBuilder straight out for this component — ChainBuilder's own
    // "Connected! Your chain is complete." local-state feedback never gets
    // a chance to render. This proves the fold-in fix: MatchResolution
    // itself carries the acknowledgment, derived from `myTerminalState`
    // (part of the very same resolved-match payload), so it survives that
    // unmount rather than depending on anything ChainBuilder ever rendered.
    render(<MatchResolution detail={detail({ myTerminalState: { busted: false, timedOut: false, completed: true } })} />);

    expect(screen.getByText('Connected! Your chain is complete.')).toBeInTheDocument();
  });

  it('S-218 bugfix: does not show the chain-complete acknowledgment for a forfeiting player (bust/timeout)', () => {
    render(
      <MatchResolution
        detail={detail({
          outcome: 'Loss',
          myScore: null,
          myTerminalState: { busted: true, timedOut: false, completed: false },
        })}
      />,
    );

    expect(screen.queryByText('Connected! Your chain is complete.')).not.toBeInTheDocument();
  });

  it('REQ-1408: shows the caller\'s own completed chain for context', () => {
    render(
      <MatchResolution
        detail={detail({
          myChainSteps: [
            {
              position: 1,
              attemptNumber: 1,
              candidatePlayerId: 'p1',
              candidatePlayerName: 'Bridge Player',
              matchedClubName: 'Some Club',
              matchedOverlapStartYear: 2010,
              matchedOverlapEndYear: 2015,
              isValid: true,
              closesChain: true,
              closingClubName: 'Some Other Club',
              closingOverlapStartYear: 2016,
              closingOverlapEndYear: 2018,
              submittedAt: '2026-09-01T02:00:00Z',
            },
          ],
        })}
      />,
    );

    expect(screen.getByText('Your chain')).toBeInTheDocument();
    expect(screen.getByText(/Bridge Player/)).toBeInTheDocument();
  });

  // REQ-1418: opponent's completed chain becomes visible once the match is
  // Resolved.
  it('REQ-1418: renders the opponent\'s completed chain once opponentChainSteps is present', () => {
    render(
      <MatchResolution
        detail={detail({
          opponentChainSteps: [
            {
              chainStepId: 'step-1',
              position: 1,
              attemptNumber: 1,
              candidatePlayerId: 'p2',
              candidatePlayerName: 'Opponent Bridge Player',
              matchedClubName: 'Another Club',
              matchedOverlapStartYear: 2011,
              matchedOverlapEndYear: 2016,
              isValid: true,
              closesChain: true,
              closingClubName: 'Closing Club',
              closingOverlapStartYear: 2017,
              closingOverlapEndYear: null,
              submittedAt: '2026-09-01T02:30:00Z',
            },
          ],
        })}
      />,
    );

    expect(screen.getByText('Opponent’s chain')).toBeInTheDocument();
    expect(screen.getByText(/Opponent Bridge Player/)).toBeInTheDocument();
    // REQ-1418: the opponent's chain runs in the opposite direction from
    // "Your chain" — it starts at their own target pick (Ronaldo) and closes
    // by reaching the caller's target pick (Messi), so both target names
    // still appear, just in the reversed order.
    expect(screen.getAllByText(/Cristiano Ronaldo/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/Lionel Messi/).length).toBeGreaterThan(0);
  });

  it('REQ-1418: does not render an opponent-chain section when opponentChainSteps is null', () => {
    render(<MatchResolution detail={detail({ opponentChainSteps: null })} />);

    expect(screen.queryByText('Opponent’s chain')).not.toBeInTheDocument();
  });

  it('REQ-1418: renders an empty opponent chain plainly (a forfeit before any step) without fabricating steps', () => {
    render(<MatchResolution detail={detail({ myChainSteps: [], opponentChainSteps: [] })} />);

    const heading = screen.getByText('Opponent’s chain');
    expect(heading).toBeInTheDocument();
    // Both "Your chain" (also empty by default) and "Opponent's chain" show
    // a "not yet connected" placeholder rather than a fabricated step — two
    // occurrences confirms the opponent section renders its own list, not a
    // duplicate/empty render.
    expect(screen.getAllByText(/not yet connected/)).toHaveLength(2);
  });
});

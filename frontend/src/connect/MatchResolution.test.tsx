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
              claimedClubName: 'Some Club',
              isValid: true,
              closesChain: true,
              submittedAt: '2026-09-01T02:00:00Z',
            },
          ],
        })}
      />,
    );

    expect(screen.getByText('Your chain')).toBeInTheDocument();
    expect(screen.getByText(/Bridge Player/)).toBeInTheDocument();
  });
});

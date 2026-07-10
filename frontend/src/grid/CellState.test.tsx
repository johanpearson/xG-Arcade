import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { CellState } from './CellState';

// SCREEN-01a / REQ-210: four distinct "attempted" cell states. Constructed
// via props directly — state 4 (round closed) is not reachable through the
// live API yet (GET /rounds/current only returns an Active round, S-011
// scope), so it's exercised here rather than through a real fetch flow.
describe('CellState', () => {
  it('REQ-210 state 1: correct + round active shows a live label and no fabricated uniqueness data', () => {
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
      />,
    );

    expect(screen.getByText('Henry')).toBeInTheDocument();
    expect(screen.getByText('live')).toBeInTheDocument();
    expect(screen.queryByText(/unique/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/pts/i)).not.toBeInTheDocument();
  });

  it('REQ-210 state 2: incorrect with one attempt remaining spells out the count as text', () => {
    render(
      <CellState
        playerName="Ronaldinho"
        isCorrect={false}
        attemptCount={1}
        locked={false}
        roundStatus="active"
      />,
    );

    expect(screen.getByText('1 attempt left')).toBeInTheDocument();
  });

  it('REQ-210 state 3: incorrect with no attempts left is locked and says so in text, no fabricated points', () => {
    render(
      <CellState
        playerName="Ronaldinho"
        isCorrect={false}
        attemptCount={2}
        locked
        roundStatus="active"
      />,
    );

    expect(screen.getByText('no attempts left')).toBeInTheDocument();
    expect(screen.queryByText(/\d+\s*pts/i)).not.toBeInTheDocument();
  });

  it('REQ-210 state 4: round closed shows "final" text and no live dot, for either prior outcome', () => {
    const { rerender } = render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="closed" />,
    );

    expect(screen.getByText('final')).toBeInTheDocument();
    expect(document.querySelector('.cell-state__live-dot')).not.toBeInTheDocument();

    rerender(
      <CellState
        playerName="Ronaldinho"
        isCorrect={false}
        attemptCount={2}
        locked
        roundStatus="closed"
      />,
    );

    expect(screen.getByText('final')).toBeInTheDocument();
  });

  it('falls back to a non-fabricated label when no player name is known client-side', () => {
    render(
      <CellState isCorrect attemptCount={1} locked roundStatus="active" />,
    );

    expect(screen.getByText('Guess submitted')).toBeInTheDocument();
  });
});

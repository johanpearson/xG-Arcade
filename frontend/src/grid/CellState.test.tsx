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

  it('REQ-204: correct + round active with a live uniqueness value shows it in mono numerals plus "updates until" copy', () => {
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        uniquePercent={0.12}
        roundEndTime="2026-07-11T18:00:00Z"
      />,
    );

    expect(screen.getByText('live')).toBeInTheDocument();
    expect(screen.getByText('12% unique')).toBeInTheDocument();
    expect(screen.getByText(/updates until round closes on/)).toBeInTheDocument();
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

  it('REQ-205/206: round closed with a locked score shows "X% unique · Y pts" alongside "final"', () => {
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="closed"
        uniquePercent={0.12}
        finalPoints={88}
      />,
    );

    expect(screen.getByText('12% unique · 88 pts')).toBeInTheDocument();
    expect(screen.getByText('final')).toBeInTheDocument();
  });

  it('REQ-210: falls back to a non-fabricated label when no player name is known client-side', () => {
    render(
      <CellState isCorrect attemptCount={1} locked roundStatus="active" />,
    );

    expect(screen.getByText('Guess submitted')).toBeInTheDocument();
  });
});

// S-015 (SCREEN-01a / design-document.md §2's "signature element: badge
// dock"): the reveal animation is only ever triggered by a *transition*
// observed while the component stays mounted (guess-submit, or round-close
// while already correct) — never by directly mounting already in a correct
// state (e.g. a page reload), and never for anything other than the two
// "correct" states. useRevealToken is exercised here indirectly through
// CellState's own rendered className/markup, the same black-box approach
// the rest of this file already uses, rather than reaching into the hook.
describe('CellState badge-dock reveal (S-015)', () => {
  it('S-015: a guess-submit transition (incorrect+active -> correct+active) renders the docked badges and applies cell-state--reveal', () => {
    const { container, rerender } = render(
      <CellState
        playerName="Henry"
        isCorrect={false}
        attemptCount={1}
        locked={false}
        roundStatus="active"
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
      />,
    );

    expect(screen.queryByTestId('badge-dock-row')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).not.toBeInTheDocument();

    rerender(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
      />,
    );

    expect(screen.getByTestId('badge-dock-row')).toBeInTheDocument();
    expect(screen.getByTestId('badge-dock-col')).toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).toBeInTheDocument();
  });

  it('S-015: mounting directly already correct+active (no prior incorrect render, e.g. a page reload) shows the badges already docked with no reveal class', () => {
    const { container } = render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
      />,
    );

    expect(screen.getByTestId('badge-dock-row')).toBeInTheDocument();
    expect(screen.getByTestId('badge-dock-col')).toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).not.toBeInTheDocument();
  });

  it('S-015: a round-close transition while already correct (active -> closed) also applies cell-state--reveal', () => {
    const { container, rerender } = render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
      />,
    );

    expect(container.querySelector('.cell-state--reveal')).not.toBeInTheDocument();

    rerender(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="closed"
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
      />,
    );

    expect(screen.getByTestId('badge-dock-row')).toBeInTheDocument();
    expect(screen.getByTestId('badge-dock-col')).toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).toBeInTheDocument();
  });

  it('S-015: both reveals happening in one mounted lifetime (guess-submit, then later round-close) each restart the reveal — not just the first', () => {
    // This is the scenario the key={revealToken} remount (CellState.tsx)
    // exists for: cell-state--reveal, once baked into a className string by
    // the first transition, would otherwise stay present unchanged through
    // the live->closed branch switch and never visibly "restart" for the
    // second transition. Asserting the badge-dock node itself is replaced
    // (not just that the reveal class is present, which would pass even if
    // the animation never actually restarted) is what actually verifies the
    // remount happened.
    const { container, rerender } = render(
      <CellState
        playerName="Henry"
        isCorrect={false}
        attemptCount={1}
        locked={false}
        roundStatus="active"
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
      />,
    );

    // Reveal 1: guess-submit.
    rerender(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
      />,
    );
    expect(container.querySelector('.cell-state--reveal')).toBeInTheDocument();
    const badgeNodeAfterFirstReveal = screen.getByTestId('badge-dock-row');

    // Reveal 2: round-close, on the same mounted instance.
    rerender(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="closed"
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
      />,
    );
    expect(container.querySelector('.cell-state--reveal')).toBeInTheDocument();
    expect(screen.getByTestId('badge-dock-row')).not.toBe(badgeNodeAfterFirstReveal);
  });

  it('S-015: partial category props (e.g. missing colCategoryValue) render no badge dock at all, not a half-rendered one', () => {
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        // colCategoryValue intentionally omitted
      />,
    );

    expect(screen.queryByTestId('badge-dock-row')).not.toBeInTheDocument();
    expect(screen.queryByTestId('badge-dock-col')).not.toBeInTheDocument();
  });

  it('S-015: mounting directly already correct+closed (no prior active render, e.g. a page reload after the round closed) shows the badges already docked with no reveal class', () => {
    const { container } = render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="closed"
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
      />,
    );

    expect(screen.getByTestId('badge-dock-row')).toBeInTheDocument();
    expect(screen.getByTestId('badge-dock-col')).toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).not.toBeInTheDocument();
  });

  it('S-015: omitting the row/col category props entirely renders no badge-dock spans at all, even on a correct-guess transition (backward-compat)', () => {
    const { rerender } = render(
      <CellState playerName="Henry" isCorrect={false} attemptCount={1} locked={false} roundStatus="active" />,
    );

    rerender(<CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" />);

    expect(screen.queryByTestId('badge-dock-row')).not.toBeInTheDocument();
    expect(screen.queryByTestId('badge-dock-col')).not.toBeInTheDocument();
  });

  it('S-015: an incorrect-state re-render (e.g. attempt count increasing, still incorrect) never applies cell-state--reveal or renders badge-dock spans', () => {
    const { container, rerender } = render(
      <CellState
        playerName="Ronaldinho"
        isCorrect={false}
        attemptCount={1}
        locked={false}
        roundStatus="active"
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
      />,
    );

    rerender(
      <CellState
        playerName="Ronaldinho"
        isCorrect={false}
        attemptCount={2}
        locked
        roundStatus="active"
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
      />,
    );

    expect(screen.queryByTestId('badge-dock-row')).not.toBeInTheDocument();
    expect(screen.queryByTestId('badge-dock-col')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).not.toBeInTheDocument();
  });
});

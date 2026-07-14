import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { CellState } from './CellState';

// SCREEN-01a / REQ-210: four distinct "attempted" cell states. Constructed
// via props directly — state 4 (round closed) is not reachable through the
// live API yet (GET /rounds/current only returns an Active round, S-011
// scope), so it's exercised here rather than through a real fetch flow.
//
// S-041 (2026-07-14 redesign, REQ-204/212): states 1 and 4 are now a single
// rendering branch — at rest, both show only a checkmark plus a points value
// (state 1's live estimate, state 4's locked FinalPoints), never a percent,
// never a dot, never "live"/"final"/"~"/"estimated" wording, and no player
// name/badge dock until the caller-owned `revealed` prop is true (REQ-212 —
// CellState no longer owns the reveal toggle itself, GridCell does).
describe('CellState', () => {
  it('REQ-210 state 1: correct + round active shows a checkmark and the live points value, nothing else', () => {
    render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" livePoints={12} />,
    );

    expect(screen.getByText('✓')).toBeInTheDocument();
    expect(screen.getByText('12 pts')).toBeInTheDocument();
    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
    expect(screen.queryByText(/live/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/final/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/unique|%/i)).not.toBeInTheDocument();
    expect(document.querySelector('.cell-state__live-dot')).not.toBeInTheDocument();
  });

  it('REQ-210 state 1: with no livePoints known yet (guess just submitted, live values not back yet) shows the checkmark with no points line at all, never a fabricated value', () => {
    render(<CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" />);

    expect(screen.getByText('✓')).toBeInTheDocument();
    expect(screen.queryByText(/pts/i)).not.toBeInTheDocument();
  });

  it('REQ-210 state 2: incorrect with one attempt remaining spells out the count as text', () => {
    render(
      <CellState playerName="Ronaldinho" isCorrect={false} attemptCount={1} locked={false} roundStatus="active" />,
    );

    expect(screen.getByText('1 attempt left')).toBeInTheDocument();
    expect(screen.getByText('✕')).toBeInTheDocument();
  });

  // S-033 (REQ-204): a locked-incorrect cell is guaranteed to lock at
  // MaxPointsPerCell (ADR-0021's golf-scoring worst case, not 0) — a known
  // constant, not a live computation, so it's shown immediately rather than
  // waiting on REQ-205's actual round-close lock.
  it('REQ-210 state 3: incorrect with no attempts left is locked, says so in text, and shows the guaranteed MaxPointsPerCell value', () => {
    render(<CellState playerName="Ronaldinho" isCorrect={false} attemptCount={2} locked roundStatus="active" />);

    expect(screen.getByText('no attempts left')).toBeInTheDocument();
    expect(screen.getByText('100 pts')).toBeInTheDocument();
    expect(screen.queryByText('Ronaldinho')).not.toBeInTheDocument();
  });

  it('REQ-210 state 4: round closed, correct outcome, shows only a checkmark and the locked FinalPoints — no "final" wording on a correct cell', () => {
    render(<CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="closed" finalPoints={88} />);

    expect(screen.getByText('✓')).toBeInTheDocument();
    expect(screen.getByText('88 pts')).toBeInTheDocument();
    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
    expect(screen.queryByText(/final/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/live/i)).not.toBeInTheDocument();
  });

  it('REQ-210 state 4: round closed, incorrect outcome, is locked and says "final" in text, no fabricated points', () => {
    render(<CellState playerName="Ronaldinho" isCorrect={false} attemptCount={2} locked roundStatus="closed" />);

    expect(screen.getByText('final')).toBeInTheDocument();
    expect(screen.getByText('✕')).toBeInTheDocument();
    expect(screen.queryByText(/\d+\s*pts/i)).not.toBeInTheDocument();
  });

  it('REQ-204: state 1 and state 4 render identically in structure at rest given equivalent points — checkmark + points, no live indicator of any kind, no percent', () => {
    const { container: liveContainer } = render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" livePoints={40} />,
    );
    const { container: closedContainer } = render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="closed" finalPoints={40} />,
    );

    // Same structural shape: one icon, one points line, no dot, no percent,
    // no live/final qualifier — a player cannot tell, from either markup,
    // whether the shown value is still live or already locked.
    for (const container of [liveContainer, closedContainer]) {
      expect(container.querySelector('.cell-state__icon--correct')).toBeInTheDocument();
      expect(container.querySelector('.cell-state__live-dot')).not.toBeInTheDocument();
      expect(container.textContent).not.toMatch(/live|final|~|estimated|%/i);
    }

    expect(liveContainer.querySelector('.cell-state__meta')?.textContent).toBe(
      closedContainer.querySelector('.cell-state__meta')?.textContent,
    );
  });

  it('REQ-210: falls back to a non-fabricated label when no player name is known client-side, once revealed', () => {
    render(<CellState isCorrect attemptCount={1} locked roundStatus="active" revealed />);

    expect(screen.getByText('Guess submitted')).toBeInTheDocument();
  });
});

// S-015 (design-document.md §2's "signature element: badge dock"), rewritten
// for S-041/REQ-212: the reveal animation is now driven by the caller-owned
// `revealed` prop transitioning false -> true (a click/tap on the whole
// cell, owned by GridCell) — no longer by a guess-correct or round-close
// transition, since CellState no longer knows or cares how/when the guess
// became correct, only whether the caller currently wants it shown.
// useRevealToken is exercised here indirectly through CellState's own
// rendered className/markup, the same black-box approach the rest of this
// file already uses, rather than reaching into the hook.
describe('CellState badge-dock reveal (S-015, re-keyed to the revealed prop by S-041)', () => {
  it('REQ-212: revealed=false at rest shows no name and no badge dock, and applies no cell-state--reveal class', () => {
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
        revealed={false}
      />,
    );

    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
    expect(screen.queryByTestId('badge-dock-row')).not.toBeInTheDocument();
    expect(screen.queryByTestId('badge-dock-col')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).not.toBeInTheDocument();
  });

  it('REQ-212: a revealed prop transition (false -> true) renders the docked badges/name and applies cell-state--reveal', () => {
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
        revealed={false}
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
        revealed
      />,
    );

    expect(screen.getByText('Henry')).toBeInTheDocument();
    expect(screen.getByTestId('badge-dock-row')).toBeInTheDocument();
    expect(screen.getByTestId('badge-dock-col')).toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).toBeInTheDocument();
  });

  it('REQ-212: a revealed prop transition back (true -> false) hides the name/badges again and drops cell-state--reveal', () => {
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
        revealed
      />,
    );

    expect(screen.getByText('Henry')).toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).toBeInTheDocument();

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
        revealed={false}
      />,
    );

    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
    expect(screen.queryByTestId('badge-dock-row')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).not.toBeInTheDocument();
  });

  it('S-015: mounting directly already revealed=true (no prior false render) shows the badges already docked with cell-state--reveal applied — the class mirrors the current revealed prop directly, not just a transition; revealToken (which drives the remount-to-restart-animation behavior) only increments on an observed false -> true transition, but does not gate the class itself', () => {
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
        revealed
      />,
    );

    expect(screen.getByTestId('badge-dock-row')).toBeInTheDocument();
    expect(screen.getByTestId('badge-dock-col')).toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).toBeInTheDocument();
  });

  it('S-015: two reveal cycles in one mounted lifetime (false -> true, then false -> true again) each restart the animation via a real remount, not just a class toggle', () => {
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
        revealed={false}
      />,
    );

    // Reveal 1.
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
        revealed
      />,
    );
    expect(container.querySelector('.cell-state--reveal')).toBeInTheDocument();
    const badgeNodeAfterFirstReveal = screen.getByTestId('badge-dock-row');

    // Hide.
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
        revealed={false}
      />,
    );
    expect(container.querySelector('.cell-state--reveal')).not.toBeInTheDocument();

    // Reveal 2, same mounted instance.
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
        revealed
      />,
    );
    expect(container.querySelector('.cell-state--reveal')).toBeInTheDocument();
    expect(screen.getByTestId('badge-dock-row')).not.toBe(badgeNodeAfterFirstReveal);
  });

  it('S-015: partial category props (e.g. missing colCategoryValue) render no badge dock at all when revealed, not a half-rendered one', () => {
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
        revealed
      />,
    );

    expect(screen.queryByTestId('badge-dock-row')).not.toBeInTheDocument();
    expect(screen.queryByTestId('badge-dock-col')).not.toBeInTheDocument();
  });

  it('S-015: omitting the row/col category props entirely renders no badge-dock spans at all, even revealed (backward-compat)', () => {
    render(<CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" revealed />);

    expect(screen.queryByTestId('badge-dock-row')).not.toBeInTheDocument();
    expect(screen.queryByTestId('badge-dock-col')).not.toBeInTheDocument();
  });

  it('REQ-212: a revealed=true prop on an incorrect (state 2/3) cell has no effect — no name/badge dock is ever shown for a wrong guess', () => {
    const { container } = render(
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
        revealed
      />,
    );

    expect(screen.queryByText('Ronaldinho')).not.toBeInTheDocument();
    expect(screen.queryByTestId('badge-dock-row')).not.toBeInTheDocument();
    expect(screen.queryByTestId('badge-dock-col')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).not.toBeInTheDocument();
  });

  it('REQ-212: state 4 (round closed, correct) also honors the revealed prop the same way state 1 does', () => {
    const { container, rerender } = render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="closed"
        finalPoints={88}
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
        revealed={false}
      />,
    );

    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).not.toBeInTheDocument();

    rerender(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="closed"
        finalPoints={88}
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
        revealed
      />,
    );

    expect(screen.getByText('Henry')).toBeInTheDocument();
    expect(screen.getByTestId('badge-dock-row')).toBeInTheDocument();
    expect(screen.getByTestId('badge-dock-col')).toBeInTheDocument();
    expect(container.querySelector('.cell-state--reveal')).toBeInTheDocument();
  });
});

// S-020 (design-document.md §2's "Rejected-guess cue"): a shake + red flash
// on a rejected guess, mechanically and visually distinct from S-015's
// badge-dock reveal above — different trigger (a rejection, not a match),
// never sharing a class or keyframe with the reveal. Unaffected by S-041:
// still driven by attemptCount/isCorrect transitions, not the `revealed`
// prop. Same "transition, not mount" rule as useRevealToken in spirit:
// useShakeToken fires while the component stays mounted and attemptCount
// increases with isCorrect still false, and also — via the
// submittedThisSession prop — on a first mount that is itself a fresh
// rejection (a cell's first-ever guess this session, which GridCell mounts
// CellState directly into rather than transitioning into from an
// already-mounted render). It never fires on a first mount of a guess
// loaded from a page reload (submittedThisSession false/omitted). Exercised
// the same black-box way as S-015 — via CellState's rendered className/
// markup, not by reaching into the hook directly.
describe('CellState shake-and-flash reveal (S-020)', () => {
  it('S-020: a rejection transition (attemptCount increasing while still incorrect, at least one attempt remaining) applies cell-state--shake and remounts the DOM node, not just toggles a class', () => {
    const { container, rerender } = render(
      <CellState playerName="Ronaldinho" isCorrect={false} attemptCount={1} locked={false} roundStatus="active" />,
    );

    expect(container.querySelector('.cell-state--shake')).not.toBeInTheDocument();
    const nodeBeforeRejection = container.querySelector('.cell-state');

    rerender(
      <CellState playerName="Ronaldinho" isCorrect={false} attemptCount={2} locked={false} roundStatus="active" />,
    );

    expect(container.querySelector('.cell-state--shake')).toBeInTheDocument();
    // Node identity must actually change (a real remount), not just gain a
    // class — the same check S-015's remount test uses via node reference
    // comparison, since key={shakeToken} is what restarts the CSS animation
    // on every rejection.
    expect(container.querySelector('.cell-state')).not.toBe(nodeBeforeRejection);
  });

  it('S-020: a rejection transition into state 3 (no attempts remaining) also applies cell-state--shake on the locked branch', () => {
    const { container, rerender } = render(
      <CellState playerName="Ronaldinho" isCorrect={false} attemptCount={1} locked={false} roundStatus="active" />,
    );

    expect(container.querySelector('.cell-state--shake')).not.toBeInTheDocument();

    rerender(<CellState playerName="Ronaldinho" isCorrect={false} attemptCount={2} locked roundStatus="active" />);

    expect(screen.getByText('no attempts left')).toBeInTheDocument();
    expect(container.querySelector('.cell-state--locked')).toBeInTheDocument();
    expect(container.querySelector('.cell-state--shake')).toBeInTheDocument();
  });

  it('S-020: mounting directly already incorrect (e.g. a page reload, no prior render) shows no cell-state--shake class', () => {
    const { container } = render(
      <CellState playerName="Ronaldinho" isCorrect={false} attemptCount={1} locked={false} roundStatus="active" />,
    );

    expect(container.querySelector('.cell-state--shake')).not.toBeInTheDocument();
  });

  it('S-020: a correct-guess transition (incorrect -> correct) never applies cell-state--shake — that is the badge-dock reveal\'s territory, not this one', () => {
    const { container, rerender } = render(
      <CellState playerName="Henry" isCorrect={false} attemptCount={1} locked={false} roundStatus="active" />,
    );

    rerender(<CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" />);

    expect(container.querySelector('.cell-state--shake')).not.toBeInTheDocument();
  });

  it('S-020: attemptCount increasing while already correct (a locked, correct cell re-rendering with a bumped attempt count) does not apply cell-state--shake', () => {
    const { container, rerender } = render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" />,
    );

    rerender(<CellState playerName="Henry" isCorrect attemptCount={2} locked roundStatus="active" />);

    expect(container.querySelector('.cell-state--shake')).not.toBeInTheDocument();
  });

  it('S-020: a cell\'s first-ever guess this session (submittedThisSession, mounting directly incorrect) still applies cell-state--shake, unlike an ordinary page-reload mount', () => {
    const { container } = render(
      <CellState
        playerName="Definitely Not A Real Player"
        isCorrect={false}
        attemptCount={1}
        locked={false}
        roundStatus="active"
        submittedThisSession
      />,
    );

    expect(container.querySelector('.cell-state--shake')).toBeInTheDocument();
  });

  it('S-020: submittedThisSession on a first mount that is already correct does not apply cell-state--shake (only a rejection seeds it)', () => {
    const { container } = render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" submittedThisSession />,
    );

    expect(container.querySelector('.cell-state--shake')).not.toBeInTheDocument();
  });
});

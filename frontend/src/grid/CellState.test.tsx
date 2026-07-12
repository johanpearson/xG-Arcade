import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { CellState } from './CellState';

// Note on why most reveal-toggle assertions below use `fireEvent.click`/
// `fireEvent.focus`/etc. rather than `@testing-library/user-event`: those
// isolate one trigger at a time (a bare click with no accompanying focus
// event, a bare focus with no click, etc.), which is what each test is
// actually asserting about. A realistic click fires a native `focus` event
// immediately before its `click` event — `LiveMetaDisclosure` deliberately
// tracks click/hover/focus as three independent flags (OR'd together)
// specifically so that combination can never fight over one shared boolean
// (an earlier single-toggle version of this component had exactly that bug:
// a real click would open the panel via focus, then immediately re-close it
// via the click's own toggle, in the same gesture). The dedicated
// `userEvent.click` test below is what exercises that realistic combined
// sequence end-to-end, rather than one isolated event at a time.

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

    // S-019: the live meta text is disclosed on demand, not always shown —
    // reveal it first via the "live" toggle before asserting its contents.
    fireEvent.click(screen.getByRole('button', { name: /live/i }));

    expect(screen.getByText('live')).toBeInTheDocument();
    expect(screen.getByText('88% of others guessed this too')).toBeInTheDocument();
    expect(screen.getByText(/updates until round closes on/)).toBeInTheDocument();
  });

  it('REQ-204 (S-018): correct + round active with a live uniqueness value and a live point estimate shows "X% unique · ~N pts estimated"', () => {
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        uniquePercent={0.12}
        livePoints={12}
        roundEndTime="2026-07-11T18:00:00Z"
      />,
    );

    // S-019: reveal the disclosure before asserting its text is present.
    fireEvent.click(screen.getByRole('button', { name: /live/i }));

    expect(screen.getByText('live')).toBeInTheDocument();
    expect(screen.getByText('88% of others guessed this too · ~12 pts estimated')).toBeInTheDocument();
  });

  it('REQ-204 (S-018): the live point estimate is visually and textually distinct from a round-closed locked score', () => {
    const { container: liveContainer } = render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        uniquePercent={0.12}
        livePoints={12}
      />,
    );
    // S-019: the live half of this comparison is behind the reveal toggle;
    // the closed/final half below never uses the toggle at all (state 4 has
    // no disclosure — see REQ-205/206).
    fireEvent.click(screen.getByRole('button', { name: /live/i }));
    const liveText = screen.getByText('88% of others guessed this too · ~12 pts estimated');
    expect(liveContainer.querySelector('.cell-state--live')).toBeInTheDocument();
    expect(liveContainer.querySelector('.cell-state--final')).not.toBeInTheDocument();

    const { container: closedContainer } = render(
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
    const closedText = screen.getByText('88% of others guessed this too · 88 pts');
    expect(closedContainer.querySelector('.cell-state--final')).toBeInTheDocument();
    expect(closedContainer.querySelector('.cell-state--live')).not.toBeInTheDocument();

    // The live estimate's wording ("~N pts estimated") must never collapse
    // to, or be mistaken for, the locked-score wording ("Y pts") — this is
    // the acceptance criterion, not just an implementation detail.
    expect(liveText.textContent).not.toEqual(closedText.textContent);
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

    expect(screen.getByText('88% of others guessed this too · 88 pts')).toBeInTheDocument();
    expect(screen.getByText('final')).toBeInTheDocument();
  });

  it('REQ-210: falls back to a non-fabricated label when no player name is known client-side', () => {
    render(
      <CellState isCorrect attemptCount={1} locked roundStatus="active" />,
    );

    expect(screen.getByText('Guess submitted')).toBeInTheDocument();
  });
});

// S-019 (REQ-204/SCREEN-01a redesign): state 1's live uniqueness %/points/
// round-end text moved behind an on-demand disclosure (tap/long-press, or
// hover/focus on desktop) instead of always rendering — the permanent
// at-rest indicator (green dot + "live" text) is unchanged. These tests
// cover the acceptance criteria that text is genuinely absent from the DOM
// until revealed, and that the reveal/hide state is exposed accessibly
// (aria-expanded on the toggle, aria-live on the revealed panel) rather
// than only working for a mouse.
describe('CellState live meta disclosure (S-019)', () => {
  it('REQ-204: on initial render the live uniqueness/points/round-end text is not in the document, but the permanent "live" label and dot are', () => {
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        uniquePercent={0.12}
        livePoints={12}
        roundEndTime="2026-07-11T18:00:00Z"
      />,
    );

    // At-rest indicator: always present, unaffected by S-019.
    expect(screen.getByText('live')).toBeInTheDocument();
    expect(document.querySelector('.cell-state__live-dot')).toBeInTheDocument();

    // Disclosed detail: not yet revealed, so genuinely absent from the DOM
    // (not just visually hidden) until the player interacts.
    expect(screen.queryByText(/of others guessed this too/)).not.toBeInTheDocument();
    expect(screen.queryByText(/pts estimated/)).not.toBeInTheDocument();
    expect(screen.queryByText(/updates until round closes on/)).not.toBeInTheDocument();
  });

  it('REQ-204: the reveal toggle has aria-expanded="false" initially, "true" after a click, and toggles back to "false" on a second click', () => {
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        uniquePercent={0.12}
      />,
    );

    const toggle = screen.getByRole('button', { name: /live/i });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');

    fireEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('88% of others guessed this too')).toBeInTheDocument();

    fireEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByText('88% of others guessed this too')).not.toBeInTheDocument();
  });

  it('REQ-204: focusing the toggle reveals the text (keyboard equivalent) and blurring hides it again', () => {
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        uniquePercent={0.12}
      />,
    );

    const toggle = screen.getByRole('button', { name: /live/i });

    // Focus/blur are the keyboard-accessible equivalent of hover — this is
    // the "not mouse/touch-only" half of S-019's acceptance criteria.
    fireEvent.focus(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('88% of others guessed this too')).toBeInTheDocument();

    fireEvent.blur(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByText('88% of others guessed this too')).not.toBeInTheDocument();
  });

  it('REQ-204: hovering reveals the text (desktop equivalent) and moving the mouse away hides it again', () => {
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        uniquePercent={0.12}
      />,
    );

    const toggle = screen.getByRole('button', { name: /live/i });

    fireEvent.mouseEnter(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('88% of others guessed this too')).toBeInTheDocument();

    fireEvent.mouseLeave(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByText('88% of others guessed this too')).not.toBeInTheDocument();
  });

  it('REQ-204: once revealed, the live meta text lives inside an aria-live="polite" region, so screen readers are told about the change', () => {
    const { container } = render(
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

    fireEvent.click(screen.getByRole('button', { name: /live/i }));

    const livePanel = container.querySelector('[aria-live="polite"]');
    expect(livePanel).toBeInTheDocument();
    expect(livePanel).toHaveTextContent('88% of others guessed this too');
    expect(livePanel).toHaveTextContent(/updates until round closes on/);
  });

  it('REQ-204: with uniquePercent null (guess just submitted, live values not back yet) there is no disclosure/toggle at all, just the plain "live" text — guards against the S-019 wrapper appearing with nothing to disclose', () => {
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
      />,
    );

    expect(screen.getByText('live')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /live/i })).not.toBeInTheDocument();
    expect(document.querySelector('[aria-expanded]')).not.toBeInTheDocument();
  });

  it('REQ-204: a single realistic click (which fires focus immediately before click, like a real mouse click on a focusable button) reveals the text and leaves it open, not a flash-open-then-instant-close', async () => {
    const user = userEvent.setup();
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        uniquePercent={0.12}
      />,
    );

    const toggle = screen.getByRole('button', { name: /live/i });
    await user.click(toggle);

    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('88% of others guessed this too')).toBeInTheDocument();
  });

  it('REQ-204: a second realistic click un-toggles the panel immediately, even though a real click leaves the button both hovered and focused', async () => {
    // Regression test: a real click leaves the pointer resting on the
    // button (it never moved) and the button focused. An earlier version of
    // this component let `hovering` alone keep the panel open regardless of
    // `toggledOpen`, so a mouse user's second click had no visible effect —
    // the panel only closed once the mouse physically moved away. Neither
    // leftover (hover or focus) may block a click from closing the panel.
    const user = userEvent.setup();
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        uniquePercent={0.12}
      />,
    );

    const toggle = screen.getByRole('button', { name: /live/i });
    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');

    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByText('88% of others guessed this too')).not.toBeInTheDocument();

    // Hover's own peek behavior must still work afterward: leaving and
    // re-entering reveals it again, so the fix doesn't just permanently
    // disable hover.
    await user.unhover(toggle);
    await user.hover(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
  });

  it('REQ-204: tabbing to the toggle with the keyboard (no mouse involved at all) reveals the text the same as hovering does', async () => {
    const user = userEvent.setup();
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        uniquePercent={0.12}
      />,
    );

    const toggle = screen.getByRole('button', { name: /live/i });
    await user.tab();

    expect(toggle).toHaveFocus();
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
  });

  it('REQ-204: pressing Enter while keyboard-focused closes the panel immediately, the same as a mouse click while hovering', async () => {
    // Regression test for the keyboard analog of the hover-suppression bug:
    // pressing Enter/Space activates the toggle's onClick without blurring
    // it, so `keyboardFocused` stayed true through a keyboard-driven close
    // just like `hovering` did for the mouse case.
    const user = userEvent.setup();
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        uniquePercent={0.12}
      />,
    );

    const toggle = screen.getByRole('button', { name: /live/i });
    await user.tab();
    expect(toggle).toHaveAttribute('aria-expanded', 'true');

    await user.keyboard('{Enter}');
    expect(toggle).toHaveAttribute('aria-expanded', 'true');

    await user.keyboard('{Enter}');
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByText('88% of others guessed this too')).not.toBeInTheDocument();
  });

  it("REQ-204: an odd number of Enter presses leaves the panel open via the click toggle even after tabbing away, the same as a mouse click's persistent state survives the pointer leaving", async () => {
    // `toggledOpen` is deliberately persistent, independent of hover/focus
    // (S-019: "a tap toggles it open/closed" as a standing state, distinct
    // from hover/focus's transient peek) — a mouse click that opens the
    // panel keeps it open after the pointer moves away, and the keyboard
    // equivalent (Enter) must behave the same way after blur, not close
    // just because focus/hover-driven suppression no longer applies.
    const user = userEvent.setup();
    render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        uniquePercent={0.12}
      />,
    );

    const toggle = screen.getByRole('button', { name: /live/i });
    await user.tab();
    await user.keyboard('{Enter}');
    expect(toggle).toHaveAttribute('aria-expanded', 'true');

    await user.tab();
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('88% of others guessed this too')).toBeInTheDocument();
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

// S-020 (design-document.md §2's "Rejected-guess cue"): a shake + red flash
// on a rejected guess, mechanically and visually distinct from S-015's
// badge-dock reveal above — different trigger (a rejection, not a match),
// never sharing a class or keyframe with the reveal. Same "transition, not
// mount" rule as useRevealToken in spirit: useShakeToken fires while the
// component stays mounted and attemptCount increases with isCorrect still
// false, and also — via the submittedThisSession prop — on a first mount
// that is itself a fresh rejection (a cell's first-ever guess this session,
// which GridCell mounts CellState directly into rather than transitioning
// into from an already-mounted render). It never fires on a first mount of
// a guess loaded from a page reload (submittedThisSession false/omitted).
// Exercised the same black-box way as S-015 — via CellState's rendered
// className/markup, not by reaching into the hook directly.
describe('CellState shake-and-flash reveal (S-020)', () => {
  it('S-020: a rejection transition (attemptCount increasing while still incorrect, at least one attempt remaining) applies cell-state--shake and remounts the DOM node, not just toggles a class', () => {
    const { container, rerender } = render(
      <CellState
        playerName="Ronaldinho"
        isCorrect={false}
        attemptCount={1}
        locked={false}
        roundStatus="active"
      />,
    );

    expect(container.querySelector('.cell-state--shake')).not.toBeInTheDocument();
    const nodeBeforeRejection = container.querySelector('.cell-state');

    rerender(
      <CellState
        playerName="Ronaldinho"
        isCorrect={false}
        attemptCount={2}
        locked={false}
        roundStatus="active"
      />,
    );

    expect(container.querySelector('.cell-state--shake')).toBeInTheDocument();
    // Node identity must actually change (a real remount), not just gain a
    // class — the same check S-015's "both reveals in one mounted lifetime"
    // test uses via node reference comparison, since key={shakeToken} is
    // what restarts the CSS animation on every rejection.
    expect(container.querySelector('.cell-state')).not.toBe(nodeBeforeRejection);
  });

  it('S-020: a rejection transition into state 3 (no attempts remaining) also applies cell-state--shake on the locked branch', () => {
    const { container, rerender } = render(
      <CellState
        playerName="Ronaldinho"
        isCorrect={false}
        attemptCount={1}
        locked={false}
        roundStatus="active"
      />,
    );

    expect(container.querySelector('.cell-state--shake')).not.toBeInTheDocument();

    rerender(
      <CellState
        playerName="Ronaldinho"
        isCorrect={false}
        attemptCount={2}
        locked
        roundStatus="active"
      />,
    );

    expect(screen.getByText('no attempts left')).toBeInTheDocument();
    expect(container.querySelector('.cell-state--locked')).toBeInTheDocument();
    expect(container.querySelector('.cell-state--shake')).toBeInTheDocument();
  });

  it('S-020: mounting directly already incorrect (e.g. a page reload, no prior render) shows no cell-state--shake class', () => {
    const { container } = render(
      <CellState
        playerName="Ronaldinho"
        isCorrect={false}
        attemptCount={1}
        locked={false}
        roundStatus="active"
      />,
    );

    expect(container.querySelector('.cell-state--shake')).not.toBeInTheDocument();
  });

  it('S-020: a correct-guess transition (incorrect -> correct) never applies cell-state--shake — that is the badge-dock reveal\'s territory, not this one', () => {
    const { container, rerender } = render(
      <CellState playerName="Henry" isCorrect={false} attemptCount={1} locked={false} roundStatus="active" />,
    );

    rerender(<CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" />);

    // The badge-dock reveal fires as expected (S-015's territory) while the
    // shake never does — the two animations stay mutually exclusive.
    expect(container.querySelector('.cell-state--reveal')).toBeInTheDocument();
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
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        submittedThisSession
      />,
    );

    expect(container.querySelector('.cell-state--shake')).not.toBeInTheDocument();
  });
});

import { fireEvent, render, screen } from '@testing-library/react';
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

  // S-033 (REQ-204), simplified same-day per direct feedback: a
  // locked-incorrect cell is guaranteed to lock at MaxPointsPerCell
  // (ADR-0021's golf-scoring worst case, not 0) — a known constant, not a
  // live computation, so it's shown immediately rather than waiting on
  // REQ-205's actual round-close lock. No "no attempts left" qualifier —
  // same minimal "✕ + points" structure a correct cell uses.
  it('REQ-210 state 3: incorrect with no attempts left is locked and shows the guaranteed MaxPointsPerCell value, nothing else', () => {
    render(<CellState playerName="Ronaldinho" isCorrect={false} attemptCount={2} locked roundStatus="active" />);

    expect(screen.getByText('✕')).toBeInTheDocument();
    expect(screen.getByText('100 pts')).toBeInTheDocument();
    expect(screen.queryByText('Ronaldinho')).not.toBeInTheDocument();
    expect(screen.queryByText(/no attempts left/i)).not.toBeInTheDocument();
  });

  it('REQ-210 state 4: round closed, correct outcome, shows only a checkmark and the locked FinalPoints — no "final" wording on a correct cell', () => {
    render(<CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="closed" finalPoints={88} />);

    expect(screen.getByText('✓')).toBeInTheDocument();
    expect(screen.getByText('88 pts')).toBeInTheDocument();
    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
    expect(screen.queryByText(/final/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/live/i)).not.toBeInTheDocument();
  });

  // Brought in line with state 3 the same day: state 4's incorrect outcome
  // now shows the same guaranteed MaxPointsPerCell value too, using the
  // frontend's own known constant rather than a FinalPoints value that
  // would need to come from the API (which, per the S-011 scope gap, this
  // state can't reach live yet anyway) — no "final" wording, same as
  // state 3 dropped "no attempts left".
  it('REQ-210 state 4: round closed, incorrect outcome, is locked and shows the guaranteed MaxPointsPerCell value, nothing else', () => {
    render(<CellState playerName="Ronaldinho" isCorrect={false} attemptCount={2} locked roundStatus="closed" />);

    expect(screen.getByText('✕')).toBeInTheDocument();
    expect(screen.getByText('100 pts')).toBeInTheDocument();
    expect(screen.queryByText('Ronaldinho')).not.toBeInTheDocument();
    expect(screen.queryByText(/final/i)).not.toBeInTheDocument();
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

  // Same parity check as above, for the incorrect-locked branch (state 3
  // vs. state 4's incorrect outcome) — both are guaranteed to score
  // MaxPointsPerCell regardless of when the cell locked, so their markup
  // should be identical too, not just individually correct.
  it('REQ-204: state 3 and state 4\'s incorrect outcome render identically in structure — checkmark + MaxPointsPerCell, no qualifier text of any kind', () => {
    const { container: activeContainer } = render(
      <CellState playerName="Ronaldinho" isCorrect={false} attemptCount={2} locked roundStatus="active" />,
    );
    const { container: closedContainer } = render(
      <CellState playerName="Ronaldinho" isCorrect={false} attemptCount={2} locked roundStatus="closed" />,
    );

    for (const container of [activeContainer, closedContainer]) {
      expect(container.querySelector('.cell-state__icon--incorrect')).toBeInTheDocument();
      expect(container.textContent).not.toMatch(/no attempts left|final/i);
    }

    expect(activeContainer.querySelector('.cell-state__meta')?.textContent).toBe(
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

// REQ-214 (2026-07-18 status note — photo decoupled from the click/tap
// reveal): a correct cell's photo shows automatically at rest, filling the
// cell, whenever one is available — no click/tap required, and REQ-212's
// existing reveal toggle continues to gate only the name/badge dock, now
// completely independent of the photo's own visibility (superseding PR
// #79's originally-shipped click-gated 18px-avatar version, which the
// now-deleted describe block this replaces used to cover). Degrades to
// exactly today's text-only, no-scrim at-rest display (no broken-image
// icon, no loading/error state, no footprint change) whenever no photo is
// available, whether that's because the field is absent entirely, explicitly
// null (backend confirmed no photo), or a same-session image load failure.
//
// S-048 (2026-07-19, direct user feedback — "at rest, only picture. on
// click name + points only in an overlay"): a further simplification on top
// of the above, superseding several of this block's own at-rest/revealed
// assertions (updated in place below, not left stale — the S-029 lesson).
// At rest, a photo cell now overlays nothing at all (no checkmark, no
// points, no scrim) — only once `revealed` does an overlay appear, and it
// carries only the name and points, never a checkmark or badge dock. The
// no-photo case is completely unaffected by S-048 and is covered by the
// separate describe block above.
describe('CellState photo reveal (REQ-214, 2026-07-18: decoupled from click/tap reveal; overlay content narrowed further by S-048, 2026-07-19)', () => {
  it('S-048: a photo shows automatically at rest (revealed=false, no click/tap) with nothing overlaid — no checkmark, no points, no name, no scrim', () => {
    const { container } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed={false}
      />,
    );

    // Decorative-image queries: an <img alt=""> has an accessible role of
    // "presentation"/"none" per the ARIA spec (not "img"), and is also
    // aria-hidden, so this is queried structurally rather than via
    // getByRole — same reasoning the badge-dock glyphs already use
    // (data-testid) for their own decorative, aria-hidden elements.
    const img = container.querySelector('.cell-state__photo-img');
    expect(img).toBeInTheDocument();
    expect(img).toHaveAttribute('src', 'https://example.test/henry.jpg');
    // Decorative only.
    expect(img).toHaveAttribute('alt', '');
    // S-048: at rest, a photo cell overlays nothing — no overlay element at
    // all, and none of its former contents (checkmark, points, name).
    expect(container.querySelector('.cell-state__overlay')).not.toBeInTheDocument();
    expect(screen.queryByText('✓')).not.toBeInTheDocument();
    expect(screen.queryByText('12 pts')).not.toBeInTheDocument();
    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
  });

  it('S-048: clicking/tapping the cell (revealed -> true) reveals an overlay with the name and points only — no checkmark, no badge dock — without affecting the photo\'s own visibility', () => {
    const { container, rerender } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
        revealed={false}
      />,
    );

    expect(container.querySelector('.cell-state__photo-img')).toBeInTheDocument();
    expect(screen.queryByText('Henry')).not.toBeInTheDocument();

    rerender(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
        revealed
      />,
    );

    const overlay = container.querySelector('.cell-state__overlay');
    expect(overlay).toBeInTheDocument();
    expect(screen.getByText('Henry')).toBeInTheDocument();
    expect(screen.getByText('12 pts')).toBeInTheDocument();
    // No checkmark anywhere in the revealed photo overlay (dropped
    // entirely, not merely hidden), and no badge dock either (S-047's
    // exception stands — never reintroduced by S-048).
    expect(screen.queryByText('✓')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state__icon')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state__badge-dock')).not.toBeInTheDocument();
    // Same photo `src`, still present — revealing the overlay did not
    // remount or hide the photo layer.
    const img = container.querySelector('.cell-state__photo-img');
    expect(img).toBeInTheDocument();
    expect(img).toHaveAttribute('src', 'https://example.test/henry.jpg');
  });

  it('S-048: clicking/tapping again (revealed -> false) removes the whole overlay (name and points) but the photo stays', () => {
    const { container, rerender } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed
      />,
    );

    expect(screen.getByText('Henry')).toBeInTheDocument();
    expect(screen.getByText('12 pts')).toBeInTheDocument();

    rerender(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed={false}
      />,
    );

    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
    expect(screen.queryByText('12 pts')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state__overlay')).not.toBeInTheDocument();
    // The photo, unlike the overlay, is unaffected by the toggle going back
    // to false — it's still showing.
    expect(container.querySelector('.cell-state__photo-img')).toBeInTheDocument();
  });

  it('REQ-214: no photoUrl field at all (today\'s baseline) falls back to exactly today\'s text-only at-rest display — no image, no broken-image icon, no scrim wrapper — fully unaffected by this story', () => {
    const { container } = render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" livePoints={12} />,
    );

    expect(container.querySelector('.cell-state__photo-img')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state__overlay')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state--photo')).not.toBeInTheDocument();
    expect(screen.getByText('✓')).toBeInTheDocument();
    expect(screen.getByText('12 pts')).toBeInTheDocument();
  });

  it('REQ-214: an explicit photoUrl={null} (backend confirmed no photo exists) degrades identically to the no-field baseline', () => {
    const { container: baselineContainer } = render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" livePoints={12} revealed />,
    );
    const { container: explicitNullContainer } = render(
      <CellState
        playerName="Henry"
        photoUrl={null}
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed
      />,
    );

    expect(explicitNullContainer.querySelector('.cell-state--photo')).not.toBeInTheDocument();
    // Byte-for-byte identical markup, not just "visually similar" — a real
    // regression check that the two "no photo" cases produce the same DOM,
    // not merely the same on-screen appearance.
    expect(explicitNullContainer.querySelector('.cell-state')?.outerHTML).toBe(
      baselineContainer.querySelector('.cell-state')?.outerHTML,
    );
  });

  it('REQ-214: an image load failure removes the photo and falls back to text-only at rest, never showing a broken-image icon', () => {
    const { container } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/broken.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
      />,
    );

    const img = container.querySelector('.cell-state__photo-img') as HTMLImageElement;
    expect(img).toBeInTheDocument();
    fireEvent.error(img);

    expect(container.querySelector('.cell-state__photo-img')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state--photo')).not.toBeInTheDocument();
    expect(screen.getByText('✓')).toBeInTheDocument();
    expect(screen.getByText('12 pts')).toBeInTheDocument();
  });

  it('REQ-214: an incorrect (state 2/3) cell never shows a photo even if photoUrl is present, unchanged from REQ-212\'s existing rule for names', () => {
    const { container } = render(
      <CellState
        playerName="Ronaldinho"
        photoUrl="https://example.test/ronaldinho.jpg"
        isCorrect={false}
        attemptCount={2}
        locked
        roundStatus="active"
      />,
    );

    expect(container.querySelector('.cell-state__photo-img')).not.toBeInTheDocument();
  });

  // REQ-212's own status note records a real layout bug (the name silently
  // shrinking to zero width) that was missed by tests and only caught by
  // required manual browser verification — this test exists specifically so
  // that class of bug can't recur silently for the photo layer. jsdom has no
  // real layout engine (no box model, no actual rendering), so it cannot
  // report true pixel bounding boxes the way a real browser could; the
  // closest genuine regression signal available here is asserting the CSS
  // rules that are the *mechanism* by which the footprint stays fixed are
  // actually in effect — not a snapshot of appearance, a check of the
  // layout-affecting properties themselves. See this story's own real-browser
  // verification for the check this doesn't replace.
  it('REQ-214: the photo layer is taken out of normal flow (position: absolute, inset: 0) so it can never grow the cell\'s own box — the mechanism that keeps the footprint fixed regardless of the photo', () => {
    const { container } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed={false}
      />,
    );

    const photoLayer = container.querySelector('.cell-state--photo');
    expect(photoLayer).toBeInTheDocument();
    const photoLayerStyle = getComputedStyle(photoLayer as Element);
    // Removed from whatever normal flow it sits in (Grid.css's .grid-cell,
    // its positioned ancestor) — this is what lets it fill the cell without
    // the cell's own box ever being sized by the photo's content. Checked
    // via the `inset` shorthand itself (jsdom's CSSOM doesn't expand it into
    // the four longhands the way a real browser's getComputedStyle would).
    expect(photoLayerStyle.position).toBe('absolute');
    expect(photoLayerStyle.getPropertyValue('inset')).toBe('0px');

    const img = container.querySelector('.cell-state__photo-img');
    expect(img).toBeInTheDocument();
    const imgStyle = getComputedStyle(img as Element);
    // The image inside is cropped to fill the fixed box (object-fit: cover)
    // rather than the box growing to fit the image — never sized by its own
    // intrinsic dimensions (no width:auto/height:auto here).
    expect(imgStyle.position).toBe('absolute');
    expect(imgStyle.objectFit).toBe('cover');
    expect(imgStyle.width).toBe('100%');
    expect(imgStyle.height).toBe('100%');
  });

  it('REQ-214: the no-photo (`.cell-state` without `--photo`) case is never absolutely positioned — only the photo path uses the fill-the-cell mechanism, the plain text-only at-rest display is unaffected', () => {
    const { container } = render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" livePoints={12} />,
    );

    const cellState = container.querySelector('.cell-state');
    expect(cellState).toBeInTheDocument();
    expect(cellState?.classList.contains('cell-state--photo')).toBe(false);
    const style = getComputedStyle(cellState as Element);
    expect(style.position).not.toBe('absolute');
  });

  // S-048 superseded this test's premise: before this story, both the
  // no-photo and photo cases rendered a `.cell-state__row` (badges + name +
  // icon) once revealed, wrapped differently but structurally identical.
  // As of S-048, the photo case no longer renders `.cell-state__row` (or
  // `Row`) at all — only a plain name + points overlay — so the two cases
  // are now deliberately *not* structurally identical once revealed. This
  // replaces the old equality check with the new, correct invariant.
  it('S-048: a revealed photo cell renders no cell-state__row, no icon, and no badge dock at all — only the no-photo case still uses that structure', () => {
    const { container: noPhotoContainer } = render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
        revealed
      />,
    );
    const { container: photoContainer } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
        revealed
      />,
    );

    // No-photo case: unaffected, still the full Row structure.
    expect(noPhotoContainer.querySelector('.cell-state__row')).toBeInTheDocument();
    expect(noPhotoContainer.querySelector('.cell-state__icon')).toBeInTheDocument();
    expect(noPhotoContainer.querySelector('.cell-state__badge-dock')).toBeInTheDocument();

    // Photo case (S-048): none of that structure exists — only the name and
    // points, directly inside `.cell-state__overlay`.
    expect(photoContainer.querySelector('.cell-state__row')).not.toBeInTheDocument();
    expect(photoContainer.querySelector('.cell-state__icon')).not.toBeInTheDocument();
    expect(photoContainer.querySelector('.cell-state__badge-dock')).not.toBeInTheDocument();
    expect(photoContainer.querySelector('.cell-state__overlay')).toBeInTheDocument();
    expect(photoContainer.querySelector('.cell-state__name')).toBeInTheDocument();
    expect(photoContainer.querySelector('.cell-state__meta')).toBeInTheDocument();
  });

  // S-048: the points value is now only rendered once revealed (never at
  // rest), so `revealed` must be passed for this element to exist at all —
  // the contrast pairing itself is unchanged from before S-048.
  it('REQ-214: the overlaid points value uses accent-gold (not accent-gold-text) on the photo\'s scrim, the token pairing design-document.md §2\'s overlay-scrim entry specifies for this dark backdrop', () => {
    const { container } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed
      />,
    );

    const meta = container.querySelector('.cell-state__meta');
    expect(meta).toBeInTheDocument();
    expect(getComputedStyle(meta as Element).color).toBe('var(--color-accent-gold)');

    // Compare against a no-photo correct cell's own meta color — the two
    // must differ, since the no-photo case is the one that should keep
    // using accent-gold-text (its background is surface-card, not the
    // scrim).
    const { container: noPhotoContainer } = render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" livePoints={12} />,
    );
    const noPhotoMeta = noPhotoContainer.querySelector('.cell-state__meta');
    expect(getComputedStyle(meta as Element).color).not.toBe(getComputedStyle(noPhotoMeta as Element).color);
  });

  // S-048 (2026-07-19, direct user feedback) superseded the checkmark
  // exception this test used to cover: as of S-048, a photo cell never
  // renders a checkmark at all, at rest or revealed — the
  // `accent-green-scrim` token this test used to assert on is now dormant
  // (design-document.md §2's matching note). Replaced with the correct
  // current invariant: no checkmark anywhere on a photo cell, in either
  // state, while the no-photo case keeps its checkmark exactly as before.
  it('S-048: a photo cell never renders a checkmark icon, at rest or revealed — the no-photo case is unaffected and still shows one', () => {
    const { container: restContainer } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed={false}
      />,
    );
    const { container: revealedContainer } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed
      />,
    );

    expect(restContainer.querySelector('.cell-state__icon')).not.toBeInTheDocument();
    expect(revealedContainer.querySelector('.cell-state__icon')).not.toBeInTheDocument();
    expect(revealedContainer.querySelector('.cell-state__icon--correct')).not.toBeInTheDocument();

    const { container: noPhotoContainer } = render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" livePoints={12} />,
    );
    expect(noPhotoContainer.querySelector('.cell-state__icon--correct')).toBeInTheDocument();
  });

  // Found via this story's own real-browser verification (not caught by the
  // contrast-math pass that only covered the checkmark/points): the revealed
  // name has no correct/incorrect semantic color of its own, so it normally
  // renders in text-primary (near-black) — illegible against the same
  // near-black overlay-scrim. design-document.md §2's overlay-scrim entry
  // now documents the fix (reuse surface-card/white, the lightest neutral
  // already in the token table) alongside the accent-gold pairing above.
  it('REQ-214: the revealed name uses a light (surface-card) color on the photo\'s scrim, not the normal near-black text-primary that would be illegible there', () => {
    const { container } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed
      />,
    );

    // jsdom's CSSOM returns the declared `var(...)` reference itself here
    // rather than resolving it to a final rgb() value (unlike a real
    // browser) — asserted against the token reference for that reason, same
    // approach the accent-gold-vs-accent-gold-text test above already uses.
    const name = container.querySelector('.cell-state__name');
    expect(name).toBeInTheDocument();
    expect(getComputedStyle(name as Element).color).toBe('var(--color-surface-card)');

    const { container: noPhotoContainer } = render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed
      />,
    );
    const noPhotoName = noPhotoContainer.querySelector('.cell-state__name');
    // The no-photo case is unaffected — still the normal near-black
    // text-primary against the plain surface-card cell background.
    expect(getComputedStyle(noPhotoName as Element).color).toBe('var(--color-text-primary)');
    expect(getComputedStyle(noPhotoName as Element).color).not.toBe(getComputedStyle(name as Element).color);
  });

  // S-047 (direct user feedback: the photo overlay "covers too much of the
  // photo" on mobile — see design-document.md's SCREEN-01a S-047 status
  // note for the full before/after numbers). jsdom has no real layout
  // engine, so — same "check the mechanism, not a pixel snapshot" approach
  // the REQ-214 footprint tests above already use — this checks the
  // declared CSS properties that are what actually shrinks the overlay's
  // footprint (tighter padding, smaller type on the photo variant only),
  // not a rendered bounding box. See this story's own real-browser
  // verification for the pixel-level check this doesn't replace.
  it("S-047: the photo overlay's padding is tighter than the prior uniform --space-2 (8px) on every side — now --space-1 vertical / --space-2 horizontal", () => {
    // S-048: the overlay only exists once revealed (never at rest), unlike
    // when this test was first written — `revealed` is required here now.
    const { container } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed
      />,
    );

    const overlay = container.querySelector('.cell-state__overlay');
    expect(overlay).toBeInTheDocument();
    const style = getComputedStyle(overlay as Element);
    expect(style.paddingTop).toBe('var(--space-1)');
    expect(style.paddingBottom).toBe('var(--space-1)');
    expect(style.paddingLeft).toBe('var(--space-2)');
    expect(style.paddingRight).toBe('var(--space-2)');
  });

  // S-048 superseded this test's checkmark comparison — a photo cell no
  // longer renders a checkmark at all (see the dedicated S-048 test above),
  // so there's nothing left to compare its font-size against; that
  // assertion is dropped here rather than compared against a
  // no-longer-existing element. The meta/name size comparisons are
  // otherwise unaffected by S-048 (still the S-047 reductions).
  it('S-047: the photo variant renders the points and (once revealed) the name smaller than the no-photo case — one of the changes that shrinks the overlay toward the design doc\'s coverage target', () => {
    const { container: photoContainer } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed
      />,
    );
    const { container: noPhotoContainer } = render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" livePoints={12} revealed />,
    );

    const photoMeta = photoContainer.querySelector('.cell-state__meta');
    const noPhotoMeta = noPhotoContainer.querySelector('.cell-state__meta');
    expect(getComputedStyle(photoMeta as Element).fontSize).toBe('10px');
    expect(getComputedStyle(noPhotoMeta as Element).fontSize).toBe('11px');

    const photoName = photoContainer.querySelector('.cell-state__name');
    const noPhotoName = noPhotoContainer.querySelector('.cell-state__name');
    expect(getComputedStyle(photoName as Element).fontSize).toBe('12px');
    // The no-photo name has no explicit font-size override (relies on the
    // browser/body default) — asserted as "not 12px" rather than a specific
    // value, since that default isn't a value this file owns or should pin.
    expect(getComputedStyle(noPhotoName as Element).fontSize).not.toBe('12px');
  });

  // S-048 removed the CSS rule this test used to check
  // (`.cell-state--photo .cell-state__row`'s tighter gap) as dead code,
  // since `.cell-state__row`/`Row` is no longer rendered inside a photo
  // cell at all (see CellState.css's S-048 removal note). Replaced with an
  // assertion of the current structure: the photo overlay's own gap (name
  // line to points line) still uses the tightened --space-1 S-047
  // introduced, just on `.cell-state__overlay` directly rather than a row
  // nested inside it.
  it("S-048: the photo overlay's own gap (name line to points line) uses the tightened --space-1, same value S-047 introduced, now applied directly rather than via a nested cell-state__row", () => {
    const { container: photoContainer } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed
      />,
    );

    const overlay = photoContainer.querySelector('.cell-state__overlay');
    expect(overlay).toBeInTheDocument();
    expect(getComputedStyle(overlay as Element).gap).toBe('var(--space-1)');

    const { container: noPhotoContainer } = render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" livePoints={12} revealed />,
    );
    const noPhotoRow = noPhotoContainer.querySelector('.cell-state__row');
    expect(getComputedStyle(noPhotoRow as Element).gap).toBe('var(--space-2)');
  });

  // S-047 originally fixed this by hiding both badge-dock glyphs via CSS
  // (`display: none`) while leaving them in the DOM. S-048 goes further:
  // CellState.tsx's photo branch no longer renders `Row`/badge-dock markup
  // for a photo cell at all, in either state, so there's nothing left to
  // hide via CSS — this test now asserts the badges are absent from the
  // DOM entirely, not merely hidden. The underlying reason is unchanged
  // (badges are decorative/redundant with the row/column headers, and the
  // confined overlay doesn't have room for them at typical mobile widths)
  // and the no-photo case's badge dock is still fully present and visible.
  it('S-048: the badge dock (row and column glyphs) is entirely absent from a photo cell once revealed, not just hidden — the no-photo case keeps showing its badge dock unchanged', () => {
    const { container: photoContainer } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
        revealed
      />,
    );
    const { container: noPhotoContainer } = render(
      <CellState
        playerName="Henry"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        rowCategoryType="country"
        rowCategoryValue="France"
        colCategoryType="club"
        colCategoryValue="Arsenal"
        revealed
      />,
    );

    // S-048: not present in the DOM at all, unlike S-047's display:none.
    expect(photoContainer.querySelector('.cell-state__badge-dock--row')).not.toBeInTheDocument();
    expect(photoContainer.querySelector('.cell-state__badge-dock--col')).not.toBeInTheDocument();

    const noPhotoBadgeRow = noPhotoContainer.querySelector('.cell-state__badge-dock--row');
    expect(getComputedStyle(noPhotoBadgeRow as Element).display).not.toBe('none');
    const noPhotoBadgeCol = noPhotoContainer.querySelector('.cell-state__badge-dock--col');
    expect(getComputedStyle(noPhotoBadgeCol as Element).display).not.toBe('none');

    // The name itself is unaffected by this — still present and, on the
    // photo variant, still legible (not what this test covers directly,
    // see the line-clamp test below).
    expect(photoContainer.querySelector('.cell-state__name')).toBeInTheDocument();
  });

  // S-047: the companion fix to the badge-dock hide above — bounds the
  // revealed name's own box to a single line (with an ellipsis) on the
  // photo variant, so a long name can never grow the row taller than the
  // fixed-footprint cell can show. A 2-line clamp was tried first and
  // rejected after real-browser verification showed it could still get
  // clipped from the top by `.cell-state--photo`'s `overflow: hidden`,
  // leaving an unpredictable *middle* fragment of the name visible rather
  // than its actual beginning.
  it("S-047: the photo variant's revealed name is clamped to a single line (ellipsis on overflow) — the no-photo case has no such clamp", () => {
    const { container: photoContainer } = render(
      <CellState
        playerName="A Genuinely Very Long Player Full Name"
        photoUrl="https://example.test/henry.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed
      />,
    );
    const { container: noPhotoContainer } = render(
      <CellState
        playerName="A Genuinely Very Long Player Full Name"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed
      />,
    );

    const photoName = photoContainer.querySelector('.cell-state__name');
    const style = getComputedStyle(photoName as Element);
    expect(style.getPropertyValue('-webkit-line-clamp')).toBe('1');
    expect(style.overflow).toBe('hidden');
    // The full name is still in the DOM (accessible to assistive tech and
    // to any test that queries text content) — only the visual box is
    // clamped, nothing is removed from the markup.
    expect(photoName?.textContent).toBe('A Genuinely Very Long Player Full Name');

    const noPhotoName = noPhotoContainer.querySelector('.cell-state__name');
    expect(getComputedStyle(noPhotoName as Element).getPropertyValue('-webkit-line-clamp')).not.toBe('1');
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

    expect(screen.getByText('100 pts')).toBeInTheDocument();
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

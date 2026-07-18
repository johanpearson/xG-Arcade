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

// REQ-214 (Photo reveal on a locked, correct cell): a photo shows/hides in
// lockstep with REQ-212's existing reveal toggle — never a separate control
// — and degrades to exactly today's text-only reveal (no broken-image icon,
// no loading/error state, no footprint change) whenever no photo is
// available, whether that's because the field is absent entirely (today's
// baseline, before this story), explicitly null (backend confirmed no
// photo), or a same-session image load failure.
describe('CellState photo reveal (REQ-214)', () => {
  it('REQ-214: a revealed correct cell with a photoUrl shows the photo alongside the name', () => {
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

    // Decorative-image queries: an <img alt=""> has an accessible role of
    // "presentation"/"none" per the ARIA spec (not "img"), and is also
    // aria-hidden, so this is queried structurally rather than via
    // getByRole — same reasoning the badge-dock glyphs already use
    // (data-testid) for their own decorative, aria-hidden elements.
    const img = container.querySelector('.cell-state__avatar img');
    expect(img).toBeInTheDocument();
    expect(img).toHaveAttribute('src', 'https://example.test/henry.jpg');
    // Decorative only — the visible name text is the accessible identifier,
    // same pairing rule §6 already applies to the flag/badge glyphs.
    expect(img).toHaveAttribute('alt', '');
    expect(screen.getByText('Henry')).toBeInTheDocument();
  });

  it('REQ-214: no photoUrl field at all (today\'s baseline) falls back to exactly today\'s text-only reveal — no image, no broken-image icon', () => {
    const { container } = render(
      <CellState playerName="Henry" isCorrect attemptCount={1} locked roundStatus="active" livePoints={12} revealed />,
    );

    expect(container.querySelector('.cell-state__avatar img')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state__avatar')).not.toBeInTheDocument();
    expect(screen.getByText('Henry')).toBeInTheDocument();
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

    expect(explicitNullContainer.querySelector('.cell-state__avatar')).not.toBeInTheDocument();
    // Byte-for-byte identical markup, not just "visually similar" — a real
    // regression check that the two "no photo" cases produce the same DOM,
    // not merely the same on-screen appearance.
    expect(explicitNullContainer.querySelector('.cell-state')?.outerHTML).toBe(
      baselineContainer.querySelector('.cell-state')?.outerHTML,
    );
  });

  it('REQ-214: an image load failure removes the photo and falls back to text-only, never showing a broken-image icon', () => {
    const { container } = render(
      <CellState
        playerName="Henry"
        photoUrl="https://example.test/broken.jpg"
        isCorrect
        attemptCount={1}
        locked
        roundStatus="active"
        livePoints={12}
        revealed
      />,
    );

    const img = container.querySelector('.cell-state__avatar img') as HTMLImageElement;
    expect(img).toBeInTheDocument();
    fireEvent.error(img);

    expect(container.querySelector('.cell-state__avatar img')).not.toBeInTheDocument();
    expect(container.querySelector('.cell-state__avatar')).not.toBeInTheDocument();
    expect(screen.getByText('Henry')).toBeInTheDocument();
  });

  it('REQ-214: hiding the cell (revealed -> false) hides the photo along with the name — the same toggle, not a separate control', () => {
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

    expect(container.querySelector('.cell-state__avatar img')).toBeInTheDocument();

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

    expect(container.querySelector('.cell-state__avatar img')).not.toBeInTheDocument();
    expect(screen.queryByText('Henry')).not.toBeInTheDocument();
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
        revealed
      />,
    );

    expect(container.querySelector('.cell-state__avatar img')).not.toBeInTheDocument();
  });

  // REQ-212's own status note records a real layout bug (the name silently
  // shrinking to zero width) that was missed by tests and only caught by
  // required manual browser verification — this test exists specifically so
  // that class of bug can't recur silently for the photo slot. jsdom has no
  // real layout engine (no box model, no actual rendering), so it cannot
  // report true pixel bounding boxes the way a real browser could; the
  // closest genuine regression signal available here is asserting the CSS
  // rules that are the *mechanism* by which the footprint stays fixed are
  // actually in effect (literal, non-content-derived pixel dimensions and
  // flex-shrink: 0 on the avatar slot, matching the badge dock's own already
  // battle-tested size) — not a snapshot of appearance, a check of the
  // layout-affecting properties themselves. See the "what to build" item 5
  // in this story's brief for the real-browser check this doesn't replace.
  it('REQ-214: the avatar occupies a fixed slot sized to match the existing badge dock — a photo cannot grow the row beyond what badges already reserve', () => {
    const { container } = render(
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

    // Country categories (rowCategoryType here) render as a flag emoji, not
    // a sized badge — the club badge (colCategoryType) is the one comparable
    // fixed-size circle already on this row, so that's the baseline
    // compared against below.
    const avatar = container.querySelector('.cell-state__avatar');
    const colBadge = container.querySelector('.cell-state__badge-dock--col .category-label__badge--small');
    expect(avatar).toBeInTheDocument();
    expect(colBadge).toBeInTheDocument();

    const avatarStyle = getComputedStyle(avatar as Element);
    const badgeStyle = getComputedStyle(colBadge as Element);

    // Fixed, literal (not content-derived) dimensions — an image can never
    // push this box larger, since width/height aren't driven by the image's
    // own intrinsic size (no width:auto/height:auto here).
    expect(avatarStyle.width).toBe('18px');
    expect(avatarStyle.height).toBe('18px');
    // Exactly the badge dock's own already-shipped "small" size — the photo
    // slot can't make the row taller than the badge sitting right next to it
    // already does.
    expect(avatarStyle.width).toBe(badgeStyle.width);
    expect(avatarStyle.height).toBe(badgeStyle.height);
    // Never compressible, same policy as the badges/icon it sits beside —
    // and never allowed to grow past its fixed basis either.
    expect(avatarStyle.flexShrink).toBe('0');

    // The image inside is cropped to fill the fixed box (object-fit: cover)
    // rather than the box growing to fit the image.
    const img = avatar?.querySelector('img');
    const imgStyle = getComputedStyle(img as Element);
    expect(imgStyle.objectFit).toBe('cover');
    expect(imgStyle.width).toBe('100%');
    expect(imgStyle.height).toBe('100%');
  });

  it('REQ-214: cell-state__row itself renders with the identical class/structure whether or not a photo is present — no extra wrapper reflows the row', () => {
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

    const noPhotoRow = noPhotoContainer.querySelector('.cell-state__row');
    const photoRow = photoContainer.querySelector('.cell-state__row');
    expect(getComputedStyle(noPhotoRow as Element).display).toBe(getComputedStyle(photoRow as Element).display);
    expect(getComputedStyle(noPhotoRow as Element).flexWrap).toBe(getComputedStyle(photoRow as Element).flexWrap);
    // Same number of direct flex-item children either way (row badge dock,
    // name group, col badge dock, icon) — the photo lives *inside* the name
    // group rather than adding a fifth item to the row's own flex layout.
    expect(noPhotoRow?.children.length).toBe(photoRow?.children.length);
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

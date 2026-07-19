import { useEffect, useRef, useState } from 'react';
import { MAX_ATTEMPTS_PER_CELL } from '../lib/guessRules';
import { MAX_POINTS_PER_CELL } from '../lib/scoringRules';
import { CategoryGlyph } from './CategoryLabel';
import './CellState.css';

export type RoundStatus = 'active' | 'closed';

export interface CellStateProps {
  // The correct guess's canonical, properly-cased name (GET /rounds/current
  // and the guess-submission response both now return this —
  // resolvedPlayerName). Only ever passed for a correct guess; an incorrect
  // guess renders no name at all (only that it was wrong), never the raw
  // as-typed text. Falls back to a plain, non-fabricated label if somehow
  // absent for a correct guess, rather than inventing a name.
  playerName?: string;
  // REQ-214 (2026-07-18 status note): a nullable Wikidata photo URL for the
  // resolved player, shown automatically at rest, filling the cell —
  // independent of `revealed` below, unlike playerName. See the
  // CurrentRoundGuess type's own doc comment for the confirmed backend
  // field this maps to. `undefined` (field never sent), `null` (backend
  // confirmed no photo exists), and a same-session image load failure are
  // all treated identically: fall back to exactly today's text-only,
  // no-photo at-rest display, no broken-image icon, no error state. Only
  // ever meaningful for a correct guess, same as playerName.
  photoUrl?: string | null;
  isCorrect: boolean;
  attemptCount: number;
  locked: boolean;
  // Explicit prop (not inferred) so this component can be exercised for
  // state 4 (round closed) via constructed props in tests, without wiring
  // up a live "closed round" flow that GET /rounds/current can't produce
  // today (S-011 scope).
  roundStatus: RoundStatus;
  // S-018/REQ-204: live, provisional point estimate for state 1 only — same
  // round(uniqueScore * MaxPointsPerCell) formula REQ-205 locks at round
  // close, computed live and re-derived on every request. S-041: this is
  // the *only* thing state 1 shows alongside the checkmark (no dot, no
  // "live"/"~"/"estimated" wording, no percent) — see REQ-204's 2026-07-14
  // status note. S-048 exception: on a photo cell specifically, this value
  // is no longer shown at rest at all — only once `revealed`, alongside
  // the name, with no checkmark — see REQ-204's 2026-07-19 status note.
  // Never reused for state 4's finalPoints, which is a separate, locked
  // prop.
  livePoints?: number | null;
  // REQ-205/206: the locked, permanent score — only meaningful once
  // roundStatus is "closed". Not reachable via GET /rounds/current today
  // (that endpoint only ever returns an Active round, same S-011-scope gap
  // as SCREEN-01a's state 4 generally), so this is exercised via
  // constructed props only, same as roundStatus="closed" itself.
  finalPoints?: number | null;
  // design-document.md §2's "signature element: badge dock" — the two
  // categories this cell combines, needed to render their flag/badge
  // glyphs docked beside the revealed name on a correct guess. Optional:
  // callers that don't pass these simply get no badge dock (e.g. existing
  // tests constructed before S-015).
  rowCategoryType?: string;
  rowCategoryValue?: string;
  colCategoryType?: string;
  colCategoryValue?: string;
  // S-020: true only when this guess result came from a submission made in
  // this browser session (GridCell derives it from knownPlayerName being
  // set), false for a guess loaded from GET /rounds/current on page load
  // (e.g. a reload showing a cell someone already attempted). Needed
  // because CellState doesn't exist in the tree at all for an unattempted
  // cell (GridCell's guard clause) — a cell's *first* guess this session
  // therefore mounts CellState directly into the rejected state rather than
  // transitioning into it from an already-mounted incorrect render, which
  // useShakeToken's normal transition-based logic can't see. Only consulted
  // by useShakeToken's initial state below; useRevealToken (S-015) is
  // intentionally left alone — fixing its equivalent first-correct-guess
  // gap is out of this story's scope.
  submittedThisSession?: boolean;
  // REQ-212 (S-041): whether the guessed player's name/badge dock is
  // currently shown. Owned by the caller (GridCell), not this component —
  // the click/tap target that toggles it is the whole cell, which GridCell
  // renders, not a control inside CellState. Only meaningful when
  // isCorrect+locked (states 1/4); ignored otherwise, since states 2/3
  // never show a name regardless (S-029). S-048: on a photo cell
  // specifically, this prop also gates the points value (never gated by it
  // in the no-photo case, where points stays always-visible at rest) — see
  // REQ-212's 2026-07-19 status note.
  revealed?: boolean;
}

interface CategoryRef {
  categoryType: string;
  value: string;
}

// design-document.md §2 "signature element: badge dock": the row/column
// badge slides in and settles beside the player name the moment it becomes
// visible. S-041: that moment is now whenever `revealed` transitions from
// false to true (a click/tap on the cell, REQ-212) — before this story it
// was tied to a guess-submit/round-close transition instead, since the name
// used to show automatically at one of those two points; now it never shows
// until the player actively reveals it. Returns a token that increments on
// every such transition (replaying the animation on every reveal, not just
// the first) — 0 means "never revealed yet," i.e. render nothing/no
// animation.
function useRevealToken(revealed: boolean): number {
  const prevRevealedRef = useRef(revealed);
  const [token, setToken] = useState(0);

  useEffect(() => {
    if (revealed && !prevRevealedRef.current) {
      setToken((current) => current + 1);
    }
    prevRevealedRef.current = revealed;
  }, [revealed]);

  return token;
}

// design-document.md §2 "Rejected-guess cue" (S-020): a shake + red flash on
// a rejected guess — mechanically and visually distinct from
// useRevealToken's badge-dock reveal above (triggered by a rejection, not a
// match; never touches badge-dock elements or keyframes). Fires whenever
// attemptCount increases while the cell is still incorrect, whether or not
// an attempt remains afterward (state 2 -> state 2, or state 2 -> state 3).
//
// A cell's *first* guess this session is a special case: GridCell doesn't
// render CellState at all for an unattempted cell, so the first guess
// mounts CellState directly already-incorrect rather than transitioning
// into that state from an already-mounted render — indistinguishable, from
// inside this hook alone, from a page reload showing a cell someone else
// already attempted (which must NOT shake). `submittedThisSession` is the
// caller-supplied signal that breaks that tie: true only means "this
// specific guess result was just submitted in this browser session," so
// seeding the token on mount in that case still fires the cue on a fresh
// cell's first rejection while a real page-load mount (submittedThisSession
// false/undefined) stays silent, same as before.
function useShakeToken(attemptCount: number, isCorrect: boolean, submittedThisSession: boolean): number {
  const prevRef = useRef({ attemptCount, isCorrect });
  const [token, setToken] = useState(() => (submittedThisSession && !isCorrect ? 1 : 0));

  useEffect(() => {
    const prev = prevRef.current;
    const justRejected = !isCorrect && !prev.isCorrect && attemptCount > prev.attemptCount;
    if (justRejected) {
      setToken((current) => current + 1);
    }
    prevRef.current = { attemptCount, isCorrect };
  }, [attemptCount, isCorrect]);

  return token;
}

// SCREEN-01a: the four "attempted" cell states from REQ-210. An unattempted
// cell (guess === null) is a simple guard clause in the caller (GridCell),
// not a fifth case here — this component always assumes at least one
// attempt was made.
//
// S-041 (REQ-204/212): states 1 (correct, round active) and 4 (correct,
// round closed) are a single rendering branch — both show only a checkmark
// plus a points value at rest (state 1's live estimate, state 4's locked
// FinalPoints), with no live/final distinction on the cell itself. The
// player name/badge dock, gated behind `revealed` (owned by GridCell,
// REQ-212), is the only thing that differs by interaction, not by state.
// S-048 exception: this description is for the no-photo case only — a
// correct cell with a photo (`hasPhoto` below) shows nothing at rest and
// only the name + points (no checkmark, no badge dock) once revealed. See
// the `isCorrect` branch's own comment below for the full detail.
export function CellState({
  playerName,
  photoUrl,
  isCorrect,
  attemptCount,
  locked,
  roundStatus,
  livePoints,
  finalPoints,
  rowCategoryType,
  rowCategoryValue,
  colCategoryType,
  colCategoryValue,
  submittedThisSession = false,
  revealed = false,
}: CellStateProps) {
  const name = playerName ?? 'Guess submitted';
  const revealToken = useRevealToken(revealed);
  const shakeToken = useShakeToken(attemptCount, isCorrect, submittedThisSession);
  // REQ-214 (2026-07-18 status note): whether the photo layer is currently
  // showing — true whenever a photoUrl was given and hasn't failed to load
  // this session, false otherwise (missing/null field, or a load failure
  // already observed). Deliberately NOT gated by `revealed` — that's the
  // whole point of this status note superseding the original click-gated
  // shipped version. Hoisted to this top level (rather than living inside
  // Row/a nested component the way the old 18px-avatar version did) because
  // the photo is now rendered whether or not the cell is revealed, so its
  // failure state needs to survive independently of the reveal toggle.
  const [photoFailed, setPhotoFailed] = useState(false);
  const badges =
    rowCategoryType != null && rowCategoryValue != null && colCategoryType != null && colCategoryValue != null
      ? {
          row: { categoryType: rowCategoryType, value: rowCategoryValue },
          col: { categoryType: colCategoryType, value: colCategoryValue },
        }
      : undefined;

  // States 1/4 (correct): identical structure, differing only in which
  // points value is shown.
  if (isCorrect) {
    const points = roundStatus === 'closed' ? finalPoints : livePoints;
    const hasPhoto = Boolean(photoUrl) && !photoFailed;

    // S-048 (direct user feedback: "at rest, only picture. on click name +
    // points only in an overlay"): a photo cell's overlay is now rendered
    // only when `revealed`, and holds only the name and points — no
    // checkmark (dropped entirely, not merely relocated behind the toggle;
    // see design-document.md §2's `accent-green-scrim` token note, now
    // dormant) and no badge dock (already dropped by S-047, stays dropped).
    // Before this story, a correct cell's checkmark+points was shown at
    // rest unconditionally (REQ-204) and only the name/badge dock were
    // gated by `revealed` (REQ-212) — that's still true for the no-photo
    // case below, but no longer true for the photo case: at rest a photo
    // cell overlays nothing at all, so its only always-visible signal that
    // the cell is "done" is the photo's own presence, not a score value —
    // a deliberate trade-off recorded in design-document.md SCREEN-01a's
    // S-048 status note and requirements-document.md's matching REQ-204
    // status note, not assumed to be obviously fine.
    if (hasPhoto) {
      return (
        // key={revealToken}: forces a remount (not just a class toggle) so
        // the reveal effect restarts on every reveal, even a second reveal
        // after the player closed and reopened it (same className string
        // both times, which a mere re-render wouldn't restart). The photo
        // layer itself is unaffected by this remount — it isn't driven by
        // revealToken at all, so it doesn't flicker/reload just because the
        // overlay was toggled.
        <div
          key={revealToken}
          className={`cell-state cell-state--correct cell-state--photo ${revealed ? 'cell-state--reveal' : ''}`}
        >
          <CellPhoto src={photoUrl as string} onError={() => setPhotoFailed(true)} />
          {revealed && (
            <div className="cell-state__overlay">
              <span className="cell-state__name">{name}</span>
              {points != null && <p className="cell-state__meta mono-figure">{points} pts</p>}
            </div>
          )}
        </div>
      );
    }

    // No-photo case, unchanged by S-048: checkmark + points always visible
    // at rest (REQ-204), name + badge dock gated by `revealed` (REQ-212).
    const overlayContent = (
      <>
        <Row name={revealed ? name : undefined} correct badges={revealed ? badges : undefined} />
        {points != null && <p className="cell-state__meta mono-figure">{points} pts</p>}
      </>
    );

    return (
      <div key={revealToken} className={`cell-state cell-state--correct ${revealed ? 'cell-state--reveal' : ''}`}>
        {overlayContent}
      </div>
    );
  }

  const attemptsLeft = MAX_ATTEMPTS_PER_CELL - attemptCount;
  // key={shakeToken}: forces a remount so the shake/flash restarts on each
  // rejected guess, even if a prior rejection already left the same
  // className string in place (e.g. two rejections in a row while still in
  // state 2) — same technique useRevealToken's callers use above.
  const shakeClassName = shakeToken > 0 ? 'cell-state--shake' : '';

  // States 2/3 (incorrect): no name is shown at all, not even the raw guess —
  // just the ✕ and the attempts text. A wrong guess isn't useful information
  // for anyone else viewing the grid later, and showing the as-typed text
  // (rather than a real player's canonical name) was misleading either way.
  // State 2: incorrect, at least one attempt remaining.
  if (!locked && attemptsLeft > 0) {
    return (
      <div key={shakeToken} className={`cell-state cell-state--incorrect ${shakeClassName}`}>
        <Row correct={false} />
        <p className="cell-state__meta">
          {attemptsLeft} attempt{attemptsLeft === 1 ? '' : 's'} left
        </p>
      </div>
    );
  }

  // State 3 (round active, no attempts left) / state 4's incorrect outcome
  // (round closed): both locked, both guaranteed to score MaxPointsPerCell
  // (ADR-0021's golf-scoring worst case, never 0 — 0 is reserved for the
  // best possible correct guess). Same minimal "✕ + points" structure the
  // correct branch above uses, no "no attempts left"/"final" qualifier —
  // simplified same-day from an earlier version that kept the qualifier,
  // per direct feedback that the points value alone already says "this
  // cell is done," same as a correct cell needs no "correct" label
  // alongside its own points. roundStatus no longer affects this branch's
  // output at all: the incorrect-lock value is the same known constant
  // regardless of *when* the cell locked, so there's nothing left here
  // that depends on whether the round itself is still active or closed.
  return (
    <div key={shakeToken} className={`cell-state cell-state--incorrect ${shakeClassName}`}>
      <Row correct={false} />
      <p className="cell-state__meta mono-figure">{MAX_POINTS_PER_CELL} pts</p>
    </div>
  );
}

function Row({
  name,
  correct,
  badges,
}: {
  // Absent for an incorrect guess (states 2/3) — no name is shown at all,
  // only that the guess was wrong. Also absent for a correct-but-unrevealed
  // cell (REQ-212). S-048: this component is no longer used at all for a
  // correct cell that has a photo (that branch renders its name/points
  // directly, with no checkmark) — Row now only ever renders for the
  // no-photo correct case and the two incorrect cases, never the photo one.
  name?: string;
  correct: boolean;
  badges?: { row: CategoryRef; col: CategoryRef };
}) {
  return (
    <div className="cell-state__row">
      {badges && (
        <span
          className="cell-state__badge-dock cell-state__badge-dock--row"
          data-testid="badge-dock-row"
          aria-hidden="true"
        >
          <CategoryGlyph categoryType={badges.row.categoryType} value={badges.row.value} size="small" />
        </span>
      )}
      {name && <span className="cell-state__name">{name}</span>}
      {badges && (
        <span
          className="cell-state__badge-dock cell-state__badge-dock--col"
          data-testid="badge-dock-col"
          aria-hidden="true"
        >
          <CategoryGlyph categoryType={badges.col.categoryType} value={badges.col.value} size="small" />
        </span>
      )}
      <span
        className={`cell-state__icon cell-state__icon--${correct ? 'correct' : 'incorrect'}`}
        aria-hidden="true"
      >
        {correct ? '✓' : '✕'}
      </span>
    </div>
  );
}

// REQ-214 (2026-07-18 status note): the resolved player's photo, filling
// the cell's full footprint at rest — superseding the prior shipped version
// (PR #79), which rendered a small 18px circular avatar nested inside the
// revealed name row via a component then called `PlayerAvatar`. That name
// and shape are both gone: this now renders full-bleed via
// `.cell-state__photo-img` (absolutely positioned by CellState.css,
// deliberately outside `.grid-cell`'s own padding box — see that
// stylesheet's comment for why that's the mechanism that keeps the cell's
// footprint fixed regardless of the photo, the same guarantee the old
// fixed-18px-box used to provide a different way). Renders unconditionally
// once mounted (CellState only mounts this when `hasPhoto` is true, i.e. a
// src was given and hasn't failed to load this session yet) — this
// component's own `onError` is what flips that state back to false one
// level up, causing CellState to fall back to exactly today's no-photo,
// no-scrim at-rest display on the next render, no broken-image icon ever
// shown in between. Decorative only (`alt=""`, `aria-hidden`) — whatever
// text is overlaid on top (the no-photo case's checkmark/points, or this
// photo case's name/points once revealed per S-048) already carries the
// accessible content, same pairing rule §6 applies to the flag/badge
// glyphs elsewhere in this file. At rest, a photo cell (S-048) overlays no
// text at all — the photo's own decorative alt="" is correct either way,
// since REQ-201's cell-level aria-label (GridCell.tsx) is what actually
// carries this cell's accessible name, not any text inside CellState.
function CellPhoto({ src, onError }: { src: string; onError: () => void }) {
  return <img className="cell-state__photo-img" src={src} alt="" aria-hidden="true" onError={onError} />;
}

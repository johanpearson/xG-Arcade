import { useEffect, useId, useRef, useState } from 'react';
import { MAX_ATTEMPTS_PER_CELL } from '../lib/guessRules';
import { CategoryGlyph } from './CategoryLabel';
import './CellState.css';

export type RoundStatus = 'active' | 'closed';

export interface CellStateProps {
  // The guessed/revealed player's name. GET /rounds/current does not return
  // this (only isCorrect/attemptCount/locked) — the real name is only known
  // client-side for a guess submitted in the current session. Falls back to
  // a plain, non-fabricated label rather than inventing a name; see the
  // frontend report for this gap flagged back for a follow-up REQ.
  playerName?: string;
  isCorrect: boolean;
  attemptCount: number;
  locked: boolean;
  // Explicit prop (not inferred) so this component can be exercised for
  // state 4 (round closed) via constructed props in tests, without wiring
  // up a live "closed round" flow that GET /rounds/current can't produce
  // today (S-011 scope).
  roundStatus: RoundStatus;
  // REQ-204: live unique_percent (0-1), re-derived on every request. Only
  // ever set when isCorrect — undefined/null means "not correct yet" or
  // (state 4) "not wired to a live source yet," never fabricated. S-019:
  // in state 1 this and the two fields below are only rendered once the
  // player reveals them (tap/long-press, or hover/focus on desktop) — see
  // LiveMetaDisclosure below; the text itself is unchanged, only when it
  // renders.
  uniquePercent?: number | null;
  // S-018 (REQ-204 extension): live, provisional point estimate for state 1
  // only — same round(uniqueScore * MaxPointsPerCell) formula REQ-205 locks
  // at round close, computed live and re-derived on every request. Rendered
  // with wording that marks it an estimate that can still change; never
  // reused for state 4's finalPoints, which is a separate, locked prop.
  livePoints?: number | null;
  // REQ-204's "updates until round closes on [date/time]" microcopy — only
  // rendered alongside uniquePercent, since it describes that value's
  // liveness, not the cell generally.
  roundEndTime?: string;
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
}

interface CategoryRef {
  categoryType: string;
  value: string;
}

// design-document.md §2: "Used only at guess-submit and round-close reveal,
// nowhere else." Both are *transitions* observed while this component stays
// mounted (the cell isn't remounted across a guess submission or a round
// closing) — never on first mount already in that state, e.g. a page
// reload showing a cell that was already correct/closed. Returns a token
// that increments once per qualifying transition; 0 means "never
// revealed," i.e. render the badges already docked, no animation.
function useRevealToken(isCorrect: boolean, roundStatus: RoundStatus): number {
  const prevRef = useRef({ isCorrect, roundStatus });
  const [token, setToken] = useState(0);

  useEffect(() => {
    const prev = prevRef.current;
    const justAnsweredCorrectly = isCorrect && !prev.isCorrect && roundStatus === 'active';
    const justClosedWhileCorrect =
      isCorrect && prev.roundStatus === 'active' && roundStatus === 'closed';
    if (justAnsweredCorrectly || justClosedWhileCorrect) {
      setToken((current) => current + 1);
    }
    prevRef.current = { isCorrect, roundStatus };
  }, [isCorrect, roundStatus]);

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
export function CellState({
  playerName,
  isCorrect,
  attemptCount,
  locked,
  roundStatus,
  uniquePercent,
  livePoints,
  roundEndTime,
  finalPoints,
  rowCategoryType,
  rowCategoryValue,
  colCategoryType,
  colCategoryValue,
  submittedThisSession = false,
}: CellStateProps) {
  const name = playerName ?? 'Guess submitted';
  const revealToken = useRevealToken(isCorrect, roundStatus);
  const shakeToken = useShakeToken(attemptCount, isCorrect, submittedThisSession);
  const badges =
    rowCategoryType != null && rowCategoryValue != null && colCategoryType != null && colCategoryValue != null
      ? {
          row: { categoryType: rowCategoryType, value: rowCategoryValue },
          col: { categoryType: colCategoryType, value: colCategoryValue },
        }
      : undefined;

  // State 4: round closed — either prior outcome, now permanent. No "live"
  // dot at all, regardless of correctness.
  if (roundStatus === 'closed') {
    return (
      // key={revealToken}: this same div also renders for a cell that was
      // already correct+live (state 1) before closing — reusing that DOM
      // node would carry over its already-played cell-state--reveal
      // animation instead of restarting it for *this* transition, so a
      // fresh reveal forces a real remount rather than a class toggle.
      <div
        key={revealToken}
        className={`cell-state cell-state--final cell-state--${isCorrect ? 'correct' : 'incorrect'} ${
          isCorrect && revealToken > 0 ? 'cell-state--reveal' : ''
        }`}
      >
        <Row name={name} correct={isCorrect} badges={isCorrect ? badges : undefined} />
        {isCorrect && uniquePercent != null && finalPoints != null && (
          <p className="cell-state__meta">
            {formatPercent(uniquePercent)}% unique · {finalPoints} pts
          </p>
        )}
        <p className="cell-state__meta">final</p>
      </div>
    );
  }

  // State 1: correct, round still active — locked from further guessing,
  // but still "live" until round close (REQ-203/204). S-019: the live
  // uniqueness/points/round-end text is disclosed on tap/long-press (or
  // hover/focus on desktop) rather than always shown, to cut the clutter of
  // every unresolved cell showing full live text at once — the quiet green
  // dot stays the permanent at-rest "still live" indicator either way.
  if (isCorrect) {
    return (
      // key={revealToken}: forces a remount (not just a class toggle) so the
      // slide-in/flash restarts on this specific guess-submit reveal, same
      // reasoning as the closed-state branch below.
      <div
        key={revealToken}
        className={`cell-state cell-state--live cell-state--correct ${
          revealToken > 0 ? 'cell-state--reveal' : ''
        }`}
      >
        <Row name={name} correct badges={badges} />
        {uniquePercent != null ? (
          <LiveMetaDisclosure uniquePercent={uniquePercent} livePoints={livePoints} roundEndTime={roundEndTime} />
        ) : (
          <p className="cell-state__meta">
            <span className="cell-state__live-dot" aria-hidden="true" />
            live
          </p>
        )}
      </div>
    );
  }

  const attemptsLeft = MAX_ATTEMPTS_PER_CELL - attemptCount;
  // key={shakeToken}: forces a remount so the shake/flash restarts on each
  // rejected guess, even if a prior rejection already left the same
  // className string in place (e.g. two rejections in a row while still in
  // state 2) — same technique useRevealToken's callers use above.
  const shakeClassName = shakeToken > 0 ? 'cell-state--shake' : '';

  // State 2: incorrect, at least one attempt remaining.
  if (!locked && attemptsLeft > 0) {
    return (
      <div key={shakeToken} className={`cell-state cell-state--incorrect ${shakeClassName}`}>
        <Row name={name} correct={false} />
        <p className="cell-state__meta">
          {attemptsLeft} attempt{attemptsLeft === 1 ? '' : 's'} left
        </p>
      </div>
    );
  }

  // State 3: incorrect, no attempts remaining (round still active). REQ-205/
  // 206 don't exist yet, so no "0 pts" line — same reasoning as state 1.
  return (
    <div key={shakeToken} className={`cell-state cell-state--incorrect cell-state--locked ${shakeClassName}`}>
      <Row name={name} correct={false} />
      <p className="cell-state__meta">no attempts left</p>
    </div>
  );
}

// REQ-204: uniquePercent arrives as a 0-1 fraction from the API.
function formatPercent(uniquePercent: number): number {
  return Math.round(uniquePercent * 100);
}

function formatDateTime(isoDateTime: string): string {
  return new Date(isoDateTime).toLocaleString(undefined, {
    weekday: 'short',
    hour: '2-digit',
    minute: '2-digit',
  });
}

// S-019 (REQ-204/SCREEN-01a redesign): the live uniqueness %/points/round-end
// text is disclosed only on tap/long-press (a tap toggles it open/closed,
// since touch has no hover) or hover/focus (the desktop-equivalent, transient
// peek — open while hovering/focused, closes again on mouseleave/blur). The
// quiet green live-dot and "live" text stay permanently visible either way,
// satisfying REQ-204's "always as text, never icon-only" rule for the
// at-rest state; the disclosed text is the same text as before, just not
// always rendered. `aria-expanded` on the toggle and `aria-live="polite"` on
// the revealed panel are what make the state change itself accessible to
// screen readers, not just sighted users.
//
// Three independent flags combine via OR rather than one shared boolean: a
// real mouse click fires a native `focus` event immediately before its
// `click` event, so a single toggle driven off one merged value would reveal
// (via focus) and then immediately hide again (via the click's own toggle)
// within the same physical click. Separating click/hover/focus into their
// own flags fixes that first-click case, but focus alone isn't quite right
// either: a mouse click leaves the button focused afterward, and if that
// lingering `focused` counted the same as a real keyboard tab, a *second*
// click could never close the panel (its own toggle would flip `toggledOpen`
// off, but `revealed` would stay true via `focused` regardless). `pointerDownRef`
// distinguishes the two: a mousedown immediately before focus means this
// focus is a side effect of a pointer click (already covered by `hovering`,
// since the pointer has to be over the button to click it) and is not
// counted; a focus with no preceding mousedown is a real keyboard tab, which
// still peeks like hover does.
function LiveMetaDisclosure({
  uniquePercent,
  livePoints,
  roundEndTime,
}: {
  uniquePercent: number;
  livePoints?: number | null;
  roundEndTime?: string;
}) {
  const [toggledOpen, setToggledOpen] = useState(false);
  const [hovering, setHovering] = useState(false);
  const [keyboardFocused, setKeyboardFocused] = useState(false);
  const pointerDownRef = useRef(false);
  const revealed = toggledOpen || hovering || keyboardFocused;
  const panelId = useId();

  return (
    <>
      <button
        type="button"
        className="cell-state__meta cell-state__reveal-toggle"
        aria-expanded={revealed}
        aria-controls={panelId}
        onClick={() => setToggledOpen((current) => !current)}
        onMouseDown={() => {
          pointerDownRef.current = true;
        }}
        onFocus={() => {
          if (!pointerDownRef.current) setKeyboardFocused(true);
          pointerDownRef.current = false;
        }}
        onBlur={() => {
          setKeyboardFocused(false);
          pointerDownRef.current = false;
        }}
        onMouseEnter={() => setHovering(true)}
        onMouseLeave={() => setHovering(false)}
      >
        <span className="cell-state__live-dot" aria-hidden="true" />
        live
      </button>
      {revealed && (
        <div id={panelId} aria-live="polite">
          <p className="cell-state__meta mono-figure">
            {formatPercent(uniquePercent)}% unique
            {livePoints != null && ` · ~${livePoints} pts estimated`}
          </p>
          {roundEndTime && (
            <p className="cell-state__meta cell-state__meta--muted">
              updates until round closes on {formatDateTime(roundEndTime)}
            </p>
          )}
        </div>
      )}
    </>
  );
}

function Row({
  name,
  correct,
  badges,
}: {
  name: string;
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
      <span className="cell-state__name">{name}</span>
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

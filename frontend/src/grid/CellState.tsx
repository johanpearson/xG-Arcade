import { useEffect, useId, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { MAX_ATTEMPTS_PER_CELL } from '../lib/guessRules';
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
  // in state 1 the player name and this value's %-breakdown text are only
  // rendered once the player reveals them (tap/long-press, or hover/focus
  // on desktop) — see useRevealDisclosure/RevealToggle below. S-040: the
  // same name-gating now also applies to state 4's correct-outcome branch,
  // and this being non-null is what decides whether a toggle exists at all
  // in either state (no toggle, name shown unconditionally, if it's null).
  uniquePercent?: number | null;
  // S-018 (REQ-204 extension): live, provisional point estimate for state 1
  // only — same round(uniqueScore * MaxPointsPerCell) formula REQ-205 locks
  // at round close, computed live and re-derived on every request. S-040:
  // now always-visible at rest (moved off the reveal toggle), and still
  // repeated unchanged inside the revealed %-breakdown text — deliberate
  // duplication, not a copy bug. Never reused for state 4's finalPoints,
  // which is a separate, locked prop.
  livePoints?: number | null;
  // REQ-204's "updates until round closes on [date/time]" microcopy — only
  // rendered alongside uniquePercent, since it describes that value's
  // liveness, not the cell generally. Still gated behind reveal (S-019),
  // unaffected by S-040.
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
  // S-040: one shared disclosure per mounted cell — only ever consulted by
  // whichever branch below actually has something to disclose (state 1, or
  // state 4's correct-outcome half); called unconditionally here regardless,
  // same "always call, conditionally use" pattern as the two hooks above.
  const disclosure = useRevealDisclosure();
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
    // S-040: a toggle only exists once there's real data to disclose (same
    // rule state 1 already followed) — without it, the name shows
    // unconditionally, same as this branch's pre-S-040 behavior.
    const hasFinalMeta = isCorrect && uniquePercent != null && finalPoints != null;
    const revealName = isCorrect && (!hasFinalMeta || disclosure.revealed);
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
        <Row name={revealName ? name : undefined} correct={isCorrect} badges={revealName ? badges : undefined} />
        {hasFinalMeta ? (
          <RevealToggle
            revealed={disclosure.revealed}
            toggleHandlers={disclosure.toggleHandlers}
            atRest={
              <>
                <span>{finalPoints} pts</span>
                <span aria-hidden="true">·</span>
                <span>final</span>
              </>
            }
          >
            <p className="cell-state__meta mono-figure">
              {formatOthersGuessedPercent(uniquePercent)}% of others guessed this too · {finalPoints} pts
            </p>
          </RevealToggle>
        ) : (
          <p className="cell-state__meta">final</p>
        )}
      </div>
    );
  }

  // State 1: correct, round still active — locked from further guessing,
  // but still "live" until round close (REQ-203/204). S-019: the live
  // uniqueness/points/round-end text is disclosed on tap/long-press (or
  // hover/focus on desktop) rather than always shown, to cut the clutter of
  // every unresolved cell showing full live text at once. S-040: the same
  // toggle now also gates the player name (and its badge dock, since the
  // badge dock only makes sense docked beside a visible name) — the quiet
  // green dot and "live" text stay the permanent at-rest indicator either
  // way, joined now by the live point estimate (moved off the toggle,
  // always-visible).
  if (isCorrect) {
    const hasLiveMeta = uniquePercent != null;
    const revealName = !hasLiveMeta || disclosure.revealed;
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
        <Row name={revealName ? name : undefined} correct badges={revealName ? badges : undefined} />
        {hasLiveMeta ? (
          <RevealToggle
            revealed={disclosure.revealed}
            toggleHandlers={disclosure.toggleHandlers}
            atRest={
              <>
                <span className="cell-state__live-dot" aria-hidden="true" />
                <span>live</span>
                {livePoints != null && (
                  <>
                    <span aria-hidden="true">·</span>
                    <span>{`~${livePoints} pts estimated`}</span>
                  </>
                )}
              </>
            }
          >
            <p className="cell-state__meta mono-figure">
              {formatOthersGuessedPercent(uniquePercent)}% of others guessed this too
              {livePoints != null && ` · ~${livePoints} pts estimated`}
            </p>
            {roundEndTime && (
              <p className="cell-state__meta cell-state__meta--muted">
                updates until round closes on {formatDateTime(roundEndTime)}
              </p>
            )}
          </RevealToggle>
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

  // State 3: incorrect, no attempts remaining (round still active). REQ-205/
  // 206 don't exist yet, so no "0 pts" line — same reasoning as state 1.
  return (
    <div key={shakeToken} className={`cell-state cell-state--incorrect cell-state--locked ${shakeClassName}`}>
      <Row correct={false} />
      <p className="cell-state__meta">no attempts left</p>
    </div>
  );
}

// REQ-204: uniquePercent arrives as a 0-1 fraction from the API, and is
// framed here as its complement — "how many other correct guessers also
// picked this answer" — rather than "how unique is this answer." Same
// number, reworded: a player-feedback pass found "X% unique" confusing
// once paired with ADR-0021's golf-style points (a *higher* uniqueness
// percentage means *fewer* points, the opposite of what "unique" suggests).
// "N% of others also guessed this" moves in the same direction as the point
// value instead (more people guessing the same answer = more common = more
// points, worse under golf scoring) — no formula changed, only the wording.
function formatOthersGuessedPercent(uniquePercent: number): number {
  return Math.round((1 - uniquePercent) * 100);
}

function formatDateTime(isoDateTime: string): string {
  return new Date(isoDateTime).toLocaleString(undefined, {
    weekday: 'short',
    hour: '2-digit',
    minute: '2-digit',
  });
}

// S-019 (REQ-204/SCREEN-01a redesign), extended by S-040 to also gate state
// 1's player name and to add the equivalent toggle to state 4: content is
// disclosed only on tap/long-press (a tap toggles it open/closed, since
// touch has no hover) or hover/focus (the desktop-equivalent, transient peek
// — open while hovering/focused, closes again on mouseleave/blur).
// `aria-expanded` on the toggle and `aria-live="polite"` on the revealed
// panel are what make the state change itself accessible to screen readers,
// not just sighted users.
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
//
// `hoverSuppressed` fixes an analogous second bug on the mouse path: the
// pointer is still resting over the button immediately after a click (it
// never moved), so `hovering` stays true through the whole click and a
// second click that flips `toggledOpen` back to false had no visible effect
// — `revealed` stayed true via `hovering` regardless, so a mouse user could
// never close the panel by clicking again, only by moving the mouse away.
// When a click closes the panel while still hovering, hover's peek is
// suppressed until the pointer actually leaves and re-enters, so the click
// close reliably sticks. `keyboardSuppressed` is the identical fix for the
// keyboard path: pressing Enter/Space to activate the toggle's own `onClick`
// does not blur the button, so `keyboardFocused` stays true through a
// keyboard-driven close too — without this, a keyboard/screen-reader user
// could never close the panel via the toggle at all (only by tabbing away),
// and pressing Enter an odd number of times before tabbing away would leave
// `toggledOpen` stuck true with no way to notice or undo it without
// revisiting the cell.
function useRevealDisclosure(): {
  revealed: boolean;
  toggleHandlers: {
    onClick: () => void;
    onMouseDown: () => void;
    onFocus: () => void;
    onBlur: () => void;
    onMouseEnter: () => void;
    onMouseLeave: () => void;
  };
} {
  const [toggledOpen, setToggledOpen] = useState(false);
  const [hovering, setHovering] = useState(false);
  const [hoverSuppressed, setHoverSuppressed] = useState(false);
  const [keyboardFocused, setKeyboardFocused] = useState(false);
  const [keyboardSuppressed, setKeyboardSuppressed] = useState(false);
  const pointerDownRef = useRef(false);
  const revealed =
    toggledOpen || (hovering && !hoverSuppressed) || (keyboardFocused && !keyboardSuppressed);

  return {
    revealed,
    toggleHandlers: {
      onClick: () =>
        setToggledOpen((current) => {
          const next = !current;
          if (!next) {
            if (hovering) setHoverSuppressed(true);
            if (keyboardFocused) setKeyboardSuppressed(true);
          }
          return next;
        }),
      onMouseDown: () => {
        pointerDownRef.current = true;
      },
      onFocus: () => {
        if (!pointerDownRef.current) setKeyboardFocused(true);
        pointerDownRef.current = false;
      },
      onBlur: () => {
        setKeyboardFocused(false);
        setKeyboardSuppressed(false);
        pointerDownRef.current = false;
      },
      onMouseEnter: () => setHovering(true),
      onMouseLeave: () => {
        setHovering(false);
        setHoverSuppressed(false);
      },
    },
  };
}

// S-040: the toggle markup shared by state 1 (live meta + name, extended
// from S-019) and state 4's correct-outcome branch (new) — `atRest` is the
// always-visible button content (never empty, satisfying REQ-204's "always
// as text, never icon-only" rule), `children` is the on-demand panel.
// Callers pass the same `revealed`/`toggleHandlers` pair they also use to
// gate the player name/badge dock elsewhere in their own markup, so the name
// and this panel open and close in lockstep.
function RevealToggle({
  revealed,
  toggleHandlers,
  atRest,
  children,
}: {
  revealed: boolean;
  toggleHandlers: ReturnType<typeof useRevealDisclosure>['toggleHandlers'];
  atRest: ReactNode;
  children: ReactNode;
}) {
  const panelId = useId();

  return (
    <>
      <button
        type="button"
        className="cell-state__meta cell-state__reveal-toggle"
        aria-expanded={revealed}
        aria-controls={panelId}
        {...toggleHandlers}
      >
        {atRest}
      </button>
      {revealed && (
        <div id={panelId} aria-live="polite">
          {children}
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
  // Absent for an incorrect guess (states 2/3) — no name is shown at all,
  // only that the guess was wrong.
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

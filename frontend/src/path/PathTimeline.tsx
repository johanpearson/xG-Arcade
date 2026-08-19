import { useState } from 'react';
import { CategoryGlyph } from '../components/CategoryLabel';
import type { PathClueTurn } from '../lib/types';
import './PathTimeline.css';

export interface PathTimelineProps {
  clues: PathClueTurn[];
  solved: boolean;
  // User-testing fix (2026-08-02): required (not optional/defaulted), same
  // as `solved` above — PathScreen.tsx always has a real value for this
  // (`puzzle.guess?.locked ?? false`), and a required prop makes every call
  // site (including tests) state its intent explicitly rather than silently
  // falling back to "not locked." True whenever a puzzle can no longer be
  // guessed at all — either solved (isCorrect) or locked-unsolved (the
  // fixed attempt cap was exhausted without a correct guess, REQ-1205). Used
  // to distinguish "solved" (gold reveal) from "locked but never solved"
  // (a distinct, non-gold reveal — see FailedRevealNode below) — until now
  // this second case rendered nothing at all beyond the last real clue,
  // which is the bug this prop exists to fix.
  locked: boolean;
  resolvedPlayerName?: string | null;
  resolvedPlayerPhotoUrl?: string | null;
  // REQ-1206 (2026-08-08 addition): the puzzle's locked point value —
  // present alongside resolvedPlayerName/resolvedPlayerPhotoUrl the moment
  // the puzzle locks (solved, or the 7-attempt cap exhausted unsolved),
  // never before. See CurrentPathGuess.points (lib/types.ts) for why this is
  // never rendered with "~"/"estimated" wording, unlike xG Grid's
  // livePoints.
  points?: number | null;
}

// REQ-1203/SCREEN-10 (S-086): plain-language label for each of the three
// fixed one-at-a-time clue kinds — ClubReveal/YearRange render their own
// structured content instead (see renderClueContent below), so they never
// need one of these.
// Bug fix (2026-08-02, bug-bundle): the "Age" kind's own value has always
// been a birth year, never a computed age (PathClueSequenceBuilder's own doc
// comment on the backend: rendering an actual age would need a
// TimeProvider/"now" dependency this pure builder deliberately avoids) — the
// display label just never caught up to match, so the UI showed e.g.
// "Age: 1980" for what is really a birth year. Only the label changes here;
// the wire-level PathClueKind "Age" identifier is untouched (an internal
// contract, not user-facing).
const TEXT_CLUE_LABELS: Record<'Position' | 'Nationality' | 'Age', string> = {
  Position: 'Position',
  Nationality: 'Nationality',
  Age: 'Birth year',
};

// design-document.md SCREEN-10: "the literal career path being drawn as
// it's revealed, one node per clue, oldest at top. Every past clue stays
// visible." Clues only ever grow (a turn, once revealed, is never removed
// or reordered — PathClueSequenceBuilder's TurnNumber is stable) so each
// node is keyed by its own turnNumber and mounts exactly once; a CSS
// animation applied at that one mount naturally never replays on a later
// re-render, unlike CellState's badge-dock reveal (which needed an explicit
// replay token because that element toggles visible/hidden repeatedly, not
// append-only).
//
// Bug fix (2026-08-03, user-tester report): the solved/failed reveal used to
// REPLACE the last real clue turn's own content (the old isLastNode branch
// rendered SolvedNode/FailedRevealNode *instead of* renderClueContent) — for
// a single-club turn that's a small cosmetic swap, but the same turn can
// carry several bundled clubs (PathClueSequenceBuilder's 3-3-4 split for a
// long career) or the bundled year-range/position/nationality/age content,
// and replacing it wholesale silently deleted that entire turn's real
// content the instant the puzzle locked — directly contradicting this
// file's own "every past clue stays visible" doc comment above, and exactly
// what the tester reported as "the latest shown clue was removed upon
// correct answer." The reveal is now its own trailing node, appended after
// every real clue turn rather than displacing one.
export function PathTimeline({
  clues,
  solved,
  locked,
  resolvedPlayerName,
  resolvedPlayerPhotoUrl,
  points,
}: PathTimelineProps) {
  // REQ-1203: the bundled year-range turn's own payload is just a list of
  // strings, in the same order every club was revealed across the 3
  // preceding ClubReveal turns combined (PathClueTurn's own backend doc
  // comment) — this screen pairs each range back up with its club's name by
  // that shared index, since the wireframe shows them together
  // ("Ajax 2001–04 · Juventus 2004–06 · …"), not as bare, unlabeled years.
  const revealedClubNames = clues
    .filter((turn) => turn.kind === 'ClubReveal')
    .flatMap((turn) => turn.clubs ?? [])
    .map((club) => club.clubName);

  // User-testing fix (2026-08-02, unchanged by this fix): a puzzle that
  // locked *without* ever being solved (the fixed attempt cap ran out,
  // REQ-1205) used to show nothing beyond the last real clue — no reveal at
  // all, leaving the player never told what the answer was. `locked` covers
  // both "solved" and "locked-unsolved"; `isFailedReveal` is specifically
  // the second case (locked is true, solved is false), rendered as a
  // clearly distinct, non-gold node — see FailedRevealNode below and its own
  // design-document.md SCREEN-10 status note.
  const isSolvedReveal = solved;
  const isFailedReveal = locked && !solved;

  return (
    <ol className="path-timeline" aria-label="Revealed clues, oldest first">
      {clues.map((turn) => (
        <li
          key={turn.turnNumber}
          // Quality-gate fix (S-086 follow-up): the settle-in animation
          // class is now applied unconditionally — `prefers-reduced-motion`
          // is handled entirely by PathTimeline.css's own `@media` override
          // (which sets `animation: none`, fully cancelling the animation,
          // same end result as never applying the class), matching
          // CellState.css's own CSS-only reduced-motion pattern rather than
          // duplicating that logic in JS. See PathTimeline.css's comment on
          // this rule for the full reasoning.
          className="path-timeline__node path-timeline__node--animate-in"
        >
          <span className="path-timeline__dot" aria-hidden="true" />
          <div className="path-timeline__content">{renderClueContent(turn, revealedClubNames)}</div>
        </li>
      ))}
      {(isSolvedReveal || isFailedReveal) && (
        <li
          key="reveal"
          className={`path-timeline__node ${isSolvedReveal ? 'path-timeline__node--solved' : 'path-timeline__node--failed'} path-timeline__node--animate-in`}
        >
          <span className="path-timeline__dot" aria-hidden="true" />
          <div className="path-timeline__content">
            {isSolvedReveal ? (
              <SolvedNode name={resolvedPlayerName} photoUrl={resolvedPlayerPhotoUrl} points={points} />
            ) : (
              <FailedRevealNode name={resolvedPlayerName} photoUrl={resolvedPlayerPhotoUrl} points={points} />
            )}
          </div>
        </li>
      )}
    </ol>
  );
}

function renderClueContent(turn: PathClueTurn, revealedClubNames: string[]) {
  if (turn.kind === 'ClubReveal') {
    // A plain <div>/<div> pairing, not a nested <ul>/<li> — this timeline's
    // own outer <ol>/<li> (below) already owns the "list of clue turns"
    // semantics; a second, nested list here would give every club its own
    // (misleading, and ambiguous for testing-library's role queries)
    // "listitem" role for what is really just this one turn's content.
    return (
      <div className="path-timeline__clubs">
        {(turn.clubs ?? []).map((club) => (
          <div key={club.clubName} className="path-timeline__club">
            <CategoryGlyph categoryType="club" value={club.clubName} size="small" />
            <span className="path-timeline__club-name">{club.clubName}</span>
            {/* S-163: a presentation-only heuristic ("probably a loan," inferred
                from date-range containment, not a sourced fact — see
                PathClubClue.isLoan's own doc comment in lib/types.ts) — reuses
                the existing muted-secondary-text treatment
                (.path-timeline__club-apps' own --color-text-muted/12px rule)
                rather than introducing a new badge color/weight, per
                design-document.md §2's "no ad-hoc token" rule. */}
            {club.isLoan && <span className="path-timeline__club-apps">(loan)</span>}
            {club.appearanceCount != null && (
              <span className="path-timeline__club-apps mono-figure">{club.appearanceCount} apps</span>
            )}
          </div>
        ))}
      </div>
    );
  }

  if (turn.kind === 'YearRange') {
    const ranges = turn.yearRanges ?? [];
    // User-testing fix (2026-08-02): this used to join every club's year
    // range into one inline paragraph with " · " separators (e.g. "Paris
    // Saint-Germain 2017-19 · Lille 2019-23 · Juventus 2023-present ·
    // Marseille 2025-present"), which read as a dense, hard-to-scan block
    // once it wrapped on mobile — confirmed against a real screenshot from
    // testing. Each club/year-range pair now gets its own line instead — a
    // plain stacked block (not a nested <ul>/<li>, for the same reason
    // ClubReveal's own block above isn't one: this turn's content lives
    // inside the outer <ol>/<li>'s own single listitem, and a second nested
    // list would give every range its own misleading "listitem" role). Same
    // club-name-paired-with-range content and revealedClubNames[index]
    // pairing logic as before — only the layout (inline-joined → one row
    // per club) changed.
    return (
      <div className="path-timeline__year-ranges">
        {ranges.map((range, index) => {
          const clubName = revealedClubNames[index];
          const label = clubName ? `${clubName} ${range}` : range;
          return (
            <p key={`${clubName ?? 'range'}-${range}`} className="path-timeline__year-range">
              {label}
            </p>
          );
        })}
      </div>
    );
  }

  // Position / Nationality / Age: one plain "Label: value" turn each, in
  // that fixed order — REQ-1207's "not available" contract renders exactly
  // as sent, never a skipped turn (PathEndpoints already guarantees
  // turn.textValue is never itself null for these kinds).
  const label = TEXT_CLUE_LABELS[turn.kind] ?? turn.kind;
  return (
    <p className="path-timeline__text-clue">
      <span className="path-timeline__text-clue-label">{label}:</span> {turn.textValue}
    </p>
  );
}

// design-document.md SCREEN-10: "the final node turns gold … and shows the
// target player's name plus, when Player.PhotoUrl is set, their photo …
// falling back to the same initials-avatar treatment REQ-214 already
// established for a player with no photo on file." As of S-048
// (design-document.md SCREEN-01a's own status note / REQ-214's matching
// note), xG Grid's no-photo fallback is no longer a circular-initials
// avatar at all — that treatment was deliberately removed in favor of
// plain text, with no avatar element of any kind. There is nothing left in
// the current codebase to "reuse as-is" for an initials avatar, so this
// renders the same text-only fallback REQ-214 actually has today rather
// than inventing a new avatar component this story was never asked to
// design — flagged back to design-document.md's SCREEN-10 section as a
// stale reference, not silently resolved.
function SolvedNode({
  name,
  photoUrl,
  points,
}: {
  name?: string | null;
  photoUrl?: string | null;
  points?: number | null;
}) {
  // Quality-gate fix (S-086 follow-up): same same-session image-load-failure
  // fallback as CellState.tsx's `CellPhoto`/`photoFailed` — a photo URL that
  // 404s or otherwise fails to load falls back to the text-only treatment
  // (no image at all) rather than the browser's own broken-image icon.
  const [photoFailed, setPhotoFailed] = useState(false);
  const hasPhoto = Boolean(photoUrl) && !photoFailed;

  return (
    <div className="path-timeline__solved">
      <p className="path-timeline__solved-label">
        <span aria-hidden="true">✓</span> Solved
      </p>
      {hasPhoto && (
        <img
          className="path-timeline__solved-photo"
          src={photoUrl as string}
          alt=""
          aria-hidden="true"
          onError={() => setPhotoFailed(true)}
        />
      )}
      <p className="path-timeline__solved-name">{name ?? 'Puzzle solved'}</p>
      {/* REQ-1206: plain, final wording ("N pts") — never "~N pts
          estimated" (that's xG Grid's genuinely-provisional livePoints
          convention; this value can't change once the puzzle locks). Same
          `mono-figure` numerals-use-mono-face treatment every other
          score/count in this app already uses (e.g.
          `cell-state__meta mono-figure` in CellState.tsx). Defensive
          `points != null` guard, same pattern as every other optional field
          on this node — an older cached response predating REQ-1206 simply
          shows no points line, not a broken "null pts". */}
      {points != null && <p className="path-timeline__points mono-figure">{points} pts</p>}
    </div>
  );
}

// User-testing fix (2026-08-02, design-document.md SCREEN-10 status note
// added the same day): a puzzle that locks unsolved (REQ-1205's fixed
// attempt cap exhausted without a correct guess) previously showed nothing
// beyond the last real clue — no reveal of what the answer actually was.
// This is that reveal, deliberately NOT styled as SolvedNode's gold "✓
// Solved" treatment (§2 "gold means settled/correct" — this player did not
// get it right, and reusing the correct-state color here would misleadingly
// imply they did). Reuses SolvedNode's structure (same photo-with-
// fallback-on-load-error logic, same name line) since the *content* being
// shown is identical — a player name and optional photo — only the
// semantic outcome differs. Styled with `accent-red` (§2: "incorrect
// states"), the same token CellState.css's own incorrect cell state already
// uses directly for text/icon color (accent-red passes text contrast as-is,
// ~4.9:1 on white — no darkened `-text` variant needed, unlike
// accent-gold/accent-green). Copy: "Out of attempts" states plainly what
// happened (§5: errors/end-states state what happened, no apology, no
// hedging); the name line omits an "It was" preamble to match SolvedNode's
// own plain-name treatment exactly, since the red "Out of attempts" label
// immediately above it already supplies the "this is the answer, and you
// didn't get it" framing without needing to repeat it in the name line too.
function FailedRevealNode({
  name,
  photoUrl,
  points,
}: {
  name?: string | null;
  photoUrl?: string | null;
  points?: number | null;
}) {
  const [photoFailed, setPhotoFailed] = useState(false);
  const hasPhoto = Boolean(photoUrl) && !photoFailed;

  return (
    <div className="path-timeline__failed">
      <p className="path-timeline__failed-label">
        <span aria-hidden="true">✕</span> Out of attempts
      </p>
      {hasPhoto && (
        <img
          className="path-timeline__failed-photo"
          src={photoUrl as string}
          alt=""
          aria-hidden="true"
          onError={() => setPhotoFailed(true)}
        />
      )}
      {/* Defensive, not a stopgap (2026-08-02): PathEndpoints.cs populates
          resolvedPlayerName/resolvedPlayerPhotoUrl whenever the puzzle is
          Locked (solved or attempt-cap exhausted), not only when IsCorrect
          — but this component still never assumes the field is present.
          `name` can legitimately be null here (e.g. the target player's
          FullName lookup itself came back empty), so this renders a correct
          "Out of attempts" node with no dangling "It was null" text rather
          than trusting the backend contract blindly. */}
      {name && <p className="path-timeline__failed-name">{name}</p>}
      {/* REQ-1206: an exhausted-unsolved puzzle still returns a real Points
          value (the worst-case ScoringRules.MaxPointsPerCell, per
          PathEndpoints.cs) — shown here with the same plain "N pts" wording
          and mono-figure treatment as SolvedNode's own points line, no
          "final"/celebratory framing either way (lower is better,
          ADR-0021's golf-scoring convention — this is a plain fact, not an
          achievement). */}
      {points != null && <p className="path-timeline__points mono-figure">{points} pts</p>}
    </div>
  );
}

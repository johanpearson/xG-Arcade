import { CategoryGlyph } from '../grid/CategoryLabel';
import { usePrefersReducedMotion } from '../lib/motion';
import type { PathClueTurn } from '../lib/types';
import './PathTimeline.css';

export interface PathTimelineProps {
  clues: PathClueTurn[];
  solved: boolean;
  resolvedPlayerName?: string | null;
  resolvedPlayerPhotoUrl?: string | null;
}

// REQ-1203/SCREEN-10 (S-086): plain-language label for each of the three
// fixed one-at-a-time clue kinds — ClubReveal/YearRange render their own
// structured content instead (see renderClueContent below), so they never
// need one of these.
const TEXT_CLUE_LABELS: Record<'Position' | 'Nationality' | 'Age', string> = {
  Position: 'Position',
  Nationality: 'Nationality',
  Age: 'Age',
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
export function PathTimeline({ clues, solved, resolvedPlayerName, resolvedPlayerPhotoUrl }: PathTimelineProps) {
  const reducedMotion = usePrefersReducedMotion();
  const lastIndex = clues.length - 1;

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

  return (
    <ol className="path-timeline" aria-label="Revealed clues, oldest first">
      {clues.map((turn, index) => {
        const isFinal = solved && index === lastIndex;
        return (
          <li
            key={turn.turnNumber}
            className={`path-timeline__node ${isFinal ? 'path-timeline__node--solved' : ''} ${
              !reducedMotion ? 'path-timeline__node--animate-in' : ''
            }`}
          >
            <span className="path-timeline__dot" aria-hidden="true" />
            <div className="path-timeline__content">
              {isFinal ? (
                <SolvedNode name={resolvedPlayerName} photoUrl={resolvedPlayerPhotoUrl} />
              ) : (
                renderClueContent(turn, revealedClubNames)
              )}
            </div>
          </li>
        );
      })}
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
    return (
      <p className="path-timeline__year-ranges">
        {ranges.map((range, index) => {
          const clubName = revealedClubNames[index];
          const label = clubName ? `${clubName} ${range}` : range;
          return (
            <span key={`${clubName ?? 'range'}-${range}`} className="path-timeline__year-range">
              {index > 0 && <span aria-hidden="true"> · </span>}
              {label}
            </span>
          );
        })}
      </p>
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
function SolvedNode({ name, photoUrl }: { name?: string | null; photoUrl?: string | null }) {
  return (
    <div className="path-timeline__solved">
      <p className="path-timeline__solved-label">
        <span aria-hidden="true">✓</span> Solved
      </p>
      {photoUrl && <img className="path-timeline__solved-photo" src={photoUrl} alt="" aria-hidden="true" />}
      <p className="path-timeline__solved-name">{name ?? 'Puzzle solved'}</p>
    </div>
  );
}

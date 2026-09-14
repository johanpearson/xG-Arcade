// REQ-1203 (S-086): one club revealed within a ClubReveal turn — mirrors
// `PathClubClueResponse` (backend/src/XGArcade.Api/Path/PathEndpoints.cs).
// appearanceCount is null exactly when Wikidata's appearance-count
// qualifier wasn't recorded for that stint — the club is still shown,
// without a count, never delayed/omitted and never a fabricated "0 apps".
// isLoan (S-163, 2026-08-19 addition): a presentation-only heuristic flag —
// true when `PathCareerStintFilter.IsInferredLoan` found this stint's
// date range fully nested inside a different, concurrent club's stint
// (e.g. Beckham's 1994-95 Preston North End loan, nested inside his
// 1992-2003 Man Utd stint). A deliberate, explicitly-imprecise inference
// (no Wikidata "on loan from" property is read), never a factual/sourced
// claim — has no effect on eligibility or scoring, purely changes how the
// club-reveal clue is labeled. See docs/backlog.md S-163 and its ADR.
export interface PathClubClue {
  clubName: string;
  appearanceCount: number | null;
  isLoan: boolean;
}

// REQ-1203 (S-086): one turn of the fixed 7-turn clue-reveal sequence —
// mirrors `PathClueTurnResponse` exactly. `kind` is the backend's
// `PathClueKind` enum serialized as its name ("ClubReveal" | "YearRange" |
// "Position" | "Nationality" | "Age") — declared here as a literal union,
// not a plain string. Unlike `CategoryType` (types.ts's own top-of-file
// note), which is a plain string because *which* axis is country vs. club
// is derived dynamically and isn't a fixed set, `PathClueKind` is a closed,
// backend-fixed set of five turn kinds — a literal union is more type-safe
// here and nothing in this codebase depends on forward-compat string
// behavior for an unrecognized value. (`PathTimeline`'s render switch still
// falls back to a generic text-clue rendering for any value that isn't
// `ClubReveal`/`YearRange`, so an unrecognized kind wouldn't crash even if
// the backend ever sent one outside this union — but that's a defensive
// runtime fallback, not something this type intentionally allows.)
// Exactly one of clubs/yearRanges/textValue is non-null per turn, selected
// by kind — see PathClueTurn's own backend doc comment for which.
export type PathClueKind = 'ClubReveal' | 'YearRange' | 'Position' | 'Nationality' | 'Age';

export interface PathClueTurn {
  turnNumber: number;
  kind: PathClueKind;
  clubs: PathClubClue[] | null;
  yearRanges: string[] | null;
  textValue: string | null;
}

// REQ-1204 (S-086): mirrors `CurrentPathGuessResponse` exactly — same
// only-when-isCorrect rule for resolvedPlayerName/resolvedPlayerPhotoUrl as
// CurrentRoundGuess (an incorrect or in-progress guess never reveals
// the target player's identity).
export interface CurrentPathGuess {
  isCorrect: boolean;
  attemptCount: number;
  locked: boolean;
  submittedName: string;
  resolvedPlayerName: string | null;
  resolvedPlayerPhotoUrl: string | null;
  // REQ-1206 (2026-08-08 frontend addition): non-null only when `locked` is
  // true (solved, or the 7-attempt cap exhausted unsolved — REQ-1205).
  // Mirrors `CurrentPathGuessResponse.Points` exactly. Deliberately NOT the
  // same shape/wording as CurrentRoundGuess.livePoints: livePoints is
  // genuinely provisional (it depends on how many other players have also
  // solved the same cell, which can keep growing until round close).
  // ClueEfficiencyScoringStrategy's formula has no such dependency — both
  // inputs (cluesUsed, the fixed 7-clue cap) are fully determined the
  // instant a puzzle locks and never change afterward — so this value is
  // arithmetically identical to what the leaderboard will eventually show
  // as FinalPoints, not an estimate. Render it with plain "N pts" wording
  // (PathTimeline.tsx), never "~"/"estimated"/"provisional" — see
  // REQ-1206's "Important asymmetry from REQ-204's LivePoints" note in
  // requirements-document.md.
  points: number | null;
}

// REQ-1203 (S-086): mirrors `CurrentPathPuzzleResponse` exactly. `clues` is
// only ever the turns unlocked so far for the requesting player — this array
// growing (via a re-fetch of GET /path/current after each guess) IS the
// "revealed so far" state; there is no separate reveal endpoint.
export interface CurrentPathPuzzle {
  puzzleId: string;
  clues: PathClueTurn[];
  guess: CurrentPathGuess | null;
}

// REQ-1201/1202 (S-086): mirrors `CurrentPathResponse` exactly — the active
// xg-path round's whole puzzle list at once, same shape/auth/404-as-empty
// idiom as CurrentRoundResponse.
export interface CurrentPathResponse {
  roundId: string;
  // REQ-304: see CurrentRoundResponse.sequenceNumber — same
  // display-only, per-GameKey ("xg-path") round number.
  sequenceNumber: number;
  startTime: string;
  endTime: string;
  allowGuessChange: boolean;
  puzzles: CurrentPathPuzzle[];
}

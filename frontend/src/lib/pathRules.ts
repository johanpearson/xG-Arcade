// REQ-1205: every xG Path puzzle has a fixed attempt/clue cap of 7 — never
// xG Grid's MAX_ATTEMPTS_PER_CELL (frontend/src/lib/guessRules.ts, a
// different game's different fixed value). Mirrors
// backend/src/XGArcade.Games.XGPath/PathClueSequenceBuilder.cs's
// `TotalTurns` constant — duplicated here only for copy ("Clue N of M"),
// never for enforcement; the server (`GetMaxAttemptsForCellAsync`) is always
// the source of truth for whether a guess is actually accepted, same
// division of responsibility guessRules.ts's own doc comment describes for
// xG Grid.
export const MAX_CLUES_PER_PUZZLE = 7;

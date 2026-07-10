// REQ-210: two guesses per cell, locked immediately on a correct answer.
// Mirrors backend/src/XGArcade.Core/Scoring/GuessRules.cs's
// MaxAttemptsPerCell — duplicated here only for copy ("N attempts left"),
// never for enforcement. The server is always the source of truth for
// whether a guess is actually accepted; this constant only decides what
// text to show.
export const MAX_ATTEMPTS_PER_CELL = 2;

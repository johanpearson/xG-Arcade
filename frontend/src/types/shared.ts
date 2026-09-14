// REQ-207/ADR-0007 (S-032): a suggestion sourced from PlayerNameIndex
// (COMP-10) only — a name appearing here implies nothing about whether
// it's correct for the current cell. Never merge this shape/path with
// PlayerAttribute/PlayerOverride correctness data (ADR-0007's boundary
// rule). birthYear is optional disambiguation context only (e.g. two
// players sharing a name), not a correctness signal, and must never be
// styled to suggest one is "more right" than another. `nationality` was
// removed from this shape (and the API response) entirely — unlike
// birthYear, it can directly leak the answer for nationality-based xG
// Grid categories (e.g. Country × Club), since seeing which suggestions
// carry the target nationality tells the player who's eligible before
// they even guess.
//
// wikidataQid (bug fix, 2026-09-05, ADR-0107): unlike nationality, not a
// correctness signal for any xG Grid category — an opaque id. Lets xG
// Connect's target-pick/chain-step screens submit the exact real person a
// suggestion represents, rather than a bare name that can't tell two
// different real people sharing a name apart (a real, reported incident:
// two different real footballers both named "Jonas Olsson"). Absent/null
// for a suggestion indexed before this column existed — callers must treat
// that as "submit by name instead," never assume it's always present.
export interface PlayerAutocompleteSuggestion {
  playerId: string;
  name: string;
  birthYear?: number;
  wikidataQid?: string | null;
}

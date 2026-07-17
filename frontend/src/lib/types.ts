// Shapes mirror the API DTOs exactly (see backend/src/XGArcade.Api/Rounds/RoundEndpoints.cs
// and Guesses/GuessEndpoints.cs) — kept as plain types at the boundary per
// coding-guidelines.md's "props explicitly typed, never any."

// REQ-107: one axis is always country, the other always club — but which
// axis is which is derived from the API's actual *CategoryType fields, never
// hardcoded, so this is a plain string, not a fixed union of two literals.
export type CategoryType = string;

export interface CurrentRoundGuess {
  isCorrect: boolean;
  attemptCount: number;
  locked: boolean;
  submittedName: string;
  // Frontend name-display fix: the canonical, properly-cased Player.FullName
  // for a correct guess — null whenever isCorrect is false (an incorrect
  // guess shows no name at all, only that it was wrong) or, defensively, if
  // it somehow can't be resolved. Never a substitute for submittedName,
  // which stays the raw as-typed text unaffected.
  resolvedPlayerName: string | null;
  // REQ-204: null until the guess is correct — re-derived on every request,
  // not persisted, until the round closes.
  uniquePercent: number | null;
  // S-018 (REQ-204 extension): null until the guess is correct, recomputed
  // on every request from uniquePercent via the same round(uniqueScore *
  // MaxPointsPerCell) formula REQ-205 locks at round close — an estimate
  // that can still change, never the locked FinalPoints.
  livePoints: number | null;
}

export interface CurrentRoundCell {
  cellId: string;
  row: number;
  col: number;
  rowCategoryType: CategoryType;
  rowCategoryValue: string;
  colCategoryType: CategoryType;
  colCategoryValue: string;
  guess: CurrentRoundGuess | null;
}

export interface CurrentRoundResponse {
  roundId: string;
  startTime: string;
  endTime: string;
  allowGuessChange: boolean;
  cells: CurrentRoundCell[];
}

export interface SubmitGuessResponse {
  isCorrect: boolean;
  attemptCount: number;
  locked: boolean;
  // Frontend name-display fix: see CurrentRoundGuess.resolvedPlayerName.
  resolvedPlayerName: string | null;
}

export interface SignupResponse {
  id: string;
  email: string;
  displayName: string;
}

export interface LoginResponse {
  accessToken: string;
  refreshToken: string | null;
}

// SCREEN-03 (REQ-401/404's Tier 0 slice: the global league only).
// REQ-607 (S-034): rank is the row's global 1-based rank, not a page-local
// index — a later page no longer starts at rank 1, so the UI must always
// read this field rather than deriving rank from array position.
export interface LeaderboardRow {
  rank: number;
  userId: string;
  displayName: string;
  totalPoints: number;
  isRequestingUser: boolean;
}

// REQ-607 (S-034): the backend paginates via cursor/pageSize now — `rows`
// is capped at the requested pageSize per response, `nextCursor` is what to
// pass back as `cursor` for the next page, and `requestingUserRow` is
// always populated with the caller's own row/rank (even off-page) so
// SCREEN-03's "your position" footer never needs a second round-trip.
export interface LeaderboardResponse {
  rows: LeaderboardRow[];
  requestingUserRow: LeaderboardRow | null;
  nextCursor: number | null;
  hasMore: boolean;
}

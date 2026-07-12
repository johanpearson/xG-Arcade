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
export interface LeaderboardRow {
  userId: string;
  displayName: string;
  totalPoints: number;
  isRequestingUser: boolean;
}

export interface LeaderboardResponse {
  rows: LeaderboardRow[];
}

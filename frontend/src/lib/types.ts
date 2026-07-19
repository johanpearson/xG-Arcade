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
export interface LeaderboardRow {
  userId: string;
  displayName: string;
  totalPoints: number;
  isRequestingUser: boolean;
}

export interface LeaderboardResponse {
  rows: LeaderboardRow[];
}

// REQ-504: GET /auth/me — `isAdmin` is the only signal the frontend has for
// whether to show the admin nav entry point at all (App.tsx); the actual
// authorization is always re-checked server-side per request regardless.
export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  emailConfirmed: boolean;
  isAdmin: boolean;
}

// REQ-502/503: a single unverified PlayerData row, as returned by
// GET /admin/player-data/unverified (SCREEN-04).
export interface UnverifiedPlayerData {
  id: string;
  playerId: string;
  playerFullName: string;
  field: string;
  value: string;
  source: string;
  confidence: string;
  syncedAt: string;
}

// REQ-501: the PlayerOverride record created by POST /admin/player-overrides.
export interface PlayerOverride {
  id: string;
  playerId: string;
  field: string;
  value: string;
  reason: string;
  lockedByAdminId: string;
  lockedAt: string;
}

// REQ-505: a single round, as returned by the admin round-control endpoints
// (close/end-time) and nested inside AdminActiveRound below.
export interface AdminRound {
  roundId: string;
  gameKey: string;
  startTime: string;
  endTime: string;
}

// REQ-505: GET /admin/rounds/{gameKey}/active's response shape. This is also
// the frontend's only signal for whether the round-control/user-deletion
// admin sections exist in this environment at all — see
// `fetchActiveAdminRound`'s 404-as-null handling in lib/api.ts.
export interface AdminActiveRound {
  hasActiveRound: boolean;
  round: AdminRound | null;
}

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
  // REQ-214 (Photo reveal on a locked, correct cell): a nullable Wikidata
  // P18 photo URL for the resolved player, carried alongside
  // resolvedPlayerName wherever that's already resolved. Field name
  // confirmed against the backend half (S-043,
  // `CurrentRoundGuessResponse.ResolvedPlayerPhotoUrl` in
  // `XGArcade.Api.Rounds.RoundEndpoints`), which landed in parallel with
  // this frontend half — camelCase JSON serialization matches exactly, no
  // rename needed. Deliberately optional (`?:`), not just nullable, so an
  // older cached response that predates this field still degrades safely to
  // "no photo," same as an explicit `null` — never a type error and never a
  // fabricated photo.
  resolvedPlayerPhotoUrl?: string | null;
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
  // REQ-214: see CurrentRoundGuess.resolvedPlayerPhotoUrl — same confirmed
  // field name (matches `SubmitGuessResponse.ResolvedPlayerPhotoUrl` in
  // `XGArcade.Api.Guesses.GuessEndpoints`), present here too since
  // GridScreen.handleSubmitGuess spreads this response directly into the
  // cell's guess without an intervening GET /rounds/current, so a photo
  // revealed immediately after submitting (not just after a later reload)
  // needs it on this shape as well.
  resolvedPlayerPhotoUrl?: string | null;
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

// REQ-207/ADR-0007 (S-032): a suggestion sourced from PlayerNameIndex
// (COMP-10) only — a name appearing here implies nothing about whether
// it's correct for the current cell. Never merge this shape/path with
// PlayerAttribute/PlayerOverride correctness data (ADR-0007's boundary
// rule). birthYear/nationality are optional disambiguation context only
// (e.g. two players sharing a name), not a correctness signal, and must
// never be styled to suggest one is "more right" than another.
export interface PlayerAutocompleteSuggestion {
  playerId: string;
  name: string;
  birthYear?: number;
  nationality?: string;
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

// REQ-408 (S-054): a single closed round, as returned by
// GET /leagues/global/leaderboard/closed-rounds — one entry in SCREEN-03's
// "Previous Rounds" scope's round-selection list. Only ever a *closed* round
// (never active/upcoming, which is REQ-407/S-053's "Current Round"
// scope's territory instead) — `closedAt` is the field the list is ordered
// by (most recently closed first), `startTime`/`endTime` are the round's own
// window. There is no round-number field anywhere in this data, so the UI
// must label a row using these timestamps, never a fabricated "round #N."
export interface ClosedRoundSummary {
  roundId: string;
  startTime: string;
  endTime: string;
  closedAt: string;
}

// REQ-408/REQ-607 (S-054): the round-selection list's own pagination shape —
// deliberately the exact same cursor/pageSize/hasMore contract
// LeaderboardResponse below already uses, not a second, differently-shaped
// convention (REQ-408's explicit resolution of that question).
export interface ClosedRoundListResponse {
  rounds: ClosedRoundSummary[];
  nextCursor: number | null;
  hasMore: boolean;
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

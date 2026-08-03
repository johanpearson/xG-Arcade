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
  // REQ-216/ADR-0057: the mirror-image case of resolvedPlayerName/
  // resolvedPlayerPhotoUrl above — non-null ONLY when this guess locked the
  // cell with its final attempt still INCORRECT (state 3, or state 4's
  // incorrect branch) AND the submitted guess string matched a real
  // PlayerNameIndex candidate (never for state 2, and never for a guess
  // that matched nothing at all — a typo/gibberish/fictional name). Field
  // name confirmed against the backend half
  // (`CurrentRoundGuessResponse.IncorrectGuessMatchedPlayerName` in
  // `XGArcade.Api.Rounds.RoundEndpoints`, already merged) — camelCase JSON
  // matches exactly, same convention as every other field on this shape.
  // Deliberately optional (`?:`), not just nullable, for the same
  // older-cached-response-degrades-safely reason resolvedPlayerPhotoUrl
  // above already documents.
  incorrectGuessMatchedPlayerName?: string | null;
  // REQ-216/ADR-0057: a nullable Wikidata photo URL for the same
  // incorrect-but-real matched player above — independently nullable even
  // when incorrectGuessMatchedPlayerName is set (ADR-0057's Wikidata-only
  // lookup can time out, error, or genuinely have no photo; this is its own
  // silent, graceful fallback, never a fail-closed outcome). Confirmed
  // against `CurrentRoundGuessResponse.IncorrectGuessMatchedPlayerPhotoUrl`.
  incorrectGuessMatchedPlayerPhotoUrl?: string | null;
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

// REQ-209: one fitting candidate the player must choose between when a
// guess resolves to more than one real player who both satisfy the cell's
// categories — mirrors `DisambiguationCandidateResponse` in
// `XGArcade.Api.Guesses.GuessEndpoints` exactly (camelCase). Deliberately
// carries no correctness signal of its own: every listed candidate already
// satisfies both of the cell's categories server-side (that's what put it
// in this list at all), and picking one is what actually gets scored, not
// this list. `distinguishingAttributes` is the *other* known attributes
// beyond the cell's own two categories (e.g. birth year, a third club) —
// can legitimately be an empty array when nothing else is on file for that
// player; never treat that as an error or omit the candidate.
export interface DisambiguationCandidate {
  playerId: string;
  name: string;
  distinguishingAttributes: string[];
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
  // REQ-216/ADR-0057: see CurrentRoundGuess.incorrectGuessMatchedPlayerName/
  // incorrectGuessMatchedPlayerPhotoUrl — present here too (mirrors why
  // resolvedPlayerPhotoUrl is on this shape as well) since
  // GridScreen.applyScoredGuess spreads this response directly into the
  // cell's guess without an intervening GET /rounds/current, so a
  // just-locked incorrect cell shows its matched name/photo (or the
  // placeholder avatar) immediately, not only after a later reload.
  incorrectGuessMatchedPlayerName?: string | null;
  incorrectGuessMatchedPlayerPhotoUrl?: string | null;
  // REQ-209/REQ-210: null (and every other field behaves exactly as always)
  // on a normal, scored response. Non-null and non-empty ONLY when the
  // submitted name resolved to more than one fitting candidate — in that
  // case isCorrect is always false, attemptCount is always 0, locked is
  // always false, and resolvedPlayerName/resolvedPlayerPhotoUrl are always
  // null, because nothing was actually scored yet (no attempt consumed,
  // nothing persisted server-side). `candidates !== null` is the one,
  // unambiguous signal the frontend has for "render a picker instead of a
  // scored result" — never infer this from isCorrect/attemptCount alone.
  candidates: DisambiguationCandidate[] | null;
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
// rule). birthYear is optional disambiguation context only (e.g. two
// players sharing a name), not a correctness signal, and must never be
// styled to suggest one is "more right" than another. `nationality` was
// removed from this shape (and the API response) entirely — unlike
// birthYear, it can directly leak the answer for nationality-based xG
// Grid categories (e.g. Country × Club), since seeing which suggestions
// carry the target nationality tells the player who's eligible before
// they even guess.
export interface PlayerAutocompleteSuggestion {
  playerId: string;
  name: string;
  birthYear?: number;
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
// REQ-717/ADR-0036: `email` is nullable — a guest account (`User.IsGuest`
// on the backend) has none until it claims a real one via POST /auth/claim
// (see `claimAccount` in lib/api.ts). `isGuest` mirrors `User.IsGuest`
// directly (backend follow-up landed alongside this) — a first-class
// field, not derived from `email === null`.
export interface CurrentUser {
  id: string;
  email: string | null;
  displayName: string;
  emailConfirmed: boolean;
  isAdmin: boolean;
  isGuest: boolean;
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

// REQ-503 (2026-07-20 extension): a single row's outcome from
// POST /admin/player-data/approve — `failureReason` is `"NotFound"` or
// `"NotUnverified"` (as plain strings, not a shared enum type) when
// `approved` is false, `null` when true.
export interface PlayerDataApprovalResult {
  playerDataId: string;
  approved: boolean;
  failureReason: string | null;
}

// REQ-503 (2026-07-20 extension): POST /admin/player-data/approve's
// response — always 200 with one result per requested id (bulk, with a
// single id as the N=1 case), never an all-or-nothing batch result.
export interface ApprovePlayerDataResponse {
  results: PlayerDataApprovalResult[];
}

// REQ-503 (2026-07-20 extension): a single row's outcome from
// POST /admin/player-data/remove — `failureReason` is `"NotFound"` (the
// only reason removal can fail — unlike approve, removal has no
// "must still be unverified" precondition) when `removed` is false, `null`
// when true.
export interface PlayerDataRemovalResult {
  playerDataId: string;
  removed: boolean;
  failureReason: string | null;
}

// REQ-503 (2026-07-20 extension): POST /admin/player-data/remove's
// response — same shape as ApprovePlayerDataResponse above: always 200
// with one result per requested id (bulk, with a single id as the N=1
// case), never an all-or-nothing batch result.
export interface RemovePlayerDataResponse {
  results: PlayerDataRemovalResult[];
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

// REQ-714: PUT /auth/display-name's response shape
// (AuthController.UpdateDisplayName / UpdateDisplayNameResponse).
export interface UpdateDisplayNameResponse {
  id: string;
  displayName: string;
}

// REQ-507: GET /admin/accounts/metrics's response shape (SCREEN-04's
// "Accounts" section) — live counts as of the moment of the request, never a
// cached/stale snapshot. Visible to any authenticated admin in every
// environment, including Production (unlike REQ-505/506's Non-Production-only
// round-control/user-deletion probe) — see AdminAccountsEndpoints.cs.
// currentGuestCount and claimedGuestCount can never disagree with
// IsGuest/ClaimedAt by construction (REQ-717/ADR-0036), but both are
// surfaced anyway so an admin doesn't need to know that invariant to read
// this view correctly.
export interface AdminAccountMetrics {
  totalUserCount: number;
  currentGuestCount: number;
  claimedGuestCount: number;
}

// REQ-508 step 1: GET /admin/accounts/guests/count's response shape — the
// dry-run count shown before the bulk force-clear-guests action's confirm
// step, so the admin confirms a known, specific number rather than an
// open-ended action.
export interface GuestAccountCountResponse {
  count: number;
}

// REQ-508 step 2: one account's outcome from POST /admin/accounts/guests/clear
// — mirrors the per-row outcome shape REQ-503's bulk approve/remove actions
// already use (PlayerDataApprovalResult/PlayerDataRemovalResult above), but
// with three possible outcomes rather than two: a guest account can fail to
// delete for a reason other than "already gone" (surfaced via errorMessage),
// unlike removing a PlayerData row. errorMessage is null exactly when
// outcome is "Succeeded" (mirrors AdminAccountsEndpoints.cs's
// GuestAccountClearResult).
export type ClearGuestAccountOutcome = 'Succeeded' | 'NotFound' | 'Failed';

export interface ClearGuestAccountResult {
  userId: string;
  outcome: ClearGuestAccountOutcome;
  errorMessage: string | null;
}

// REQ-508 step 2: POST /admin/accounts/guests/clear's response shape —
// always 200 with one result per account matching IsGuest = true at the
// moment the action ran, never an all-or-nothing batch result (same
// reporting discipline as ApprovePlayerDataResponse/RemovePlayerDataResponse
// above).
export interface ClearGuestAccountsResponse {
  results: ClearGuestAccountResult[];
}

// REQ-1209/ADR-0058: GET /admin/xg-path/cycle's response shape
// (XGArcade.Api.Admin.AdminXGPathCycleResponse) — a pure read of REQ-1208's
// persisted `PathTargetCycle` state, never a trigger for a new eligible-pool
// computation. `hasData: false` (every other field null) is the normal,
// non-error "no xG Path round has ever generated yet" case — always a 200,
// never a 404. `remainingInCycleCount` is derived server-side
// (observedPoolSize - usedInCycleCount), not independently persisted, so it
// can never drift out of sync with the two figures it's computed from.
export interface AdminXGPathCycleState {
  hasData: boolean;
  cycleNumber: number | null;
  observedPoolSize: number | null;
  usedInCycleCount: number | null;
  remainingInCycleCount: number | null;
  lastCycleCompletedAt: string | null;
}

// REQ-215 (S-089): the persisted PlayerSuggestion row returned by
// POST /rounds/{roundId}/cells/{cellId}/suggestions
// (SuggestionEndpoints.SubmitSuggestionResponse). Always "Pending" at
// creation — this endpoint never auto-commits to PlayerAttribute/
// PlayerOverride/PlayerNameIndex (REQ-215's own explicit rule); a later
// "Approved"/"Rejected" value only ever comes from REQ-509/S-090's separate
// admin review surface, not from this response.
export interface SubmitSuggestionResponse {
  id: string;
  playerName: string;
  assertedClubs: string[];
  assertedNationality: string;
  status: string;
  createdAt: string;
}

// REQ-1203 (S-086): one club revealed within a ClubReveal turn — mirrors
// `PathClubClueResponse` (backend/src/XGArcade.Api/Path/PathEndpoints.cs)
// exactly. appearanceCount is null exactly when Wikidata's appearance-count
// qualifier wasn't recorded for that stint — the club is still shown,
// without a count, never delayed/omitted and never a fabricated "0 apps".
export interface PathClubClue {
  clubName: string;
  appearanceCount: number | null;
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
// CurrentRoundGuess above (an incorrect or in-progress guess never reveals
// the target player's identity).
export interface CurrentPathGuess {
  isCorrect: boolean;
  attemptCount: number;
  locked: boolean;
  submittedName: string;
  resolvedPlayerName: string | null;
  resolvedPlayerPhotoUrl: string | null;
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
// idiom as CurrentRoundResponse above.
export interface CurrentPathResponse {
  roundId: string;
  startTime: string;
  endTime: string;
  allowGuessChange: boolean;
  puzzles: CurrentPathPuzzle[];
}

// REQ-402/403: a custom league, as returned by POST /leagues,
// POST /leagues/join, and GET /leagues/mine (XGArcade.Api.Leagues.LeagueResponse)
// — this story's minimal "create/join/list my leagues" scope only, no
// per-league leaderboard data (that's separate, tracked follow-up work per
// REQ-404). inviteCode is always present here: every league this app's
// endpoints return is Type="custom" (the Type="global" league is never
// surfaced through these routes).
export interface CustomLeague {
  id: string;
  name: string;
  inviteCode: string;
}

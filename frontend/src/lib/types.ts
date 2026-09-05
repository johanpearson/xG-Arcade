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
  // REQ-216/ADR-0057: mirror-image case of resolvedPlayerName/
  // resolvedPlayerPhotoUrl above — see ADR-0057's Decision section for when
  // this fires (locked-incorrect cell, matched guess only) and its
  // silent-failure semantics. Field name confirmed against the backend half
  // (`CurrentRoundGuessResponse.IncorrectGuessMatchedPlayerName` in
  // `XGArcade.Api.Rounds.RoundEndpoints`, already merged) — camelCase JSON
  // matches exactly, same convention as every other field on this shape.
  // Deliberately optional (`?:`), not just nullable, for the same
  // older-cached-response-degrades-safely reason resolvedPlayerPhotoUrl
  // above already documents.
  incorrectGuessMatchedPlayerName?: string | null;
  // REQ-216/ADR-0057: nullable Wikidata photo URL for the same
  // incorrect-but-real matched player above, independently nullable even
  // when incorrectGuessMatchedPlayerName is set — see ADR-0057's "Fails
  // silently on timeout or no-match" decision. Confirmed against
  // `CurrentRoundGuessResponse.IncorrectGuessMatchedPlayerPhotoUrl`.
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
  // REQ-304: a human-readable, per-GameKey round number (e.g. "Grid Round
  // #12") — display-only, never a substitute identifier for routing,
  // submission, or lookup, which always use roundId above.
  sequenceNumber: number;
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
// window. REQ-304 (S-135) added `sequenceNumber`, a human-readable
// per-GameKey round number, alongside the existing `roundId` — `roundId`
// remains the real identifier for every lookup/route, `sequenceNumber` is
// display-only.
export interface ClosedRoundSummary {
  roundId: string;
  sequenceNumber: number;
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
// (see `claimAccount` in lib/auth.ts). `isGuest` mirrors `User.IsGuest`
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
// (close/end-time) and nested inside AdminActiveRound below. REQ-304
// (S-135) added `sequenceNumber` — the human-readable per-GameKey round
// number rendered by RoundControlSection.tsx as "Grid Round #N"/"Path Round
// #N", never the raw `roundId` GUID, which stays the real identifier.
export interface AdminRound {
  roundId: string;
  sequenceNumber: number;
  gameKey: string;
  startTime: string;
  endTime: string;
}

// REQ-505: GET /admin/rounds/{gameKey}/active's response shape. This is also
// the frontend's only signal for whether the round-control/user-deletion
// admin sections exist in this environment at all — see
// `fetchActiveAdminRound`'s 404-as-null handling in lib/admin.ts.
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

// REQ-903/ADR-0064: the response to POST /incidents
// (IncidentEndpoints.SubmitIncidentReportResponse) — the created GitHub
// issue's own URL, not secret, safe to show the player as confirmation.
export interface SubmitIncidentReportResponse {
  issueUrl: string;
}

// REQ-904/ADR-0066: one open, `user-reported`-labeled GitHub issue, as
// returned by GET /admin/incident-reports (AdminIncidentReportEndpoints
// .IncidentReportIssueResponse). Not rendered as an in-app list/detail view
// (that's explicitly out of scope, ADR-0064's "no review queue" boundary) —
// carried here only because the backend response includes it at no extra
// cost; the admin UI itself only reads `AdminIncidentReportsResponse
// .openCount` and links out to GitHub.
export interface AdminIncidentReportIssue {
  number: number;
  title: string;
  url: string;
}

// REQ-904/ADR-0066: the response to GET /admin/incident-reports
// (AdminIncidentReportEndpoints.IncidentReportsResponse). `available: false`
// means no successful GitHub poll has ever happened (cold start during an
// outage, or the token was never configured) — a distinct failure/unknown
// state that must never be read or rendered as `openCount: 0`; `openCount`
// is only meaningful when `available` is true. See PlayerSuggestionsEntry's
// use of PendingSuggestion for the sibling REQ-512 badge — this type is
// deliberately not merged with it, since REQ-904 has a genuine third state
// (`available: false`) that REQ-512's simpler count-or-403-hidden shape
// doesn't need.
export interface AdminIncidentReportsResponse {
  available: boolean;
  openCount: number;
  issues: AdminIncidentReportIssue[];
}

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
// CurrentRoundGuess above (an incorrect or in-progress guess never reveals
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
  // same shape/wording as CurrentRoundGuess.livePoints above: livePoints is
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
// idiom as CurrentRoundResponse above.
export interface CurrentPathResponse {
  roundId: string;
  // REQ-304: see CurrentRoundResponse.sequenceNumber above — same
  // display-only, per-GameKey ("xg-path") round number.
  sequenceNumber: number;
  startTime: string;
  endTime: string;
  allowGuessChange: boolean;
  puzzles: CurrentPathPuzzle[];
}

// REQ-1301-1304/1306 (xG Predict): mirrors `PredictMatchResponse`
// (`backend/src/XGArcade.Api/Predict/PredictEndpoints.cs`) exactly, camelCase
// as always over the wire. `homeGoals`/`awayGoals` are `null` until this
// specific player has predicted this specific match — once predicted, they
// hold whatever was last submitted (a resubmission overwrites, so this is
// always the current value, never a history). Deliberately does not carry
// the match's real final score or any grading outcome (REQ-1304/1305) — this
// is the player's own in-progress prediction state only, not a scored
// result; REQ-1305's asynchronous grading has no reachable frontend surface
// yet (see requirements-document.md §4.14's own intro status note).
export interface PredictMatch {
  matchId: string;
  homeTeamName: string;
  awayTeamName: string;
  kickoffUtc: string;
  homeGoals: number | null;
  awayGoals: number | null;
}

// REQ-1301/1303/1306: mirrors `CurrentPredictResponse` exactly — the active
// xg-predict round's fixed 5-match slate at once (REQ-1301: exactly 5,
// ordered by kickoff), same 404-as-empty-state idiom as
// CurrentRoundResponse/CurrentPathResponse above. `locked` is REQ-1303's
// round-wide automatic lock (true once `now >= earliest match's kickoff`) —
// once true, nothing is submittable by anyone, ever, for this round.
// `confirmedLocked` is REQ-1306's independent PER-PLAYER lock (this specific
// player used the explicit "confirm and lock" action) — can be true while
// `locked` is still false. Once either is true, this player can no longer
// edit any prediction; PredictScreen.tsx must check both independently,
// never assume one implies the other.
export interface CurrentPredictResponse {
  roundId: string;
  // REQ-304: see CurrentRoundResponse.sequenceNumber above — same
  // display-only, per-GameKey ("xg-predict") round number.
  sequenceNumber: number;
  startTime: string;
  endTime: string;
  locked: boolean;
  confirmedLocked: boolean;
  matches: PredictMatch[];
}

// REQ-1302: the response to `POST /predict/matches/{matchId}/predictions` —
// the just-submitted prediction, echoed back with the server-confirmed
// values (always identical to what was sent on success; there is no
// server-side adjustment). PredictScreen.tsx uses this to update its own
// copy of the match's homeGoals/awayGoals without a full re-fetch, mirroring
// GridScreen's applyScoredGuess-from-response idiom rather than PathScreen's
// always-re-fetch one — unlike xG Path's POST .../guesses response, this one
// already carries everything the UI needs (no separate clue-reveal state to
// pick up).
export interface SubmitPredictionResponse {
  matchId: string;
  homeGoals: number;
  awayGoals: number;
}

// REQ-1306: the response to `POST /predict/confirm`. `lockedAt` is
// display-only (not currently rendered anywhere — PredictScreen.tsx re-fetches
// GET /predict/current after a successful confirm and relies on that
// response's `confirmedLocked` flag, not this timestamp, to switch to the
// locked treatment) but is carried on this type since the server returns it
// and dropping a real response field silently would be misleading about the
// contract.
export interface ConfirmPredictionsResponse {
  roundId: string;
  lockedAt: string;
}

// REQ-509/510 (S-090)/ADR-0053: a single pending PlayerSuggestion row, as
// returned by GET /admin/suggestions — mirrors PendingSuggestionResponse
// (backend/src/XGArcade.Api/Admin/AdminSuggestionEndpoints.cs) exactly.
// Deliberately its own type, never merged with UnverifiedPlayerData above —
// ADR-0053 is explicit that PlayerSuggestion never shares a row shape with
// REQ-503's PlayerData queue. submittingUserDisplayName is null exactly when
// the submitting user has since been deleted (REQ-710 anonymizes rather than
// hard-deletes Guess rows, but SubmittingUserId here has no FK — see
// PlayerSuggestion's own backend doc comment), never an error case.
export interface PendingSuggestion {
  id: string;
  playerName: string;
  assertedClubs: string[];
  assertedNationality: string;
  submittingUserId: string;
  submittingUserDisplayName: string | null;
  rowCategoryType: string;
  colCategoryType: string;
  createdAt: string;
}

// REQ-509/510: the shared lookup response shape for both
// POST /admin/suggestions/{id}/lookup and POST /admin/player-search/lookup
// (WikidataPlayerLookupResponse). `found: false` (every other field
// null/empty) is a normal, valid "Wikidata has no matching footballer for
// this name" outcome — never conflated with a 503 "lookup unavailable"
// failure (ADR-0046's timeout-vs-no-match distinction); a 503 is left to
// throw as an ApiError by lib/admin.ts's lookup functions rather than ever
// resolving to this shape.
//
// REQ-515: `existingPlayerId` is the local Player id already on file for
// `wikidataQid`, resolved server-side via
// IPlayerRepository.GetPlayerByWikidataQidAsync. Non-null only when
// `found` is true AND a matching local Player row already exists; null in
// every other case, including `found: false` and `found: true` with no
// local Player row yet for that QID.
export interface WikidataPlayerLookupResult {
  found: boolean;
  wikidataQid: string | null;
  fullName: string | null;
  nationality: string | null;
  clubs: string[];
  existingPlayerId: string | null;
}

// REQ-509/510: the admin's reviewed/confirmed values sent to both
// POST /admin/suggestions/{id}/commit and POST /admin/player-search/commit
// (CommitPlayerDataRequest) — typically pre-filled from a prior lookup
// response and then hand-edited before submitting; the admin's own review is
// the point, never a blind rubber-stamp of whatever Wikidata returned.
// `nationality: null`/blank means "don't touch this player's nationality
// override," `clubs: []` means "don't add any new club attributes" — the
// backend 400s if both end up empty, so the UI should avoid submitting that
// combination in the first place (defense in depth, not a substitute for
// the server's own validation).
export interface CommitPlayerDataPayload {
  wikidataQid: string;
  fullName: string;
  nationality: string | null;
  clubs: string[];
  reason: string;
}

// REQ-509/510/S-129: both commit endpoints' shared response shape
// (CommitPlayerDataResponse) — reports what the commit ACTUALLY wrote, not
// just an echo of the admin's confirmed input. `playerCreated`/
// `nationalityWritten`/`clubsAdded` distinguish a real write from a no-op
// (e.g. every asserted club already an effective `PlayerAttribute`, surfaced
// via `clubsAlreadyEffective` instead of `clubsAdded`) — the old shape
// (`nationality`/`clubs` only) was indistinguishable from a no-op, which is
// the exact ambiguity this story removes. See docs/backlog.md S-129.
export interface CommitPlayerDataResult {
  playerId: string;
  playerCreated: boolean;
  nationality: string | null;
  nationalityWritten: boolean;
  clubsAdded: string[];
  clubsAlreadyEffective: string[];
}

// REQ-511: the public GET /announcement-banner response shape
// (Announcements/AnnouncementBannerEndpoints.AnnouncementBannerResponse) —
// message is non-null exactly when active is true. Both "no banner has
// ever been created" and "a banner exists but is inactive" collapse to the
// same `{ active: false, message: null }` shape here — a visitor never
// needs to tell those two apart (unlike the admin-only shape below, which
// does, via its own 404-as-null convention in fetchAdminAnnouncementBanner).
export interface AnnouncementBanner {
  active: boolean;
  message: string | null;
}

// REQ-511: the admin-only shape shared by GET/PUT/activate/deactivate
// /admin/announcement-banner (Admin/AdminAnnouncementBannerEndpoints
// .AdminAnnouncementBannerResponse) — carries isActive and audit fields the
// public AnnouncementBanner shape above deliberately omits, so the admin
// screen can pre-populate its form and know the current active state on
// load without a second request.
export interface AdminAnnouncementBanner {
  id: string;
  message: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  lastUpdatedByAdminId: string;
}

// REQ-513/514: one of the four scalar Player fields ("fullName" | "position"
// | "birthYear" | "photoUrl") POST /admin/players/{id}/refresh-from-wikidata
// can touch — mirrors `PlayerRefreshFieldResult` exactly
// (backend/src/XGArcade.Api/Admin/AdminEndpoints.cs). `oldValue` is always
// the value BEFORE the refresh ran, regardless of `changed` — REQ-514's UI
// reads it for the "unchanged" case too, since there's no other field
// carrying the current stored value then. `newValue` is populated only when
// `changed` is true. `birthYear`'s int? is serialized as its string form
// here too (same as every other field), matching the backend record's own
// choice not to add a differently-typed sibling just for one field.
export interface PlayerRefreshFieldResult {
  field: string;
  changed: boolean;
  oldValue: string | null;
  newValue: string | null;
}

// REQ-513/514: POST /admin/players/{id}/refresh-from-wikidata's response
// shape — mirrors `RefreshPlayerFromWikidataResponse` exactly. `fields`
// always carries all four PlayerRefreshFieldResult rows (fullName/position/
// birthYear/photoUrl), whether or not any of them actually changed.
export interface RefreshPlayerFromWikidataResponse {
  playerId: string;
  wikidataQid: string;
  fields: PlayerRefreshFieldResult[];
}

// REQ-411 (S-178/S-179): GET /users/{userId}/stats's response shape
// (XGArcade.Api.Users.UserStatsResponse) — read-only, single-`GameKey`-scoped
// stats/profile view, identical shape whether `userId` is the caller's own id
// or another player's (no privacy toggle, REQ-411's own "Out of scope").
// `hasRoundsPlayed: false` is the one discriminator for "zero qualifying
// rounds" — in that case `roundsPlayed` is `0` and `bestFinalPoints`/
// `averageFinalPoints`/`rank` are all `null`, never `0`-filled, so the UI can
// render a distinct "no rounds played yet" state rather than a blank or
// zero-filled screen. `rank` can independently be `null` even when
// `hasRoundsPlayed` is `true` (REQ-409's 5-round ranking minimum not yet
// met) — render it as omitted, not as an error, in that case.
export interface UserStatsResponse {
  hasRoundsPlayed: boolean;
  roundsPlayed: number;
  bestFinalPoints: number | null;
  averageFinalPoints: number | null;
  rank: number | null;
}

// REQ-722/S-180 (backend)/S-182 (frontend): POST /users/me/avatar's 201
// response shape (XGArcade.Api.Avatars.AvatarEndpoints) — status is the
// backend's AvatarStatus enum serialized as its string name ("Pending" |
// "Approved" | "Rejected"), always "Pending" at creation time — REQ-517's
// separate admin review (S-181) is the only path that ever moves a
// submission to "Approved"/"Rejected", never this endpoint.
export interface SubmitAvatarResponse {
  id: string;
  status: string;
  createdAt: string;
}

// REQ-722/S-182: one avatar submission summary, as nested within
// AvatarStatusResponse below — mirrors AvatarSubmissionSummary
// (backend/src/XGArcade.Api/Avatars/AvatarEndpoints.cs). imageUrl is always
// a relative path on this same backend (never a raw Supabase URL) — see
// fetchAvatarImageObjectUrl in lib/avatar.ts, which is how it must actually
// be fetched (an authenticated raw fetch + blob object URL, since a plain
// <img src> can't carry the Authorization header GET
// /users/me/avatar/{id}/image requires).
export interface AvatarSubmissionSummary {
  id: string;
  createdAt: string;
  imageUrl: string;
}

// REQ-722/S-182: GET /users/me/avatar's response shape. All three fields
// are independent and can be non-null simultaneously — a `rejected`
// submission never implies `approved` is null (an earlier, separate
// submission can still be sitting there Approved), and the same logic
// applies to `pending` — REQ-722's "Seeing your own status" and "Replacing
// an approved avatar" acceptance criteria are both explicit about this.
// Never collapse this into a single mutually-exclusive status switch.
export interface AvatarStatusResponse {
  pending: AvatarSubmissionSummary | null;
  rejected: AvatarSubmissionSummary | null;
  approved: AvatarSubmissionSummary | null;
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

// REQ-1401 (S-217, extended 2026-09-03 REQ-1401/1402 display-name follow-up):
// mirrors FriendRequestResponse exactly
// (backend/src/XGArcade.Api/Social/FriendEndpoints.cs) — `status` is the
// backend's FriendRequestStatus enum serialized as its string name
// ("Pending" | "Accepted" | "Declined"). `resolvedAt` is null exactly while
// `status` is "Pending". `requesterDisplayName`/`recipientDisplayName` were
// added alongside the raw ids (batch-resolved server-side via
// IUserRepository.GetByIdsAsync, never null/optional) — this closes
// design-document.md SCREEN-15's former "Identity gap" note; every list
// built from this shape now renders a real display name instead of
// `shortUserId()`'s truncated-id stand-in.
export interface FriendRequestResponse {
  id: string;
  requesterUserId: string;
  requesterDisplayName: string;
  recipientUserId: string;
  recipientDisplayName: string;
  status: string;
  createdAt: string;
  resolvedAt: string | null;
}

// REQ-1401 (S-217, extended 2026-09-03): mirrors FriendshipResponse exactly.
// `friendUserId` is always the *other* user relative to the caller (never a
// raw UserAId/UserBId pair) — the backend already normalizes this, so the
// frontend never needs to know which side of the pair the caller was.
// `friendDisplayName` is that same other user's display name (never null/
// optional) — see FriendRequestResponse's own comment above for the same
// closed "Identity gap" note.
export interface FriendshipResponse {
  id: string;
  friendUserId: string;
  friendDisplayName: string;
  createdAt: string;
}

// REQ-1402 (S-217, extended 2026-09-03): mirrors ChallengeResponse exactly
// (backend/src/XGArcade.Api/Social/ChallengeEndpoints.cs) — `status` is the
// backend's ChallengeStatus enum serialized as its string name ("Pending" |
// "Accepted" | "Declined"). `resultingMatchId` is null until a successful
// accept creates the real `ConnectMatch` row server-side (S-218's separate,
// not-yet-built scope owns whatever happens with that id next — this
// story never navigates anywhere with it, see SCREEN-15's own "Challenges
// tab" note). `challengerDisplayName`/`challengedDisplayName` were added
// alongside the raw ids (never null/optional) — same closed "Identity gap"
// note as FriendRequestResponse/FriendshipResponse above.
export interface ChallengeResponse {
  id: string;
  challengerUserId: string;
  challengerDisplayName: string;
  challengedUserId: string;
  challengedDisplayName: string;
  status: string;
  createdAt: string;
  resolvedAt: string | null;
  resultingMatchId: string | null;
}

// REQ-1403 (S-217): mirrors MatchmakingOptInResponse exactly
// (backend/src/XGArcade.Api/Social/MatchmakingEndpoints.cs). `status` is
// the backend's MatchmakingOptInStatus enum serialized as its string name
// (e.g. "Waiting"). `resultingMatchId` is always null on this response in
// practice — pairing happens later, in a separate sweep job, never
// synchronously with the opt-in call itself — but is typed nullable rather
// than omitted since the shape genuinely allows it. There is no GET/list
// endpoint for this resource (see SCREEN-15's own "Known limitation" note)
// — this response is the only source of truth the frontend ever has for a
// given opt-in, and only for the lifetime of the page it was created on.
export interface MatchmakingOptInResponse {
  id: string;
  optedInAt: string;
  expiresAt: string;
  status: string;
  resultingMatchId: string | null;
}

// REQ-1411 (S-217): mirrors NotificationSummaryResponse exactly
// (backend/src/XGArcade.Api/Notifications/NotificationEndpoints.cs) — the
// header nav's own "Friends" badge count is
// `pendingFriendRequestCount + pendingChallengeCount +
// matchesAwaitingActionCount` (see design-document.md SCREEN-07's own
// 2026-09-03 status note for why a combined count, not `hasPending` alone,
// was chosen for that badge). `hasPending` is carried through unused by
// the badge itself (the three counts already imply it) but kept on this
// type since it's a real response field, not to be silently dropped.
export interface NotificationSummaryResponse {
  pendingFriendRequestCount: number;
  pendingChallengeCount: number;
  matchesAwaitingActionCount: number;
  hasPending: boolean;
}

// S-218 (design-document.md SCREEN-16): mirrors SubmitTargetPickResponse
// exactly (backend/src/XGArcade.Api/Connect/ConnectMatchEndpoints.cs).
// `locked` is true only once this was the completing (second, non-trivial)
// selection — see that record's own doc comment for the full state machine.
export interface ConnectTargetPickSubmitResponse {
  targetPlayerId: string;
  selectedAt: string;
  locked: boolean;
}

// S-218: mirrors SubmitChainStepResponse exactly
// (backend/src/XGArcade.Api/Connect/ConnectChainStepEndpoints.cs) — always a
// normal 200 response, never an error, for a wrong or unresolvable guess
// (REQ-1406's GuessEndpoints-style precedent). `position`/`attemptNumber`/
// `candidatePlayerId` are null only when `candidatePlayerName` didn't
// resolve to any known player at all — treat that as "no such player
// found," not as a real validation failure (it consumes no attempt/strike
// server-side). `busted: true` means this player's participation in the
// match just ended (REQ-1407's two-strikes rule).
//
// Design change (2026-09-04, REQ-1406, ADR-0104): the request no longer
// carries a claimed club — the player names only a candidate, and the
// server computes which club(s) actually connect them.
// `matchedClubName`/`matchedOverlapStartYear`/`matchedOverlapEndYear` are
// additionally null whenever `isValid` is false (no shared club was found
// at all).
export interface ConnectSubmitChainStepResponse {
  isValid: boolean;
  chainComplete: boolean;
  position: number | null;
  attemptNumber: number | null;
  candidatePlayerId: string | null;
  matchedClubName: string | null;
  matchedOverlapStartYear: number | null;
  matchedOverlapEndYear: number | null;
  busted: boolean;
}

// S-218: mirrors ChatMessageResponse exactly
// (backend/src/XGArcade.Api/Connect/ConnectChatEndpoints.cs). `senderUserId`
// is nullable — goes null once REQ-710 anonymization has run for that
// sender, same nullable-in-place shape as ConnectMatchListItem/
// ConnectMatchDetail's own `opponentUserId`. `senderDisplayName` mirrors
// `senderUserId`'s own nullability exactly — null iff `senderUserId` is
// null, never a placeholder for that case (render with the same "a deleted
// user" fallback PendingSuggestion.submittingUserDisplayName's convention
// establishes); resolved server-side, never derived client-side.
export interface ConnectChatMessage {
  id: string;
  senderUserId: string | null;
  senderDisplayName: string | null;
  messageText: string;
  sentAt: string;
}

// S-218: mirrors ConnectMatchListItemResponse exactly
// (backend/src/XGArcade.Api/Connect/ConnectMatchQueryEndpoints.cs, S-218
// prep). `status`/`outcome` are the backend's enums serialized as their
// string names — `status`: "AwaitingTargetPicks" | "Active" | "Resolved";
// `outcome`: "Pending" | "Win" | "Loss" | "Draw", already translated into
// the CALLER's own perspective server-side (never PlayerA/PlayerB-relative).
// `opponentUserId` is nullable, same REQ-710-anonymization reasoning as
// ConnectChatMessage.senderUserId above. `opponentDisplayName` mirrors
// `opponentUserId`'s own nullability exactly — null iff `opponentUserId` is
// null, never a placeholder (same "a deleted user" fallback convention as
// ConnectChatMessage.senderDisplayName above).
export interface ConnectMatchListItem {
  matchId: string;
  opponentUserId: string | null;
  opponentDisplayName: string | null;
  status: string;
  createdAt: string;
  startedAt: string | null;
  deadlineUtc: string | null;
  resolvedAt: string | null;
  outcome: string;
  awaitingMyAction: boolean;
}

// S-218: mirrors ConnectTargetPickResponse exactly (nested inside
// ConnectMatchDetailResponse) — a resolved target pick, name already joined
// in server-side.
export interface ConnectTargetPickView {
  targetPlayerId: string;
  targetPlayerName: string;
  locked: boolean;
}

// S-218: mirrors ConnectChainStepDetailResponse exactly — one of the
// caller's OWN chain steps (never an opponent's — see
// ConnectMatchDetail.opponentTerminalState's own comment below).
//
// matchedClubName/matchedOverlapStartYear/matchedOverlapEndYear (design
// change, 2026-09-04, REQ-1406, ADR-0104): the club(s) the candidate and
// the preceding chain player actually share, computed server-side — never
// a player-typed claim. Null together only when isValid is false.
export interface ConnectChainStepView {
  position: number;
  attemptNumber: number;
  candidatePlayerId: string;
  candidatePlayerName: string;
  matchedClubName: string | null;
  matchedOverlapStartYear: number | null;
  matchedOverlapEndYear: number | null;
  isValid: boolean;
  closesChain: boolean;
  submittedAt: string;
}

// S-218: mirrors ConnectTerminalStateResponse exactly — the three
// terminal-reaching signals (REQ-1405/1407/1408) bundled for display, kept
// as three separate booleans (not collapsed to one) so the UI can say
// *which* terminal state applies ("busted" vs. "timed out" vs. "completed
// their chain").
export interface ConnectTerminalState {
  busted: boolean;
  timedOut: boolean;
  completed: boolean;
}

// S-218: mirrors ConnectMatchDetailResponse exactly — full single-match
// detail for the gameplay screen (GET /matches/{matchId}). `myTargetPick`/
// `opponentTargetPick` are both present on the wire but `opponentTargetPick`
// stays null until `status` leaves "AwaitingTargetPicks" (REQ-1404's
// mutual-invisibility rule) — never derived from the opponent's own
// `locked` flag client-side. `myChainSteps` is always the caller's own
// chain only; only `opponentTerminalState`'s three booleans are ever
// exposed for the opponent's progress, never their actual steps. `myScore`/
// `opponentScore` are null until that specific player has completed a valid
// chain (a forfeiting player never gets a score, REQ-1408).
export interface ConnectMatchDetail {
  status: string;
  createdAt: string;
  startedAt: string | null;
  deadlineUtc: string | null;
  resolvedAt: string | null;
  outcome: string;
  opponentUserId: string | null;
  opponentDisplayName: string | null;
  myTargetPick: ConnectTargetPickView | null;
  opponentTargetPick: ConnectTargetPickView | null;
  myChainSteps: ConnectChainStepView[];
  myTerminalState: ConnectTerminalState;
  opponentTerminalState: ConnectTerminalState;
  myScore: number | null;
  opponentScore: number | null;
}

// REQ-517 (S-183): a single pending avatar submission, as returned by
// GET /admin/avatar-submissions — mirrors PendingAvatarSubmissionResponse
// (backend/src/XGArcade.Api/Admin/AdminAvatarEndpoints.cs) exactly, oldest
// first (the backend already sorts; this UI never re-sorts). imagePreviewUrl
// is already a resolved, short-lived (5 min) signed URL — safe to use
// directly as an <img src>, never a storage key to resolve client-side.
// submittingUserDisplayName is null exactly when the submitting user has
// since been deleted (REQ-710 anonymizes rather than hard-deletes), same
// null-means-deleted convention PendingSuggestion.submittingUserDisplayName
// above already establishes — render with the same "a deleted user"
// fallback SuggestionsScreen's PendingSuggestionRow uses, for consistency.
export interface PendingAvatarSubmission {
  id: string;
  imagePreviewUrl: string;
  submittingUserId: string;
  submittingUserDisplayName: string | null;
  createdAt: string;
}

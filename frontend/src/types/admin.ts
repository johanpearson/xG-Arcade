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

// REQ-1414: mirrors ConnectDisputeDataCorrectionSuggestionResponse exactly
// (backend/src/XGArcade.Api/Admin/AdminConnectDisputeSuggestionEndpoints.cs)
// — a durable, read-only record of one Approved xG Connect dispute
// (REQ-1412/1413), for a future, out-of-scope data-correction decision.
// Deliberately its own type, never merged with PendingSuggestion above —
// per ADR-0053's own precedent (REQ-215's PlayerSuggestion got its own new,
// separate admin table rather than being folded into an unrelated queue),
// applied here for the same reason: this is a club-overlap fact discovered
// through a match, not a cell-guess candidate. Never carries any
// approve/reject/act-on affordance of its own (REQ-1414's own "no
// workflow" rule) — see ConnectDisputeSuggestionsScreen.tsx's own
// top-of-file comment.
export interface ConnectDisputeDataCorrectionSuggestion {
  id: string;
  connectMatchId: string;
  connectChainStepId: string;
  connectChainStepDisputeId: string;
  candidatePlayerId: string;
  candidatePlayerName: string;
  precedingPlayerId: string;
  precedingPlayerName: string;
  claimedClubName: string;
  createdAt: string;
}

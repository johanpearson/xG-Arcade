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
//
// chainStepId (REQ-1412, 2026-09-05): the persisted step's own id — null
// only when `candidatePlayerId` is also null (the "no such player"
// case, nothing was persisted), otherwise always present, including on an
// invalid/Busted result, specifically so the caller can immediately offer
// "dispute this" right after a failed submission (see ChainBuilder.tsx's
// own dispute-affordance handling).
export interface ConnectSubmitChainStepResponse {
  chainStepId: string | null;
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
//
// chainStepId (REQ-1412/1413, 2026-09-05): the persisted step's own id —
// previously absent from every xG Connect read surface, since nothing
// before REQ-1412 ever needed to reference a specific step by id. Needed
// to call POST /matches/{matchId}/chain-steps/{chainStepId}/dispute
// against an already-loaded step (e.g. one shown in ChainStepsList from an
// earlier fetch), not only against the just-submitted result's own
// SubmitChainStepResponse.chainStepId.
export interface ConnectChainStepView {
  chainStepId: string;
  position: number;
  attemptNumber: number;
  candidatePlayerId: string;
  candidatePlayerName: string;
  matchedClubName: string | null;
  matchedOverlapStartYear: number | null;
  matchedOverlapEndYear: number | null;
  isValid: boolean;
  closesChain: boolean;
  // closingClubName/closingOverlapStartYear/closingOverlapEndYear
  // (gap-fill addendum, 2026-09-09, REQ-1406): the club(s) the candidate
  // shares with the OTHER target player — the closing connection itself,
  // not the connection to the previous chain player (that's
  // matchedClubName/*). Computed and persisted only when closesChain is
  // true, using the same deterministic tie-break as matchedClubName/* when
  // more than one shared club exists. Null together only when closesChain
  // is false, same convention as matchedClubName/* above.
  closingClubName: string | null;
  closingOverlapStartYear: number | null;
  closingOverlapEndYear: number | null;
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
  // REQ-1418: the opponent's own completed chain — null/absent for every
  // status other than "Resolved" (REQ-1406's mutual-invisibility rule is
  // unchanged before resolution: only opponentTerminalState's three
  // booleans are ever exposed while a match is still AwaitingTargetPicks or
  // Active). Once "Resolved", populated with exactly the steps the
  // opponent actually submitted before finishing/forfeiting — same shape as
  // myChainSteps, rendered the same way via the shared ChainStepsList.tsx.
  opponentChainSteps: ConnectChainStepView[] | null;
}

// REQ-1412/1413/ADR-0109: mirrors ChainStepDisputeResponse exactly
// (backend/src/XGArcade.Api/Connect/ConnectChainStepDisputeEndpoints.cs) —
// the response to raising (POST .../chain-steps/{id}/dispute) or reviewing
// (POST .../disputes/{id}/approve or .../deny) a dispute. `status` is the
// backend's ConnectChainStepDisputeStatus enum serialized as its string
// name ("Pending" | "Approved" | "Denied"), same Enum.ToString() convention
// as every other status/outcome field in this file. `reviewedAt` is null
// exactly while `status` is "Pending".
export interface ChainStepDisputeResponse {
  disputeId: string;
  chainStepId: string;
  claimedClubName: string;
  status: string;
  raisedAt: string;
  reviewedAt: string | null;
}

// REQ-1412/1413/1420: mirrors ChainStepDisputeListItemResponse exactly — one
// dispute in a match, from the caller's OWN perspective (GET
// /matches/{matchId}/disputes). `raisedByMe: true` is the caller's own
// dispute (read-only status only, DisputeReview.tsx's own-dispute
// section); `raisedByMe: false` is the opponent's dispute (actionable — the
// caller may approve/deny it, DisputeReview.tsx's review-card section, but
// only once `visible` is true, see below). `position` is the disputed
// step's own chain position, included here (unlike ChainStepDisputeResponse
// above) so a review UI can label each dispute without a second lookup
// against myChainSteps.
//
// REQ-1420: `position`/`claimedClubName`/`candidatePlayerName` are all
// nullable, and null together exactly when `visible` is false — a Pending
// dispute raised by the OTHER participant (`raisedByMe: false`), withheld
// from the caller because the caller hasn't yet reached their own terminal
// state and the disputer didn't set `allowEarlyView`. `visible` becomes
// true (with all three fields populated) once the caller reaches a
// terminal state, once the disputer opted into early view, or always for a
// dispute the caller raised themselves (`raisedByMe: true`, never
// withheld). `candidatePlayerName` is new on this shape (previously only
// implied by ChainStepId) — DisputeReview.tsx's actionable card names it
// once visible.
export interface ChainStepDisputeListItem {
  disputeId: string;
  chainStepId: string;
  position: number | null;
  claimedClubName: string | null;
  candidatePlayerName: string | null;
  status: string;
  raisedAt: string;
  reviewedAt: string | null;
  raisedByMe: boolean;
  visible: boolean;
}

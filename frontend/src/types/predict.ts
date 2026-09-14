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
// CurrentRoundResponse/CurrentPathResponse. `locked` is REQ-1303's
// round-wide automatic lock (true once `now >= earliest match's kickoff`) —
// once true, nothing is submittable by anyone, ever, for this round.
// `confirmedLocked` is REQ-1306's independent PER-PLAYER lock (this specific
// player used the explicit "confirm and lock" action) — can be true while
// `locked` is still false. Once either is true, this player can no longer
// edit any prediction; PredictScreen.tsx must check both independently,
// never assume one implies the other.
export interface CurrentPredictResponse {
  roundId: string;
  // REQ-304: see CurrentRoundResponse.sequenceNumber — same
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

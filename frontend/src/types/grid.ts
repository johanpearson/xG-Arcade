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

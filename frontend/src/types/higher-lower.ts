// REQ-1504 (S-227/S-228): mirrors the backend's plain `HigherLowerDirection`
// C# enum (`backend/src/XGArcade.Core/Games/HigherLowerSubmission.cs`)
// exactly — `Higher = 0`, `Lower = 1`. Confirmed against that file and
// `HigherLowerEndpoints.cs`: **no `JsonStringEnumConverter` is registered
// anywhere for this enum**, unlike every other backend enum this file
// documents (`AvatarStatus`, `FriendRequestStatus`, `ChallengeStatus`,
// `MatchmakingOptInStatus`, `ConnectChainStepDisputeStatus` — all of which
// serialize as their string name). This one serializes/deserializes as a
// raw integer over JSON instead, so it's modeled as a const object + derived
// union of numeric literals rather than either a TS `enum` (unnecessary
// reverse-mapping/emitted-object baggage for a value only ever sent, never
// iterated) or a string union (would send `"Higher"`/`"Lower"` over the
// wire and fail model binding server-side, since there's no converter to
// parse them back into the enum).
export const HigherLowerDirection = {
  Higher: 0,
  Lower: 1,
} as const;
export type HigherLowerDirection = (typeof HigherLowerDirection)[keyof typeof HigherLowerDirection];

// REQ-1504 (S-227): mirrors `HigherLowerBaselineResponse` exactly — the
// current baseline's value is always revealed (never hidden), unlike
// `HigherLowerNextComparator` below.
export interface HigherLowerBaseline {
  playerId: string;
  name: string;
  value: number;
  // REQ-1508 (S-233): a nullable Wikidata P18 photo URL for the baseline
  // player — the same already-backfilled `Player.PhotoUrl` field REQ-214/
  // S-045 already carries for Grid, exposed on this screen's responses for
  // the first time (no new sourcing/backfill work). Field name confirmed
  // against the backend half (`HigherLowerBaselineResponse.PhotoUrl` in
  // `XGArcade.Api.HigherLower.HigherLowerEndpoints`). Deliberately optional
  // (`?:`), not just nullable, so an older cached response that predates
  // this field still degrades safely to "no photo" — same convention
  // `CurrentRoundGuess.resolvedPlayerPhotoUrl` already establishes.
  // Unlike that REQ-214 precedent, this is rendered unconditionally whenever
  // present, never gated behind a reveal/click — a Higher/Lower player's
  // identity (name) is already always shown, so a photo only confirms an
  // already-known identity, never leaks a hidden one (REQ-1508's own
  // "genuinely different trigger shape" scope note).
  photoUrl?: string | null;
}

// REQ-1504 (S-227): mirrors `HigherLowerNextComparatorResponse` exactly —
// deliberately has no `value` field at all, not just a nullable one, the
// same "hidden until guessed" contract the backend DTO's own doc comment
// says is enforced at the type level, not just by convention.
export interface HigherLowerNextComparator {
  playerId: string;
  name: string;
  // REQ-1508 (S-233): same field/contract as HigherLowerBaseline.photoUrl
  // above — always-visible identity confirmation, unconditional, never a
  // reveal/click gate. Showing this has no effect on this interface's own
  // "no value field" enforcement above: only the hidden stat *value* stays
  // withheld, never the identity a photo confirms.
  photoUrl?: string | null;
}

// REQ-1504/1505 (S-227): mirrors `CurrentHigherLowerResponse` exactly — the
// active xg-higher-lower round's single shared comparator sequence
// (ADR-0110: fixed, generated once, shared unchanged by every participant)
// plus this specific player's own current attempt state layered on top. Same
// 404-as-empty-state idiom as CurrentRoundResponse/CurrentPathResponse/
// CurrentPredictResponse. `nextComparator` is null exactly when
// `hasEnded` is true (nothing left to guess) — never null while an attempt
// is still active.
export interface CurrentHigherLowerResponse {
  roundId: string;
  // REQ-304: see CurrentRoundResponse.sequenceNumber — same
  // display-only, per-GameKey ("xg-higher-lower") round number.
  sequenceNumber: number;
  startTime: string;
  endTime: string;
  // A player-facing label for what's being compared (e.g. "career league
  // appearances") — server-provided, opaque text, never a fixed client-side
  // enum (mirrors CategoryType's own "derived, never hardcoded" reasoning
  // at the top of this file).
  statCategory: string;
  // The Round's fixed total sequence length (REQ-1502/1503) — the maximum
  // streak this attempt could ever reach. Used for "Streak N of M" display,
  // mirroring CurrentPathPuzzle's own puzzle-count-driven "Puzzle N of M".
  comparatorCount: number;
  streakLength: number;
  hasEnded: boolean;
  baseline: HigherLowerBaseline;
  nextComparator: HigherLowerNextComparator | null;
}

// REQ-1504 (S-227): mirrors `SubmitHigherLowerGuessResponse` exactly. Unlike
// resolvedPlayerName/resolvedPlayerPhotoUrl elsewhere in this file (only
// ever set on a CORRECT guess), revealedPlayerId/revealedPlayerName/
// revealedValue are always populated here — the just-guessed comparator's
// real identity and value are revealed on both a correct AND an incorrect
// guess (REQ-1504), never withheld either way.
export interface SubmitHigherLowerGuessResponse {
  isCorrect: boolean;
  revealedPlayerId: string;
  revealedPlayerName: string;
  revealedValue: number;
  streakLength: number;
  hasEnded: boolean;
  // REQ-1508 (S-233): a nullable Wikidata P18 photo URL for the just-
  // guessed comparator — populated on both a correct AND an incorrect
  // guess, mirroring revealedPlayerName/revealedValue's own "always
  // populated either way" contract above (unlike resolvedPlayerPhotoUrl
  // elsewhere in this file, which is correct-guess-only). Field name
  // confirmed against the backend half
  // (`SubmitHigherLowerGuessResponse.RevealedPlayerPhotoUrl`, mirroring
  // `ResolvedPlayerPhotoUrl`'s naming precedent). Deliberately optional
  // (`?:`), same older-cached-response-degrades-safely reason as the other
  // photo fields on this file.
  revealedPlayerPhotoUrl?: string | null;
}

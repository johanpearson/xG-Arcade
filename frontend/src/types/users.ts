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

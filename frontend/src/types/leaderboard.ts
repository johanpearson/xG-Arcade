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

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

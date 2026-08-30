namespace XGArcade.DataSync.ApiFootball;

// ADR-0094: non-secret API-Football config — which league/season to scope
// fixture queries to. Resolved once in ServiceRegistration.cs, the same
// "not a direct IConfiguration dependency inside this project" split
// GitHubIncidentReportOptions already establishes. See
// ServiceRegistration.AddApiFootballServices for the LeagueId/Season
// defaults (Premier League's real API-Football league ID and the
// season-by-start-year computation, respectively) and their own caveats.
public record ApiFootballOptions(int LeagueId, int Season);

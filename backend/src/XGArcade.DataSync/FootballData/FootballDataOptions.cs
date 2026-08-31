namespace XGArcade.DataSync.FootballData;

// ADR-0099: non-secret football-data.org config — which competition to
// scope fixture queries to. Resolved once in ServiceRegistration.cs, the
// same "not a direct IConfiguration dependency inside this project" split
// GitHubIncidentReportOptions already establishes. See
// ServiceRegistration.AddFootballDataServices for the CompetitionCode
// default ("PL", Premier League) and its own caveats.
//
// Deliberately no separate "season" field the way ADR-0094's
// ApiFootballOptions needed one — football-data.org's
// GET /v4/competitions/{code} response carries currentSeason.currentMatchday
// directly, so this client never has to compute or configure a season year
// itself (see ADR-0099's Consequences section for why this turned out to
// be a real, not just incidental, advantage of the swap).
public record FootballDataOptions(string CompetitionCode);

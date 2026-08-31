namespace XGArcade.DataSync.FootballData;

// REQ-1301: one fixture in an upcoming Premier League gameweek's full
// fixture list, as returned by IFootballDataClient.GetUpcomingGameweekFixturesAsync.
// KickoffUtc is parsed from football-data.org's own ISO 8601 UTC date string
// (match.utcDate, always "Z"-suffixed per their v4 API) via
// DateTimeOffset.Parse(...).UtcDateTime.
//
// Deliberately no ordering/selection semantics on this type or the method
// that returns it — REQ-1301's tightest-kickoff-clustering 5-match
// selection is a future round-generation service's job, not this client's.
public record FootballDataFixture(
    int FixtureId,
    int HomeTeamId,
    string HomeTeamName,
    int AwayTeamId,
    string AwayTeamName,
    DateTime KickoffUtc);

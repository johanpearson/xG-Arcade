namespace XGArcade.DataSync.ApiFootball;

// REQ-1301: one fixture in an upcoming Premier League gameweek's full
// fixture list, as returned by IApiFootballClient.GetUpcomingGameweekFixturesAsync.
// KickoffUtc is parsed from API-Football's own ISO 8601-with-offset date
// string (fixture.date) via DateTimeOffset.Parse(...).UtcDateTime — always
// normalized to UTC regardless of which offset API-Football itself returns.
//
// Deliberately no ordering/selection semantics on this type or the method
// that returns it — REQ-1301's tightest-kickoff-clustering 5-match
// selection is a future round-generation service's job, not this client's.
public record ApiFootballFixture(
    int FixtureId,
    int HomeTeamId,
    string HomeTeamName,
    int AwayTeamId,
    string AwayTeamName,
    DateTime KickoffUtc);

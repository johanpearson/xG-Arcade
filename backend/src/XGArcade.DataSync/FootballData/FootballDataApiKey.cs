namespace XGArcade.DataSync.FootballData;

// ADR-0099: the football-data.org API token, sent per-request as the
// "X-Auth-Token" header — kept as its own tiny type, not a bare string, for
// the same DI-activation reason GitHubIncidentReportToken
// (XGArcade.Core.IncidentReporting) already is: DI's typed-client
// activation for AddHttpClient<IFootballDataClient, FootballDataClient>
// can't resolve an unwrapped `string` constructor parameter unambiguously.
//
// Value is nullable/optional, same as GitHubIncidentReportToken (not
// SupabaseServiceRoleKey's `?? throw` at startup) — this precondition is
// not guaranteed provisioned in every environment yet.
// FootballDataClient's own per-method check turns an unset key into a
// clean per-call FootballDataClientException, never a startup crash. Never
// log this value anywhere.
public record FootballDataApiKey(string? Value);

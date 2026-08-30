namespace XGArcade.DataSync.ApiFootball;

// ADR-0094: the API-Football API key, sent per-request as the
// "x-apisports-key" header — kept as its own tiny type, not a bare string,
// for the same DI-activation reason GitHubIncidentReportToken
// (XGArcade.Core.IncidentReporting) already is: DI's typed-client
// activation for AddHttpClient<IApiFootballClient, ApiFootballClient> can't
// resolve an unwrapped `string` constructor parameter unambiguously.
//
// Value is nullable/optional, same as GitHubIncidentReportToken (not
// SupabaseServiceRoleKey's `?? throw` at startup) — ADR-0094 item 3's
// API-Football account/key precondition is not guaranteed provisioned in
// every environment yet. ApiFootballClient's own per-method check turns an
// unset key into a clean per-call ApiFootballClientException, never a
// startup crash. Never log this value anywhere.
public record ApiFootballApiKey(string? Value);

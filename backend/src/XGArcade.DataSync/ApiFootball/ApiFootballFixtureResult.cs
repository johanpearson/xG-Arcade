namespace XGArcade.DataSync.ApiFootball;

// REQ-1305: a fixture's confirmation state, as distinguished from its raw
// API-Football status-short code. See ApiFootballClient's own internal
// status-code lookup (ApiFootballClient.ResolveOutcome) for the exact
// code-to-outcome mapping and its own "compiled from documentation, not a
// live response sample" caveat (this schema is unverified from this
// sandbox — egress to api-football.com is blocked here, ADR-0094's own
// Context section).
public enum ApiFootballFixtureOutcome
{
    // Default for any in-progress/not-started/unrecognized status code.
    // ADR-0094's "For AI agents" section: this is a retry-later state, part
    // of a real, ongoing polling loop (a future grading-job story) — never
    // a permanent failure, and never conflated with an
    // ApiFootballClientException (a genuine technical/data problem).
    NotYetConfirmed,

    // Status short is one of FT/AET/PEN/AWD/WO — confirmed final. Goals
    // carries the real final score.
    Finished,

    // Status short is one of PST/CANC/ABD — REQ-1305's voided-match case.
    // HomeGoals/AwayGoals reflect whatever (possibly null) value
    // API-Football itself returned and should not be relied on for this
    // outcome.
    PostponedOrAbandoned,
}

// REQ-1305: one fixture's current status/result, as returned by
// IApiFootballClient.GetFixtureResultAsync. RawStatusShort is API-Football's
// own status-short code (e.g. "FT", "NS", "PST") — carried through
// unmodified so a caller/log line can see exactly what API-Football
// reported even when Outcome collapses several codes into one bucket.
// HomeGoals/AwayGoals are only meaningful once Outcome is Finished.
public record ApiFootballFixtureResult(
    int FixtureId,
    ApiFootballFixtureOutcome Outcome,
    string RawStatusShort,
    int? HomeGoals,
    int? AwayGoals);

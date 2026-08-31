namespace XGArcade.DataSync.FootballData;

// REQ-1305: a fixture's confirmation state, as distinguished from its raw
// football-data.org status code. See FootballDataClient's own internal
// status lookup (FootballDataClient.ResolveOutcome) for the exact
// code-to-outcome mapping and its own "compiled from documentation, not a
// live response sample" caveat (this schema is unverified from this
// sandbox — egress to football-data.org is blocked here too, same posture
// ADR-0094's Context section took for api-football.com; see ADR-0099).
public enum FootballDataFixtureOutcome
{
    // Default for any in-progress/not-started/unrecognized status code.
    // ADR-0094's "For AI agents" section (carried forward by ADR-0099):
    // this is a retry-later state, part of a real, ongoing polling loop —
    // never a permanent failure, and never conflated with a
    // FootballDataClientException (a genuine technical/data problem).
    NotYetConfirmed,

    // Status is FINISHED or AWARDED (a result administratively awarded,
    // e.g. a forfeit) — confirmed final. Goals carries the real final score.
    Finished,

    // Status is one of POSTPONED/CANCELLED/SUSPENDED — REQ-1305's
    // voided-match case. HomeGoals/AwayGoals reflect whatever (possibly
    // null) value football-data.org itself returned and should not be
    // relied on for this outcome.
    PostponedOrAbandoned,
}

// REQ-1305: one fixture's current status/result, as returned by
// IFootballDataClient.GetFixtureResultAsync. RawStatus is football-data.org's
// own status code (e.g. "FINISHED", "SCHEDULED", "POSTPONED") — carried
// through unmodified so a caller/log line can see exactly what
// football-data.org reported even when Outcome collapses several codes
// into one bucket. HomeGoals/AwayGoals are only meaningful once Outcome is
// Finished.
public record FootballDataFixtureResult(
    int FixtureId,
    FootballDataFixtureOutcome Outcome,
    string RawStatus,
    int? HomeGoals,
    int? AwayGoals);

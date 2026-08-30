namespace XGArcade.DataSync.ApiFootball;

// COMP-07 (DataSync.Clients)/ADR-0094: the one and only path this backend
// uses to call API-Football's fixtures endpoint, for xG Predict (COMP-15).
// Deliberately narrow — fixture list, status, and final score only, no
// broader API-Football surface adopted (ADR-0094 Decision item 1). This is
// a CLIENT ONLY: no round generation, no 5-match tightest-kickoff-
// clustering selection, no grading/scoring logic here — those are a
// separate, later story (S-191, docs/backlog.md) building on top of this.
//
// For AI agents (mirrors ADR-0094's own "For AI agents" section):
// - Never call this from a per-request/per-user code path — every call is
//   server-side and shared, the same discipline IWikidataClient already
//   follows.
// - Do NOT reuse WikidataClient's "fetch once, cache permanently"
//   assumption for fixture/result data — a fixture's status and score are
//   point-in-time facts that must be re-checked until REQ-1305's grading
//   confirms them. This client itself does no caching or polling; that is
//   a future grading-job story's responsibility, built on
//   ApiFootballFixtureOutcome.NotYetConfirmed being a clean, distinct,
//   retry-later signal (never a permanent failure).
// - No ExternalApiUsage budget-gating here — ADR-0094 is explicit this
//   game's usage is well under API-Football's free-tier cap, and that
//   mechanism (ADR-0011) is scoped to xG Grid's separate Tier 1 trigger.
//
// Schema caveat: API-Football's real v3 REST schema this client parses
// against is drawn from training knowledge / published documentation, NOT
// verified against a live fetch from this sandbox — egress to
// api-football.com is blocked here (same posture ADR-0094's own Context
// section already took). Flag for manual human verification before
// relying on this in production; do not treat any field name/shape here as
// confirmed.
public interface IApiFootballClient
{
    // REQ-1301: fetches one upcoming Premier League gameweek's full
    // fixture list. Implemented as two HTTP round trips against
    // API-Football's documented v3 endpoints:
    //   1. GET /fixtures/rounds?league={leagueId}&season={season}&current=true
    //      -> { "response": ["Regular Season - N"] } (take the first name).
    //   2. GET /fixtures?league={leagueId}&season={season}&round={roundName}
    //      (URL-encoded) -> the full fixture list for that round.
    //
    // Deliberately returns the WHOLE round's fixture list, unfiltered,
    // unsorted, and unsliced (may be zero, fewer than 5, or more than 5
    // fixtures) — REQ-1301's tightest-kickoff-clustering 5-match selection
    // is a future round-generation service's job, not this client's; that
    // caller decides what to do with whatever size list comes back,
    // including REQ-1301's own "abort and log" acceptance criterion for a
    // round with too few fixtures.
    //
    // Error contract: throws ApiFootballClientException on HTTP failure,
    // non-success status, timeout, or unparseable/unexpected JSON from
    // EITHER call — and also when the rounds call returns zero round names
    // (an API-Football account/config problem, not a legitimate empty
    // state). Never swallows to an empty list — this is a job-style batch
    // fetch whose success metric matters (the same "swallowing would be
    // indistinguishable from a genuine empty result" reasoning
    // WikidataClient's throwing methods already use, e.g.
    // QueryPlayerPoolBirthYearAsync): a caller needs to distinguish
    // "API-Football is unreachable" from "this round genuinely has fewer
    // than 5 fixtures."
    Task<IReadOnlyList<ApiFootballFixture>> GetUpcomingGameweekFixturesAsync(
        CancellationToken cancellationToken = default);

    // REQ-1305: looks up one fixture's current status and, if finished,
    // its final score. A single HTTP call:
    //   GET /fixtures?id={fixtureId}
    //   -> { "response": [ { "fixture": {...}, "goals": {...} } ] } (0 or 1 items).
    //
    // Point-in-time, not cache-once — see this interface's own "For AI
    // agents" section above. ApiFootballFixtureOutcome.NotYetConfirmed is
    // the clean, distinguishable "retry later" signal a future poller
    // needs; it is never thrown as a failure.
    //
    // Error contract: throws ApiFootballClientException on HTTP failure,
    // timeout, or unparseable JSON. Also throws when API-Football's
    // response array is empty (no record of this fixture ID at all) —
    // shouldn't happen for a fixture ID this system itself obtained from
    // GetUpcomingGameweekFixturesAsync, but is a distinguishable
    // technical/data problem, not REQ-1305's "not yet confirmed, retry
    // later" outcome. Conflating the two would silently absorb a genuine
    // error into what must be a real, ongoing polling state.
    Task<ApiFootballFixtureResult> GetFixtureResultAsync(
        int fixtureId,
        CancellationToken cancellationToken = default);
}

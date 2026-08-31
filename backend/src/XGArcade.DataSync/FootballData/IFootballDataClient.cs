namespace XGArcade.DataSync.FootballData;

// COMP-07 (DataSync.Clients)/ADR-0099: the one and only path this backend
// uses to call football-data.org's fixtures endpoints, for xG Predict
// (COMP-15). Replaces IApiFootballClient (ADR-0094) — API-Football's free
// plan turned out to restrict season access to a rolling historical
// window that excludes the current season entirely (confirmed via
// api-football.com's own support chat, 2026-08-31; never actually
// verified before ADR-0094 shipped, since egress to api-football.com was
// blocked from this sandbox even then), making it structurally unusable
// for a live prediction game on the free tier. See ADR-0099 for the full
// swap reasoning, including the still-open ToS-verification action item.
//
// Deliberately narrow — fixture list, status, and final score only, no
// broader football-data.org surface adopted. This is a CLIENT ONLY: no
// round generation, no 5-match tightest-kickoff-clustering selection, no
// grading/scoring logic here — that lives in XGPredictGameModule/
// PredictGradingService.
//
// For AI agents (mirrors ADR-0094's own "For AI agents" section, carried
// forward unchanged by ADR-0099):
// - Never call this from a per-request/per-user code path — every call is
//   server-side and shared, the same discipline IWikidataClient already
//   follows.
// - Do NOT reuse WikidataClient's "fetch once, cache permanently"
//   assumption for fixture/result data — a fixture's status and score are
//   point-in-time facts that must be re-checked until REQ-1305's grading
//   confirms them. This client itself does no caching or polling; that is
//   PredictGradingService's responsibility, built on
//   FootballDataFixtureOutcome.NotYetConfirmed being a clean, distinct,
//   retry-later signal (never a permanent failure).
// - No ExternalApiUsage budget-gating here — that mechanism (ADR-0011) is
//   scoped to xG Grid's separate, still-dormant Tier 1 API-Football
//   fallback trigger, unrelated to this client. football-data.org's free
//   tier is a 10-requests/minute rate limit, not a small daily cap, and
//   this game's usage (roughly 1-2 calls per round generation, plus modest
//   polling during each round's live window) is comfortably under it.
//
// Schema caveat: football-data.org's real v4 REST schema this client
// parses against is drawn from published documentation, NOT verified
// against a live fetch from this sandbox — egress to football-data.org is
// blocked here (same posture ADR-0094's own Context section already took
// for api-football.com). Flag for manual human verification before
// relying on this in production; do not treat any field name/shape here
// as confirmed.
public interface IFootballDataClient
{
    // REQ-1301: fetches one upcoming Premier League gameweek's full
    // fixture list. Implemented as two HTTP round trips against
    // football-data.org's documented v4 endpoints:
    //   1. GET /v4/competitions/{code} -> { "currentSeason": { "currentMatchday": N, ... } }
    //      (take currentSeason.currentMatchday).
    //   2. GET /v4/competitions/{code}/matches?matchday={N} -> the full
    //      fixture list for that matchday.
    //
    // Deliberately returns the WHOLE matchday's fixture list, unfiltered,
    // unsorted, and unsliced (may be zero, fewer than 5, or more than 5
    // fixtures) — REQ-1301's tightest-kickoff-clustering 5-match selection
    // is XGPredictGameModule's job, not this client's; that caller decides
    // what to do with whatever size list comes back, including REQ-1301's
    // own "abort and log" acceptance criterion for a round with too few
    // fixtures.
    //
    // Error contract: throws FootballDataClientException on HTTP failure,
    // non-success status, timeout, or unparseable/unexpected JSON from
    // EITHER call — and also when the competition call returns no
    // currentSeason.currentMatchday (an account/config problem, not a
    // legitimate empty state). Never swallows to an empty list — this is a
    // job-style batch fetch whose success metric matters, same reasoning
    // ApiFootballClientException's own doc comment (ADR-0094) already used:
    // a caller needs to distinguish "football-data.org is unreachable"
    // from "this gameweek genuinely has fewer than 5 fixtures."
    Task<IReadOnlyList<FootballDataFixture>> GetUpcomingGameweekFixturesAsync(
        CancellationToken cancellationToken = default);

    // REQ-1305: looks up one fixture's current status and, if finished,
    // its final score. A single HTTP call:
    //   GET /v4/matches/{fixtureId}
    //   -> { "id": ..., "status": "...", "homeTeam": {...}, "awayTeam": {...}, "score": { "fullTime": { "home": ..., "away": ... } } }
    //
    // Point-in-time, not cache-once — see this interface's own "For AI
    // agents" section above. FootballDataFixtureOutcome.NotYetConfirmed is
    // the clean, distinguishable "retry later" signal PredictGradingService
    // needs; it is never thrown as a failure.
    //
    // Error contract: throws FootballDataClientException on HTTP failure
    // (including a 404 for an unknown match id), timeout, or unparseable
    // JSON. Shouldn't happen for a fixture id this system itself obtained
    // from GetUpcomingGameweekFixturesAsync, but is a distinguishable
    // technical/data problem, not REQ-1305's "not yet confirmed, retry
    // later" outcome. Conflating the two would silently absorb a genuine
    // error into what must be a real, ongoing polling state.
    Task<FootballDataFixtureResult> GetFixtureResultAsync(
        int fixtureId,
        CancellationToken cancellationToken = default);
}

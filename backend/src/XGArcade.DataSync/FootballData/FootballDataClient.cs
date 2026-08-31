using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace XGArcade.DataSync.FootballData;

// ADR-0099: the one and only path this backend uses to call
// football-data.org's fixtures endpoints. Injected via HttpClient with
// BaseAddress https://api.football-data.org/v4/ (see
// ServiceRegistration.AddFootballDataServices) — the API token is set
// per-request here, never on httpClient's own DefaultRequestHeaders, the
// same discipline this client's predecessor (ApiFootballClient, ADR-0094)
// already established.
//
// Schema caveat: see IFootballDataClient's own doc comment — every JSON
// shape below is drawn from documentation, not a live response sample
// verified from this sandbox.
public class FootballDataClient(
    HttpClient httpClient,
    FootballDataApiKey apiKey,
    FootballDataOptions options,
    ILogger<FootballDataClient> logger) : IFootballDataClient
{
    public async Task<IReadOnlyList<FootballDataFixture>> GetUpcomingGameweekFixturesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureApiKeyConfigured();

        var currentMatchday = await FetchCurrentMatchdayAsync(cancellationToken);
        return await FetchFixturesForMatchdayAsync(currentMatchday, cancellationToken);
    }

    public async Task<FootballDataFixtureResult> GetFixtureResultAsync(
        int fixtureId, CancellationToken cancellationToken = default)
    {
        EnsureApiKeyConfigured();

        var requestUri = $"matches/{fixtureId}";
        var item = await SendAndParseAsync<FootballDataMatchItem>(
            requestUri, $"fetch fixture {fixtureId}'s result", cancellationToken);

        var statusRaw = item.Status;
        if (string.IsNullOrWhiteSpace(statusRaw))
        {
            logger.LogError("football-data.org returned fixture {FixtureId} with no status.", fixtureId);
            throw new FootballDataClientException($"football-data.org returned fixture {fixtureId} with no status.");
        }

        return new FootballDataFixtureResult(
            fixtureId, ResolveOutcome(statusRaw), statusRaw,
            item.Score?.FullTime?.Home, item.Score?.FullTime?.Away);
    }

    private async Task<int> FetchCurrentMatchdayAsync(CancellationToken cancellationToken)
    {
        var requestUri = $"competitions/{options.CompetitionCode}";
        var parsed = await SendAndParseAsync<FootballDataCompetitionResponse>(
            requestUri, "fetch the current matchday", cancellationToken);

        var currentMatchday = parsed.CurrentSeason?.CurrentMatchday;
        if (currentMatchday is null)
        {
            // An account/competition-code config problem (e.g. no active
            // season for this competition), not a legitimate "no upcoming
            // gameweek" state — REQ-1301's caller needs to know this
            // failed, not treat it as zero fixtures.
            logger.LogError(
                "football-data.org returned no current matchday for competition {CompetitionCode}.",
                options.CompetitionCode);
            throw new FootballDataClientException(
                "football-data.org returned no current matchday — check the FootballData:CompetitionCode configuration.");
        }

        return currentMatchday.Value;
    }

    private async Task<IReadOnlyList<FootballDataFixture>> FetchFixturesForMatchdayAsync(
        int matchday, CancellationToken cancellationToken)
    {
        var requestUri = $"competitions/{options.CompetitionCode}/matches?matchday={matchday}";
        var parsed = await SendAndParseAsync<FootballDataMatchesResponse>(
            requestUri, $"fetch fixtures for matchday {matchday}", cancellationToken);

        var items = parsed.Matches ?? [];
        var fixtures = new List<FootballDataFixture>(items.Count);
        foreach (var item in items)
        {
            fixtures.Add(ParseFixture(item, matchday));
        }

        return fixtures;
    }

    private static FootballDataFixture ParseFixture(FootballDataMatchItem item, int matchday)
    {
        // Bound to locals before the null/blank checks below — nullable
        // flow analysis reliably narrows a LOCAL variable through an
        // IsNullOrWhiteSpace/"is null" check, unlike a chained
        // `item.HomeTeam?.Name`-style property access, which does not
        // narrow the same way (same precedent as this client's
        // predecessor, ApiFootballClient.ParseFixture, ADR-0094).
        var fixtureId = item.Id;
        var date = item.UtcDate;
        var homeId = item.HomeTeam?.Id;
        var homeName = item.HomeTeam?.Name;
        var awayId = item.AwayTeam?.Id;
        var awayName = item.AwayTeam?.Name;

        if (fixtureId is null || string.IsNullOrWhiteSpace(date)
            || homeId is null || string.IsNullOrWhiteSpace(homeName)
            || awayId is null || string.IsNullOrWhiteSpace(awayName))
        {
            throw new FootballDataClientException(
                $"football-data.org returned a fixture with missing required fields for matchday {matchday}.");
        }

        DateTime kickoffUtc;
        try
        {
            kickoffUtc = DateTimeOffset.Parse(date).UtcDateTime;
        }
        catch (FormatException ex)
        {
            throw new FootballDataClientException(
                $"football-data.org returned an unparseable fixture date '{date}' for matchday {matchday}.", ex);
        }

        return new FootballDataFixture(fixtureId.Value, homeId.Value, homeName, awayId.Value, awayName, kickoffUtc);
    }

    // Exact status-to-outcome mapping — compiled from football-data.org's
    // own documented v4 match status values, NOT verified against a live
    // response sample from this sandbox (see IFootballDataClient's own doc
    // comment for the same caveat). Kept as one small, easy-to-audit switch
    // so it's cheap to extend once a real response sample is available.
    private static FootballDataFixtureOutcome ResolveOutcome(string status) => status switch
    {
        // FINISHED = played to a full-time result, AWARDED = a result
        // administratively awarded (e.g. a forfeit) — every one of these is
        // a confirmed final result with a real score.
        "FINISHED" or "AWARDED" => FootballDataFixtureOutcome.Finished,

        // POSTPONED, CANCELLED, SUSPENDED — REQ-1305's voided-match case;
        // no real final score to grade against.
        "POSTPONED" or "CANCELLED" or "SUSPENDED" => FootballDataFixtureOutcome.PostponedOrAbandoned,

        // Everything else — SCHEDULED, TIMED, IN_PLAY, PAUSED, and any
        // status not enumerated above yet — a retry-later state, never a
        // permanent failure (IFootballDataClient's "For AI agents" section).
        _ => FootballDataFixtureOutcome.NotYetConfirmed,
    };

    private void EnsureApiKeyConfigured()
    {
        if (string.IsNullOrWhiteSpace(apiKey.Value))
        {
            // Never a stack trace/exception detail here beyond this message
            // — an unconfigured key is an expected state until a
            // football-data.org account/token has been set up in this
            // environment, same "expected state, not a startup crash"
            // reasoning this client's predecessor (ApiFootballClient,
            // ADR-0094) already established.
            logger.LogError("football-data.org request failed: FootballData:ApiKey is not configured.");
            throw new FootballDataClientException("football-data.org is not configured on this environment yet.");
        }
    }

    private async Task<T> SendAndParseAsync<T>(string requestUri, string actionDescription, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        // football-data.org's real auth header — set per-request, never on
        // httpClient's own DefaultRequestHeaders (see this class's own doc
        // comment).
        request.Headers.Add("X-Auth-Token", apiKey.Value);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                // Full detail (status + football-data.org's own response
                // body) logged server-side only — this is a server-side/
                // job-only client with no player-facing caller to leak it
                // to, but the same "don't surface raw provider error
                // bodies" discipline as this client's predecessor still
                // applies to the exception message thrown below.
                logger.LogError(
                    "football-data.org request to {RequestUri} failed ({StatusCode}) trying to {Action}: {ResponseBody}",
                    requestUri, response.StatusCode, actionDescription, responseBody);
                throw new FootballDataClientException(
                    $"football-data.org returned {(int)response.StatusCode} trying to {actionDescription}.");
            }

            var parsed = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
            if (parsed is null)
            {
                logger.LogError(
                    "football-data.org returned a success status with no parseable body trying to {Action} ({RequestUri}).",
                    actionDescription, requestUri);
                throw new FootballDataClientException($"football-data.org returned an unparseable response trying to {actionDescription}.");
            }

            return parsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Network failure/timeout — same catch shape as
            // GitHubIssueClient.CreateIssueAsync/this client's predecessor.
            logger.LogError(ex, "football-data.org request to {RequestUri} failed trying to {Action}.", requestUri, actionDescription);
            throw new FootballDataClientException($"Could not reach football-data.org trying to {actionDescription}.", ex);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "football-data.org response for {RequestUri} was unparseable trying to {Action}.", requestUri, actionDescription);
            throw new FootballDataClientException($"football-data.org returned an unparseable response trying to {actionDescription}.", ex);
        }
    }

    private record FootballDataCompetitionResponse(
        [property: JsonPropertyName("currentSeason")] FootballDataSeason? CurrentSeason);

    private record FootballDataSeason(
        [property: JsonPropertyName("currentMatchday")] int? CurrentMatchday);

    private record FootballDataMatchesResponse(
        [property: JsonPropertyName("matches")] IReadOnlyList<FootballDataMatchItem>? Matches);

    // Shared item shape for both the matchday-fixture-list call (which
    // populates HomeTeam/AwayTeam) and the by-id result call (which
    // populates Score) — football-data.org's own per-match envelope is
    // identical for both, only which fields are populated differs by
    // endpoint (the by-id call also nests inside no list wrapper, unlike
    // the matches list — see FetchFixturesForMatchdayAsync vs.
    // GetFixtureResultAsync's own deserialization target).
    private record FootballDataMatchItem(
        [property: JsonPropertyName("id")] int? Id,
        [property: JsonPropertyName("utcDate")] string? UtcDate,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("homeTeam")] FootballDataTeam? HomeTeam,
        [property: JsonPropertyName("awayTeam")] FootballDataTeam? AwayTeam,
        [property: JsonPropertyName("score")] FootballDataScore? Score);

    private record FootballDataTeam(
        [property: JsonPropertyName("id")] int? Id,
        [property: JsonPropertyName("name")] string? Name);

    private record FootballDataScore(
        [property: JsonPropertyName("fullTime")] FootballDataScoreDetail? FullTime);

    private record FootballDataScoreDetail(
        [property: JsonPropertyName("home")] int? Home,
        [property: JsonPropertyName("away")] int? Away);
}

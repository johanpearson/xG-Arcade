using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace XGArcade.DataSync.ApiFootball;

// ADR-0094/COMP-07: the one and only path this backend uses to call
// API-Football's fixtures endpoint. Injected via HttpClient with
// BaseAddress https://v3.football.api-sports.io/ (see
// ServiceRegistration.AddApiFootballServices) — the API key is set
// per-request here, never on httpClient's own DefaultRequestHeaders, the
// same discipline GitHubIssueClient (XGArcade.Core.IncidentReporting)
// already establishes for its own bearer token.
//
// Schema caveat: see IApiFootballClient's own doc comment — every JSON
// shape below is drawn from documentation, not a live response sample
// verified from this sandbox.
public class ApiFootballClient(
    HttpClient httpClient,
    ApiFootballApiKey apiKey,
    ApiFootballOptions options,
    ILogger<ApiFootballClient> logger) : IApiFootballClient
{
    public async Task<IReadOnlyList<ApiFootballFixture>> GetUpcomingGameweekFixturesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureApiKeyConfigured();

        var roundName = await FetchCurrentRoundNameAsync(cancellationToken);
        return await FetchFixturesForRoundAsync(roundName, cancellationToken);
    }

    public async Task<ApiFootballFixtureResult> GetFixtureResultAsync(
        int fixtureId, CancellationToken cancellationToken = default)
    {
        EnsureApiKeyConfigured();

        var requestUri = $"fixtures?id={fixtureId}";
        var parsed = await SendAndParseAsync<ApiFootballFixturesResponse>(
            requestUri, $"fetch fixture {fixtureId}'s result", cancellationToken);

        var item = parsed.Response?.FirstOrDefault();
        if (item is null)
        {
            // Shouldn't happen for a fixture ID this system itself obtained
            // from GetUpcomingGameweekFixturesAsync, but a distinguishable
            // technical/data problem regardless — never conflated with
            // NotYetConfirmed (see IApiFootballClient's own doc comment).
            logger.LogError("API-Football returned no record for fixture {FixtureId}.", fixtureId);
            throw new ApiFootballClientException($"API-Football has no record of fixture {fixtureId}.");
        }

        var statusShort = item.Fixture?.Status?.Short;
        if (string.IsNullOrWhiteSpace(statusShort))
        {
            logger.LogError("API-Football returned fixture {FixtureId} with no status code.", fixtureId);
            throw new ApiFootballClientException($"API-Football returned fixture {fixtureId} with no status code.");
        }

        return new ApiFootballFixtureResult(
            fixtureId, ResolveOutcome(statusShort), statusShort, item.Goals?.Home, item.Goals?.Away);
    }

    private async Task<string> FetchCurrentRoundNameAsync(CancellationToken cancellationToken)
    {
        var requestUri = $"fixtures/rounds?league={options.LeagueId}&season={options.Season}&current=true";
        var parsed = await SendAndParseAsync<ApiFootballRoundsResponse>(
            requestUri, "fetch the current gameweek's round name", cancellationToken);

        var roundName = parsed.Response?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(roundName))
        {
            // An API-Football account/league/season config problem (e.g. no
            // season configured for this league), not a legitimate "no
            // upcoming gameweek" state — REQ-1301's caller needs to know
            // this failed, not treat it as zero fixtures.
            logger.LogError(
                "API-Football fixtures/rounds returned no current round name for league {LeagueId} season {Season}.",
                options.LeagueId, options.Season);
            throw new ApiFootballClientException(
                "API-Football returned no current round name — check the ApiFootball:LeagueId/Season configuration.");
        }

        return roundName;
    }

    private async Task<IReadOnlyList<ApiFootballFixture>> FetchFixturesForRoundAsync(
        string roundName, CancellationToken cancellationToken)
    {
        var requestUri = $"fixtures?league={options.LeagueId}&season={options.Season}&round={Uri.EscapeDataString(roundName)}";
        var parsed = await SendAndParseAsync<ApiFootballFixturesResponse>(
            requestUri, $"fetch fixtures for round '{roundName}'", cancellationToken);

        var items = parsed.Response ?? [];
        var fixtures = new List<ApiFootballFixture>(items.Count);
        foreach (var item in items)
        {
            fixtures.Add(ParseFixture(item, roundName));
        }

        return fixtures;
    }

    private static ApiFootballFixture ParseFixture(ApiFootballFixtureItem item, string roundName)
    {
        // Bound to locals before the null/blank checks below — nullable
        // flow analysis reliably narrows a LOCAL variable through an
        // IsNullOrWhiteSpace/"is null" check (the same pattern
        // WikidataClient's own by-name lookups already rely on), unlike a
        // chained `item.Teams?.Home?.Name`-style property access, which
        // does not narrow the same way.
        var fixtureId = item.Fixture?.Id;
        var date = item.Fixture?.Date;
        var homeId = item.Teams?.Home?.Id;
        var homeName = item.Teams?.Home?.Name;
        var awayId = item.Teams?.Away?.Id;
        var awayName = item.Teams?.Away?.Name;

        if (fixtureId is null || string.IsNullOrWhiteSpace(date)
            || homeId is null || string.IsNullOrWhiteSpace(homeName)
            || awayId is null || string.IsNullOrWhiteSpace(awayName))
        {
            throw new ApiFootballClientException(
                $"API-Football returned a fixture with missing required fields for round '{roundName}'.");
        }

        DateTime kickoffUtc;
        try
        {
            kickoffUtc = DateTimeOffset.Parse(date).UtcDateTime;
        }
        catch (FormatException ex)
        {
            throw new ApiFootballClientException(
                $"API-Football returned an unparseable fixture date '{date}' for round '{roundName}'.", ex);
        }

        return new ApiFootballFixture(fixtureId.Value, homeId.Value, homeName, awayId.Value, awayName, kickoffUtc);
    }

    // Exact status-code-to-outcome mapping — compiled from API-Football's
    // own documented v3 fixture status codes, NOT verified against a live
    // response sample from this sandbox (see IApiFootballClient's own doc
    // comment for the same caveat). Kept as one small, easy-to-audit switch
    // so it's cheap to extend once a real response sample is available.
    private static ApiFootballFixtureOutcome ResolveOutcome(string statusShort) => statusShort switch
    {
        // FT = Match Finished, AET = After Extra Time, PEN = Finished after
        // penalties, AWD = Technical loss/win awarded, WO = WalkOver —
        // every one of these is a confirmed final result with a real score.
        "FT" or "AET" or "PEN" or "AWD" or "WO" => ApiFootballFixtureOutcome.Finished,

        // PST = Postponed, CANC = Cancelled, ABD = Abandoned — REQ-1305's
        // voided-match case; no real final score to grade against.
        "PST" or "CANC" or "ABD" => ApiFootballFixtureOutcome.PostponedOrAbandoned,

        // Everything else — NS (Not Started), 1H/2H/HT/ET/BT/P (in-progress
        // phases), SUSP (Suspended), INT (Interrupted), LIVE, and any status
        // code not enumerated above yet — a retry-later state, never a
        // permanent failure (ADR-0094's "For AI agents" section).
        _ => ApiFootballFixtureOutcome.NotYetConfirmed,
    };

    private void EnsureApiKeyConfigured()
    {
        if (string.IsNullOrWhiteSpace(apiKey.Value))
        {
            // Never a stack trace/exception detail here beyond this message
            // — an unconfigured key is an expected state until
            // ADR-0094 item 3's manual API-Football account/key setup has
            // happened in this environment, the same "expected state, not a
            // startup crash" reasoning GitHubIssueClient's own null-token
            // check already establishes.
            logger.LogError("API-Football request failed: ApiFootball:ApiKey is not configured.");
            throw new ApiFootballClientException("API-Football is not configured on this environment yet.");
        }
    }

    private async Task<T> SendAndParseAsync<T>(string requestUri, string actionDescription, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        // The real API-Football direct-host auth header — set per-request,
        // never on httpClient's own DefaultRequestHeaders (see this class's
        // own doc comment).
        request.Headers.Add("x-apisports-key", apiKey.Value);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                // Full detail (status + API-Football's own response body)
                // logged server-side only — this is a server-side/job-only
                // client with no player-facing caller to leak it to, but
                // the same "don't surface raw provider error bodies"
                // discipline as GitHubIssueClient still applies to the
                // exception message thrown below.
                logger.LogError(
                    "API-Football request to {RequestUri} failed ({StatusCode}) trying to {Action}: {ResponseBody}",
                    requestUri, response.StatusCode, actionDescription, responseBody);
                throw new ApiFootballClientException(
                    $"API-Football returned {(int)response.StatusCode} trying to {actionDescription}.");
            }

            var parsed = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
            if (parsed is null)
            {
                logger.LogError(
                    "API-Football returned a success status with no parseable body trying to {Action} ({RequestUri}).",
                    actionDescription, requestUri);
                throw new ApiFootballClientException($"API-Football returned an unparseable response trying to {actionDescription}.");
            }

            return parsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Network failure/timeout — same catch shape as
            // GitHubIssueClient.CreateIssueAsync (this class's own
            // template).
            logger.LogError(ex, "API-Football request to {RequestUri} failed trying to {Action}.", requestUri, actionDescription);
            throw new ApiFootballClientException($"Could not reach API-Football trying to {actionDescription}.", ex);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "API-Football response for {RequestUri} was unparseable trying to {Action}.", requestUri, actionDescription);
            throw new ApiFootballClientException($"API-Football returned an unparseable response trying to {actionDescription}.", ex);
        }
    }

    private record ApiFootballRoundsResponse(
        [property: JsonPropertyName("response")] IReadOnlyList<string>? Response);

    private record ApiFootballFixturesResponse(
        [property: JsonPropertyName("response")] IReadOnlyList<ApiFootballFixtureItem>? Response);

    // Shared item shape for both the round-fixture-list call (which
    // populates Fixture/Teams) and the by-id result call (which populates
    // Fixture/Goals) — API-Football's own envelope is identical for both
    // ("{ response: [...] }"), only which nested fields are populated
    // differs by endpoint.
    private record ApiFootballFixtureItem(
        [property: JsonPropertyName("fixture")] ApiFootballFixtureDetail? Fixture,
        [property: JsonPropertyName("teams")] ApiFootballTeams? Teams,
        [property: JsonPropertyName("goals")] ApiFootballGoals? Goals);

    private record ApiFootballFixtureDetail(
        [property: JsonPropertyName("id")] int? Id,
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("status")] ApiFootballFixtureStatus? Status);

    private record ApiFootballFixtureStatus(
        [property: JsonPropertyName("short")] string? Short,
        [property: JsonPropertyName("long")] string? Long);

    private record ApiFootballTeams(
        [property: JsonPropertyName("home")] ApiFootballTeam? Home,
        [property: JsonPropertyName("away")] ApiFootballTeam? Away);

    private record ApiFootballTeam(
        [property: JsonPropertyName("id")] int? Id,
        [property: JsonPropertyName("name")] string? Name);

    private record ApiFootballGoals(
        [property: JsonPropertyName("home")] int? Home,
        [property: JsonPropertyName("away")] int? Away);
}

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.DataSync.FootballData;
using XGArcade.TestSupport;

namespace XGArcade.DataSync.Tests.FootballData;

// ADR-0099/REQ-1301/REQ-1305: FootballDataClient's own unit coverage — never
// calls the real football-data.org API (a fake HttpMessageHandler stands
// in, same pattern GitHubIssueClientTests.cs/WikidataClientTests.cs already
// use). No REQ-xxx exists yet for the client's own request-building/
// parsing behavior specifically (it's the plumbing round-generation/
// grading build on), same "no REQ needed for plumbing" precedent
// WikidataClientTests.cs itself documents. Replaces ApiFootballClientTests.cs
// (ADR-0094) — see ADR-0099 for why API-Football was swapped out.
public class FootballDataClientTests
{
    private static readonly FootballDataOptions Options = new("PL");

    private static FootballDataClient BuildClient(
        HttpMessageHandler handler, string? apiKey = "a-test-api-key", FootballDataOptions? options = null) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.football-data.org/v4/") },
            new FootballDataApiKey(apiKey),
            options ?? Options,
            NullLogger<FootballDataClient>.Instance);

    // Scripts two different responses by request path — FakeHttpMessageHandler's
    // constructor takes the responder delegate directly, so this is a plain
    // extension of its existing style rather than a change to the shared
    // class itself: GetUpcomingGameweekFixturesAsync makes two sequential
    // calls (competitions/{code}, then competitions/{code}/matches), which
    // land on two distinct AbsolutePaths.
    private static FakeHttpMessageHandler BuildTwoCallHandler(string competitionJson, string matchesJson) =>
        new((request, _) =>
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/matches", StringComparison.Ordinal)
                ? matchesJson : competitionJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        });

    // ---- GetUpcomingGameweekFixturesAsync (REQ-1301) -----------------------

    [Test]
    public async Task GetUpcomingGameweekFixturesAsync_HappyPath_ParsesFixtureIdsTeamsAndUtcKickoff()
    {
        const string competitionJson = """{ "currentSeason": { "currentMatchday": 4 } }""";
        const string matchesJson = """
        {
          "matches": [
            {
              "id": 1000001, "utcDate": "2026-09-12T14:00:00Z", "status": "SCHEDULED",
              "homeTeam": { "id": 33, "name": "Manchester United" }, "awayTeam": { "id": 34, "name": "Newcastle" }
            },
            {
              "id": 1000002, "utcDate": "2026-09-13T10:30:00Z", "status": "SCHEDULED",
              "homeTeam": { "id": 40, "name": "Liverpool" }, "awayTeam": { "id": 50, "name": "Arsenal" }
            }
          ]
        }
        """;
        var handler = BuildTwoCallHandler(competitionJson, matchesJson);
        var client = BuildClient(handler);

        var result = await client.GetUpcomingGameweekFixturesAsync();

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].FixtureId, Is.EqualTo(1000001));
        Assert.That(result[0].HomeTeamId, Is.EqualTo(33));
        Assert.That(result[0].HomeTeamName, Is.EqualTo("Manchester United"));
        Assert.That(result[0].AwayTeamId, Is.EqualTo(34));
        Assert.That(result[0].AwayTeamName, Is.EqualTo("Newcastle"));
        Assert.That(result[0].KickoffUtc, Is.EqualTo(new DateTime(2026, 9, 12, 14, 0, 0, DateTimeKind.Utc)));
        Assert.That(result[1].KickoffUtc, Is.EqualTo(new DateTime(2026, 9, 13, 10, 30, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task GetUpcomingGameweekFixturesAsync_DoesNotFilterOrSliceTheMatchdayFixtureList()
    {
        // REQ-1301's tightest-kickoff-clustering 5-match selection is
        // explicitly out of scope here — this client returns the whole
        // matchday's fixture list unchanged, whatever size it is.
        const string competitionJson = """{ "currentSeason": { "currentMatchday": 4 } }""";
        const string matchesJson = """
        {
          "matches": [
            { "id": 1, "utcDate": "2026-09-12T14:00:00Z", "status": "SCHEDULED", "homeTeam": { "id": 1, "name": "A" }, "awayTeam": { "id": 2, "name": "B" } },
            { "id": 2, "utcDate": "2026-09-12T14:00:00Z", "status": "SCHEDULED", "homeTeam": { "id": 3, "name": "C" }, "awayTeam": { "id": 4, "name": "D" } },
            { "id": 3, "utcDate": "2026-09-12T14:00:00Z", "status": "SCHEDULED", "homeTeam": { "id": 5, "name": "E" }, "awayTeam": { "id": 6, "name": "F" } }
          ]
        }
        """;
        var client = BuildClient(BuildTwoCallHandler(competitionJson, matchesJson));

        var result = await client.GetUpcomingGameweekFixturesAsync();

        Assert.That(result, Has.Count.EqualTo(3), "must return the whole matchday's list, not a 5-match (or any other) subset");
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_NoCurrentMatchday_Throws()
    {
        const string competitionJson = """{ "currentSeason": { "currentMatchday": null } }""";
        var client = BuildClient(BuildTwoCallHandler(competitionJson, """{ "matches": [] }"""));

        Assert.ThrowsAsync<FootballDataClientException>(async () => await client.GetUpcomingGameweekFixturesAsync());
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_MissingCurrentSeasonField_Throws()
    {
        const string competitionJson = """{ "id": 2021, "name": "Premier League" }""";
        var client = BuildClient(BuildTwoCallHandler(competitionJson, """{ "matches": [] }"""));

        Assert.ThrowsAsync<FootballDataClientException>(async () => await client.GetUpcomingGameweekFixturesAsync());
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_NonSuccessHttpStatus_Throws()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.InternalServerError);
        var client = BuildClient(handler);

        Assert.ThrowsAsync<FootballDataClientException>(async () => await client.GetUpcomingGameweekFixturesAsync());
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_MalformedJson_Throws()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, "{ this is not valid json");
        var client = BuildClient(handler);

        Assert.ThrowsAsync<FootballDataClientException>(async () => await client.GetUpcomingGameweekFixturesAsync());
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_NetworkFailure_Throws()
    {
        var client = BuildClient(new FakeHttpMessageHandlerThrowingNetworkFailure());

        Assert.ThrowsAsync<FootballDataClientException>(async () => await client.GetUpcomingGameweekFixturesAsync());
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_FixtureWithMissingRequiredField_Throws()
    {
        const string competitionJson = """{ "currentSeason": { "currentMatchday": 4 } }""";
        const string matchesJson = """
        {
          "matches": [
            { "id": 1, "status": "SCHEDULED", "homeTeam": { "id": 1, "name": "A" }, "awayTeam": { "id": 2, "name": "B" } }
          ]
        }
        """;
        var client = BuildClient(BuildTwoCallHandler(competitionJson, matchesJson));

        Assert.ThrowsAsync<FootballDataClientException>(async () => await client.GetUpcomingGameweekFixturesAsync(),
            "the fixture item above has no utcDate at all");
    }

    [Test]
    public async Task GetUpcomingGameweekFixturesAsync_SendsApiKeyHeader_AndConfiguredCompetitionCodeMatchday()
    {
        const string competitionJson = """{ "currentSeason": { "currentMatchday": 4 } }""";
        const string matchesJson = """{ "matches": [] }""";
        var handler = BuildTwoCallHandler(competitionJson, matchesJson);
        var client = BuildClient(handler, apiKey: "a-test-api-key", options: new FootballDataOptions("PL"));

        await client.GetUpcomingGameweekFixturesAsync();

        Assert.That(handler.LastRequest!.Headers.GetValues("X-Auth-Token"), Is.EqualTo(new[] { "a-test-api-key" }));
        Assert.That(handler.LastRequest.RequestUri!.AbsoluteUri, Is.EqualTo(
            "https://api.football-data.org/v4/competitions/PL/matches?matchday=4"));
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_UnconfiguredApiKey_ThrowsWithoutSendingAnyRequest()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, """{ "currentSeason": { "currentMatchday": 4 } }""");
        var client = BuildClient(handler, apiKey: null);

        Assert.ThrowsAsync<FootballDataClientException>(async () => await client.GetUpcomingGameweekFixturesAsync());
        Assert.That(handler.LastRequest, Is.Null, "an unconfigured API key must never send a request to football-data.org at all");
    }

    // ---- GetFixtureResultAsync (REQ-1305) -----------------------------------

    [Test]
    public async Task GetFixtureResultAsync_FinishedStatus_ParsesHomeAndAwayGoals()
    {
        const string json = """
        { "id": 12345, "status": "FINISHED", "homeTeam": { "id": 1, "name": "A" }, "awayTeam": { "id": 2, "name": "B" }, "score": { "fullTime": { "home": 2, "away": 1 } } }
        """;
        var client = BuildClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, json));

        var result = await client.GetFixtureResultAsync(12345);

        Assert.That(result.Outcome, Is.EqualTo(FootballDataFixtureOutcome.Finished));
        Assert.That(result.RawStatus, Is.EqualTo("FINISHED"));
        Assert.That(result.HomeGoals, Is.EqualTo(2));
        Assert.That(result.AwayGoals, Is.EqualTo(1));
    }

    [Test]
    public async Task GetFixtureResultAsync_AwardedStatus_MapsToFinished()
    {
        const string json = """
        { "id": 12345, "status": "AWARDED", "score": { "fullTime": { "home": 3, "away": 0 } } }
        """;
        var client = BuildClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, json));

        var result = await client.GetFixtureResultAsync(12345);

        Assert.That(result.Outcome, Is.EqualTo(FootballDataFixtureOutcome.Finished));
    }

    [TestCase("POSTPONED")]
    [TestCase("CANCELLED")]
    [TestCase("SUSPENDED")]
    public async Task GetFixtureResultAsync_PostponedOrAbandonedStatusCodes_MapToPostponedOrAbandoned(string status)
    {
        var json = $$"""{ "id": 12345, "status": "{{status}}", "score": { "fullTime": { "home": null, "away": null } } }""";
        var client = BuildClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, json));

        var result = await client.GetFixtureResultAsync(12345);

        Assert.That(result.Outcome, Is.EqualTo(FootballDataFixtureOutcome.PostponedOrAbandoned));
        Assert.That(result.RawStatus, Is.EqualTo(status));
    }

    [TestCase("SCHEDULED")]
    [TestCase("IN_PLAY")]
    [TestCase("PAUSED")]
    public async Task GetFixtureResultAsync_InProgressOrNotStartedStatusCodes_MapToNotYetConfirmed(string status)
    {
        var json = $$"""{ "id": 12345, "status": "{{status}}", "score": { "fullTime": { "home": null, "away": null } } }""";
        var client = BuildClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, json));

        var result = await client.GetFixtureResultAsync(12345);

        Assert.That(result.Outcome, Is.EqualTo(FootballDataFixtureOutcome.NotYetConfirmed));
    }

    [Test]
    public void GetFixtureResultAsync_MissingStatusField_Throws()
    {
        const string json = """{ "id": 12345 }""";
        var client = BuildClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, json));

        Assert.ThrowsAsync<FootballDataClientException>(async () => await client.GetFixtureResultAsync(12345));
    }

    [Test]
    public void GetFixtureResultAsync_UnknownFixtureId_404_Throws()
    {
        // football-data.org's real "no record of this fixture" shape — a
        // 404 status, not a 200-with-empty-array the way API-Football's
        // predecessor client (ADR-0094) modeled it. Already covered by the
        // generic non-success-status handling in SendAndParseAsync, but
        // worth its own named test for REQ-1305's own "distinguishable
        // technical/data problem, not a retry-later state" contract.
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.NotFound);
        var client = BuildClient(handler);

        Assert.ThrowsAsync<FootballDataClientException>(async () => await client.GetFixtureResultAsync(12345),
            "REQ-1305: 'football-data.org has no record of this fixture' must never be silently treated as 'not yet confirmed, retry later'");
    }

    [Test]
    public void GetFixtureResultAsync_NonSuccessHttpStatus_Throws()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.Unauthorized);
        var client = BuildClient(handler);

        Assert.ThrowsAsync<FootballDataClientException>(async () => await client.GetFixtureResultAsync(12345));
    }

    [Test]
    public void GetFixtureResultAsync_MalformedJson_Throws()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, "not json at all");
        var client = BuildClient(handler);

        Assert.ThrowsAsync<FootballDataClientException>(async () => await client.GetFixtureResultAsync(12345));
    }

    [Test]
    public void GetFixtureResultAsync_NetworkFailure_Throws()
    {
        var client = BuildClient(new FakeHttpMessageHandlerThrowingNetworkFailure());

        Assert.ThrowsAsync<FootballDataClientException>(async () => await client.GetFixtureResultAsync(12345));
    }

    [Test]
    public async Task GetFixtureResultAsync_SendsApiKeyHeader_AndFixtureIdInPath()
    {
        const string json = """{ "id": 12345, "status": "SCHEDULED" }""";
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, json);
        var client = BuildClient(handler, apiKey: "a-test-api-key");

        await client.GetFixtureResultAsync(12345);

        Assert.That(handler.LastRequest!.Headers.GetValues("X-Auth-Token"), Is.EqualTo(new[] { "a-test-api-key" }));
        Assert.That(handler.LastRequest.RequestUri!.ToString(), Is.EqualTo("https://api.football-data.org/v4/matches/12345"));
    }

    [Test]
    public void GetFixtureResultAsync_UnconfiguredApiKey_ThrowsWithoutSendingAnyRequest()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, """{ "id": 12345, "status": "SCHEDULED" }""");
        var client = BuildClient(handler, apiKey: "   ");

        Assert.ThrowsAsync<FootballDataClientException>(async () => await client.GetFixtureResultAsync(12345));
        Assert.That(handler.LastRequest, Is.Null, "an unconfigured (blank) API key must never send a request to football-data.org at all");
    }
}

// A minimal handler that always throws HttpRequestException, standing in
// for a genuine network failure (DNS/connection refused/etc.) — mirrors
// GitHubIssueClientTests.cs's own FakeHttpMessageHandlerThrowingNetworkFailure
// (FakeHttpMessageHandler's own factory methods only cover a real HTTP
// response, not a transport-level failure, and that one is internal to
// XGArcade.Core.Tests's namespace).
internal sealed class FakeHttpMessageHandlerThrowingNetworkFailure : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("simulated network failure");
}

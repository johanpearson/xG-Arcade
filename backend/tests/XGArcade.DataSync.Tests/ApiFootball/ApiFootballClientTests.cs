using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.DataSync.ApiFootball;
using XGArcade.TestSupport;

namespace XGArcade.DataSync.Tests.ApiFootball;

// ADR-0094/REQ-1301/REQ-1305: ApiFootballClient's own unit coverage — never
// calls the real API-Football API (a fake HttpMessageHandler stands in,
// same pattern GitHubIssueClientTests.cs/WikidataClientTests.cs already
// use). No REQ-xxx exists yet for the client's own request-building/
// parsing behavior specifically (it's the plumbing a future round-
// generation/grading story builds on), same "no REQ needed for plumbing"
// precedent WikidataClientTests.cs itself documents.
public class ApiFootballClientTests
{
    private static readonly ApiFootballOptions Options = new(LeagueId: 39, Season: 2026);

    private static ApiFootballClient BuildClient(
        HttpMessageHandler handler, string? apiKey = "a-test-api-key", ApiFootballOptions? options = null) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://v3.football.api-sports.io/") },
            new ApiFootballApiKey(apiKey),
            options ?? Options,
            NullLogger<ApiFootballClient>.Instance);

    // Scripts two different responses by request path — FakeHttpMessageHandler's
    // constructor takes the responder delegate directly, so this is a plain
    // extension of its existing style rather than a change to the shared
    // class itself: GetUpcomingGameweekFixturesAsync makes two sequential
    // calls (fixtures/rounds, then fixtures?round=...), which land on two
    // distinct AbsolutePaths ("/fixtures/rounds" vs "/fixtures").
    private static FakeHttpMessageHandler BuildTwoCallHandler(string roundsJson, string fixturesJson) =>
        new((request, _) =>
        {
            var body = request.RequestUri!.AbsolutePath == "/fixtures/rounds" ? roundsJson : fixturesJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        });

    // ---- GetUpcomingGameweekFixturesAsync (REQ-1301) -----------------------

    [Test]
    public async Task GetUpcomingGameweekFixturesAsync_HappyPath_ParsesFixtureIdsTeamsAndUtcKickoff()
    {
        const string roundsJson = """{ "response": ["Regular Season - 4"] }""";
        const string fixturesJson = """
        {
          "response": [
            {
              "fixture": { "id": 1000001, "date": "2026-09-12T14:00:00+00:00", "status": { "short": "NS", "long": "Not Started" } },
              "teams": { "home": { "id": 33, "name": "Manchester United" }, "away": { "id": 34, "name": "Newcastle" } }
            },
            {
              "fixture": { "id": 1000002, "date": "2026-09-13T11:30:00+01:00", "status": { "short": "NS", "long": "Not Started" } },
              "teams": { "home": { "id": 40, "name": "Liverpool" }, "away": { "id": 50, "name": "Arsenal" } }
            }
          ]
        }
        """;
        var handler = BuildTwoCallHandler(roundsJson, fixturesJson);
        var client = BuildClient(handler);

        var result = await client.GetUpcomingGameweekFixturesAsync();

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].FixtureId, Is.EqualTo(1000001));
        Assert.That(result[0].HomeTeamId, Is.EqualTo(33));
        Assert.That(result[0].HomeTeamName, Is.EqualTo("Manchester United"));
        Assert.That(result[0].AwayTeamId, Is.EqualTo(34));
        Assert.That(result[0].AwayTeamName, Is.EqualTo("Newcastle"));
        Assert.That(result[0].KickoffUtc, Is.EqualTo(new DateTime(2026, 9, 12, 14, 0, 0, DateTimeKind.Utc)));

        // A non-zero offset ("+01:00") must be normalized to UTC, not kept as-is.
        Assert.That(result[1].KickoffUtc, Is.EqualTo(new DateTime(2026, 9, 13, 10, 30, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task GetUpcomingGameweekFixturesAsync_DoesNotFilterOrSliceTheRoundsFixtureList()
    {
        // REQ-1301's tightest-kickoff-clustering 5-match selection is
        // explicitly out of scope here — this client returns the whole
        // round's fixture list unchanged, whatever size it is.
        const string roundsJson = """{ "response": ["Regular Season - 4"] }""";
        const string fixturesJson = """
        {
          "response": [
            { "fixture": { "id": 1, "date": "2026-09-12T14:00:00+00:00", "status": { "short": "NS" } }, "teams": { "home": { "id": 1, "name": "A" }, "away": { "id": 2, "name": "B" } } },
            { "fixture": { "id": 2, "date": "2026-09-12T14:00:00+00:00", "status": { "short": "NS" } }, "teams": { "home": { "id": 3, "name": "C" }, "away": { "id": 4, "name": "D" } } },
            { "fixture": { "id": 3, "date": "2026-09-12T14:00:00+00:00", "status": { "short": "NS" } }, "teams": { "home": { "id": 5, "name": "E" }, "away": { "id": 6, "name": "F" } } }
          ]
        }
        """;
        var client = BuildClient(BuildTwoCallHandler(roundsJson, fixturesJson));

        var result = await client.GetUpcomingGameweekFixturesAsync();

        Assert.That(result, Has.Count.EqualTo(3), "must return the whole round's list, not a 5-match (or any other) subset");
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_EmptyRoundNameResponse_Throws()
    {
        const string roundsJson = """{ "response": [] }""";
        var client = BuildClient(BuildTwoCallHandler(roundsJson, """{ "response": [] }"""));

        Assert.ThrowsAsync<ApiFootballClientException>(async () => await client.GetUpcomingGameweekFixturesAsync());
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_MissingRoundsField_Throws()
    {
        const string roundsJson = """{ "results": 0 }""";
        var client = BuildClient(BuildTwoCallHandler(roundsJson, """{ "response": [] }"""));

        Assert.ThrowsAsync<ApiFootballClientException>(async () => await client.GetUpcomingGameweekFixturesAsync());
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_NonSuccessHttpStatus_Throws()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.InternalServerError);
        var client = BuildClient(handler);

        Assert.ThrowsAsync<ApiFootballClientException>(async () => await client.GetUpcomingGameweekFixturesAsync());
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_MalformedJson_Throws()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, "{ this is not valid json");
        var client = BuildClient(handler);

        Assert.ThrowsAsync<ApiFootballClientException>(async () => await client.GetUpcomingGameweekFixturesAsync());
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_NetworkFailure_Throws()
    {
        var client = BuildClient(new FakeHttpMessageHandlerThrowingNetworkFailure());

        Assert.ThrowsAsync<ApiFootballClientException>(async () => await client.GetUpcomingGameweekFixturesAsync());
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_FixtureWithMissingRequiredField_Throws()
    {
        const string roundsJson = """{ "response": ["Regular Season - 4"] }""";
        const string fixturesJson = """
        {
          "response": [
            { "fixture": { "id": 1, "status": { "short": "NS" } }, "teams": { "home": { "id": 1, "name": "A" }, "away": { "id": 2, "name": "B" } } }
          ]
        }
        """;
        var client = BuildClient(BuildTwoCallHandler(roundsJson, fixturesJson));

        Assert.ThrowsAsync<ApiFootballClientException>(async () => await client.GetUpcomingGameweekFixturesAsync(),
            "the fixture item above has no fixture.date at all");
    }

    [Test]
    public async Task GetUpcomingGameweekFixturesAsync_SendsApiKeyHeader_AndConfiguredLeagueSeasonRound()
    {
        const string roundsJson = """{ "response": ["Regular Season - 4"] }""";
        const string fixturesJson = """{ "response": [] }""";
        var handler = BuildTwoCallHandler(roundsJson, fixturesJson);
        var client = BuildClient(handler, apiKey: "a-test-api-key", options: new ApiFootballOptions(39, 2026));

        await client.GetUpcomingGameweekFixturesAsync();

        Assert.That(handler.LastRequest!.Headers.GetValues("x-apisports-key"), Is.EqualTo(new[] { "a-test-api-key" }));
        Assert.That(handler.LastRequest.RequestUri!.AbsoluteUri, Is.EqualTo(
            "https://v3.football.api-sports.io/fixtures?league=39&season=2026&round=Regular%20Season%20-%204"));
    }

    [Test]
    public void GetUpcomingGameweekFixturesAsync_UnconfiguredApiKey_ThrowsWithoutSendingAnyRequest()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, """{ "response": [] }""");
        var client = BuildClient(handler, apiKey: null);

        Assert.ThrowsAsync<ApiFootballClientException>(async () => await client.GetUpcomingGameweekFixturesAsync());
        Assert.That(handler.LastRequest, Is.Null, "an unconfigured API key must never send a request to API-Football at all");
    }

    // ---- GetFixtureResultAsync (REQ-1305) -----------------------------------

    [Test]
    public async Task GetFixtureResultAsync_FinishedStatus_ParsesHomeAndAwayGoals()
    {
        const string json = """
        {
          "response": [
            { "fixture": { "id": 12345, "status": { "short": "FT", "long": "Match Finished" } }, "goals": { "home": 2, "away": 1 } }
          ]
        }
        """;
        var client = BuildClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, json));

        var result = await client.GetFixtureResultAsync(12345);

        Assert.That(result.Outcome, Is.EqualTo(ApiFootballFixtureOutcome.Finished));
        Assert.That(result.RawStatusShort, Is.EqualTo("FT"));
        Assert.That(result.HomeGoals, Is.EqualTo(2));
        Assert.That(result.AwayGoals, Is.EqualTo(1));
    }

    [TestCase("PST")]
    [TestCase("ABD")]
    public async Task GetFixtureResultAsync_PostponedOrAbandonedStatusCodes_MapToPostponedOrAbandoned(string statusShort)
    {
        var json = $$"""
        {
          "response": [
            { "fixture": { "id": 12345, "status": { "short": "{{statusShort}}", "long": "whatever" } }, "goals": { "home": null, "away": null } }
          ]
        }
        """;
        var client = BuildClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, json));

        var result = await client.GetFixtureResultAsync(12345);

        Assert.That(result.Outcome, Is.EqualTo(ApiFootballFixtureOutcome.PostponedOrAbandoned));
        Assert.That(result.RawStatusShort, Is.EqualTo(statusShort));
    }

    [TestCase("NS")]
    [TestCase("1H")]
    public async Task GetFixtureResultAsync_InProgressOrNotStartedStatusCodes_MapToNotYetConfirmed(string statusShort)
    {
        var json = $$"""
        {
          "response": [
            { "fixture": { "id": 12345, "status": { "short": "{{statusShort}}", "long": "whatever" } }, "goals": { "home": null, "away": null } }
          ]
        }
        """;
        var client = BuildClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, json));

        var result = await client.GetFixtureResultAsync(12345);

        Assert.That(result.Outcome, Is.EqualTo(ApiFootballFixtureOutcome.NotYetConfirmed));
    }

    [Test]
    public void GetFixtureResultAsync_EmptyResponseArray_ThrowsRatherThanReturningNotYetConfirmed()
    {
        const string json = """{ "response": [] }""";
        var client = BuildClient(FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, json));

        Assert.ThrowsAsync<ApiFootballClientException>(async () => await client.GetFixtureResultAsync(12345),
            "REQ-1305: 'API-Football has no record of this fixture' must never be silently treated as 'not yet confirmed, retry later'");
    }

    [Test]
    public void GetFixtureResultAsync_NonSuccessHttpStatus_Throws()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.Unauthorized);
        var client = BuildClient(handler);

        Assert.ThrowsAsync<ApiFootballClientException>(async () => await client.GetFixtureResultAsync(12345));
    }

    [Test]
    public void GetFixtureResultAsync_MalformedJson_Throws()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, "not json at all");
        var client = BuildClient(handler);

        Assert.ThrowsAsync<ApiFootballClientException>(async () => await client.GetFixtureResultAsync(12345));
    }

    [Test]
    public void GetFixtureResultAsync_NetworkFailure_Throws()
    {
        var client = BuildClient(new FakeHttpMessageHandlerThrowingNetworkFailure());

        Assert.ThrowsAsync<ApiFootballClientException>(async () => await client.GetFixtureResultAsync(12345));
    }

    [Test]
    public void GetFixtureResultAsync_SendsApiKeyHeader_AndFixtureIdQueryParam()
    {
        const string json = """{ "response": [] }""";
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, json);
        var client = BuildClient(handler, apiKey: "a-test-api-key");

        // Empty response throws (see the "no record" test above) — this
        // test only cares about the request that was sent before that.
        Assert.ThrowsAsync<ApiFootballClientException>(async () => await client.GetFixtureResultAsync(12345));

        Assert.That(handler.LastRequest!.Headers.GetValues("x-apisports-key"), Is.EqualTo(new[] { "a-test-api-key" }));
        Assert.That(handler.LastRequest.RequestUri!.ToString(), Is.EqualTo("https://v3.football.api-sports.io/fixtures?id=12345"));
    }

    [Test]
    public void GetFixtureResultAsync_UnconfiguredApiKey_ThrowsWithoutSendingAnyRequest()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, """{ "response": [] }""");
        var client = BuildClient(handler, apiKey: "   ");

        Assert.ThrowsAsync<ApiFootballClientException>(async () => await client.GetFixtureResultAsync(12345));
        Assert.That(handler.LastRequest, Is.Null, "an unconfigured (blank) API key must never send a request to API-Football at all");
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

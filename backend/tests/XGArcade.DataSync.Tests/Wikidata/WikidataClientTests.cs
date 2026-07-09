using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// S-006 (docs/backlog.md): no REQ-xxx exists yet for the client's own
// query-building/parsing behavior (it's the plumbing REQ-103's persistence
// tests build on, in WikidataLookupServiceTests), same pattern as
// PlayerStoreRepositoryTests.
public class WikidataClientTests
{
    private const string CountryQid = "Q142"; // France
    private const string ClubQid = "Q9617";   // Arsenal

    private static HttpClient BuildHttpClient(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://query.wikidata.org/") };

    [Test]
    public async Task QueryCountryClubIntersectionAsync_GroupsMultipleAliasRowsUnderOnePlayer()
    {
        const string json = """
            {
              "head": { "vars": ["player", "playerLabel", "alias"] },
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "Titi" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "TH14" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].WikidataQid, Is.EqualTo("Q1519"));
        Assert.That(result[0].FullName, Is.EqualTo("Thierry Henry"));
        Assert.That(result[0].Aliases, Is.EquivalentTo(new[] { "Titi", "TH14" }));
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_PlayerWithNoAlias_ReturnsEmptyAliasList()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Aliases, Is.Empty);
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_NoMatchingRows_ReturnsEmptyWithoutThrowing()
    {
        const string json = """{ "results": { "bindings": [] } }""";
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_HttpErrorStatus_ReturnsEmptyWithoutThrowing()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningStatus(System.Net.HttpStatusCode.InternalServerError)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_Timeout_ReturnsEmptyWithoutThrowing()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_MalformedJson_ReturnsEmptyWithoutThrowing()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("not valid json")));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_SentQuery_NeverContainsLimit()
    {
        // Non-negotiable, implementation-document.md §6a: the intersection
        // query's results ARE the cell's complete answer key — a LIMIT
        // would silently reintroduce the correct-guess-marked-wrong bug
        // REQ-211 exists to fix.
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Not.Contain("LIMIT"));
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_SentQuery_FetchesSkosAltLabelInTheSameQuery()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("skos:altLabel"));
    }

    [Test]
    public void QueryCountryClubIntersectionAsync_RejectsNonQidCountryValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryCountryClubIntersectionAsync("France", ClubQid));
    }
}

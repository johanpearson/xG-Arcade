using System.Text.RegularExpressions;
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
    private const string ClubAQid = "Q9617";  // Arsenal
    private const string ClubBQid = "Q7156";  // Barcelona

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
    public async Task QueryCountryClubIntersectionAsync_SentQuery_FiltersToMaleOnly()
    {
        // ADR-0025/REQ-112: Q6581097 is Wikidata's "male" item for P21 (sex
        // or gender).
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P21 wd:Q6581097"));
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_SentQuery_FiltersToDateOfBirthOnOrAfter1939()
    {
        // ADR-0025/REQ-112: a fixed date, not a rolling window — the sent
        // cutoff is always 1939-01-01, regardless of when the query runs.
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P569"));
        Assert.That(sentQuery, Does.Contain("\"1939-01-01T00:00:00Z\"^^xsd:dateTime"));
    }

    [Test]
    public async Task REQ113_QueryCountryClubIntersectionAsync_SentQuery_MatchesClubViaFullStatementPathExcludingOnlyDeprecatedRank()
    {
        // "Ever played for this club" (REQ-113 semantics; REQ-101/REQ-203's
        // correctness contract): the truthy wdt:P54 shortcut silently drops
        // every normal-rank historical club the moment a player's current
        // club is marked preferred rank, so the club match must go through
        // the full statement path (p:P54/ps:P54), excluding only deprecated
        // rank — see BuildCountryClubIntersectionQuery's own comment for
        // the Sandro Tonali x AC Milan incident this pins down.
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("?player p:P54 ?clubStatement."));
        Assert.That(sentQuery, Does.Contain($"?clubStatement ps:P54 wd:{ClubQid}."));
        Assert.That(sentQuery, Does.Contain("MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }"));
        Assert.That(sentQuery, Does.Not.Contain("wdt:P54"),
            "truthy wdt:P54 is best-rank-only — reintroducing it silently reduces 'ever played for' to 'currently plays for' whenever a current club is preferred rank");
    }

    [Test]
    public void QueryCountryClubIntersectionAsync_RejectsNonQidCountryValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryCountryClubIntersectionAsync("France", ClubQid));
    }

    [Test]
    public void QueryCountryClubIntersectionAsync_RejectsNonQidClubValue()
    {
        // Separate branch from the country check above (two independent
        // `if` guards in WikidataClient) — not guaranteed by symmetry.
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryCountryClubIntersectionAsync(CountryQid, "Arsenal"));
    }

    // ---- QueryClubClubIntersectionAsync (S-030) ----------------------------
    // Mirrors every QueryCountryClubIntersectionAsync_* test above — same
    // parsing/error-handling code path (RunIntersectionQueryAsync), just a
    // different query builder (BuildClubClubIntersectionQuery, P54 checked
    // twice instead of P27+P54).

    [Test]
    public async Task QueryClubClubIntersectionAsync_GroupsMultipleAliasRowsUnderOnePlayer()
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

        var result = await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].WikidataQid, Is.EqualTo("Q1519"));
        Assert.That(result[0].FullName, Is.EqualTo("Thierry Henry"));
        Assert.That(result[0].Aliases, Is.EquivalentTo(new[] { "Titi", "TH14" }));
    }

    [Test]
    public async Task QueryClubClubIntersectionAsync_PlayerWithNoAlias_ReturnsEmptyAliasList()
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

        var result = await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Aliases, Is.Empty);
    }

    [Test]
    public async Task QueryClubClubIntersectionAsync_NoMatchingRows_ReturnsEmptyWithoutThrowing()
    {
        const string json = """{ "results": { "bindings": [] } }""";
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryClubClubIntersectionAsync_HttpErrorStatus_ReturnsEmptyWithoutThrowing()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningStatus(System.Net.HttpStatusCode.InternalServerError)));

        var result = await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryClubClubIntersectionAsync_Timeout_ReturnsEmptyWithoutThrowing()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));

        var result = await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryClubClubIntersectionAsync_MalformedJson_ReturnsEmptyWithoutThrowing()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("not valid json")));

        var result = await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryClubClubIntersectionAsync_SentQuery_NeverContainsLimit()
    {
        // Same non-negotiable rule as QueryCountryClubIntersectionAsync
        // (implementation-document.md §6a): the result set IS the cell's
        // complete answer key.
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Not.Contain("LIMIT"));
    }

    [Test]
    public async Task QueryClubClubIntersectionAsync_SentQuery_FetchesSkosAltLabelInTheSameQuery()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("skos:altLabel"));
    }

    [Test]
    public async Task REQ113_QueryClubClubIntersectionAsync_SentQuery_ChecksP54StatementPathTwiceWithDistinctVariablesAndNeverP27()
    {
        // S-030: Club x Club's query shape checks "member of sports team"
        // (P54) against both clubs, unlike Country x Club's P27+P54 —
        // asserted explicitly since a copy-paste of the country/club query
        // builder that forgot to swap P27 for a second P54 would otherwise
        // silently produce a Country-shaped query for a Club x Club cell.
        // Both checks must use the full statement path (p:P54/ps:P54,
        // deprecated rank excluded), never truthy wdt:P54 — see the
        // country/club statement-path test above — and each club needs its
        // OWN statement variable: one shared variable could never bind,
        // since a single P54 statement can't point at two clubs.
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("?player p:P54 ?clubAStatement."));
        Assert.That(sentQuery, Does.Contain($"?clubAStatement ps:P54 wd:{ClubAQid}."));
        Assert.That(sentQuery, Does.Contain("MINUS { ?clubAStatement wikibase:rank wikibase:DeprecatedRank. }"));
        Assert.That(sentQuery, Does.Contain("?player p:P54 ?clubBStatement."));
        Assert.That(sentQuery, Does.Contain($"?clubBStatement ps:P54 wd:{ClubBQid}."));
        Assert.That(sentQuery, Does.Contain("MINUS { ?clubBStatement wikibase:rank wikibase:DeprecatedRank. }"));
        Assert.That(Regex.Matches(sentQuery, Regex.Escape("ps:P54")).Count, Is.EqualTo(2));
        Assert.That(sentQuery, Does.Not.Contain("wdt:P54"),
            "truthy wdt:P54 is best-rank-only — reintroducing it silently reduces 'ever played for' to 'currently plays for' whenever a current club is preferred rank");
        Assert.That(sentQuery, Does.Not.Contain("P27"));
    }

    [Test]
    public async Task QueryClubClubIntersectionAsync_SentQuery_FiltersToMaleOnly()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P21 wd:Q6581097"));
    }

    [Test]
    public async Task QueryClubClubIntersectionAsync_SentQuery_FiltersToDateOfBirthOnOrAfter1939()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P569"));
        Assert.That(sentQuery, Does.Contain("\"1939-01-01T00:00:00Z\"^^xsd:dateTime"));
    }

    [Test]
    public void QueryClubClubIntersectionAsync_RejectsNonQidClubAValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryClubClubIntersectionAsync("Arsenal", ClubBQid));
    }

    [Test]
    public void QueryClubClubIntersectionAsync_RejectsNonQidClubBValue()
    {
        // Separate branch from the clubA check above (two independent `if`
        // guards in WikidataClient) — not guaranteed by symmetry.
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryClubClubIntersectionAsync(ClubAQid, "Barcelona"));
    }

    // ---- QueryPlayerPoolBirthYearAsync (S-032/ADR-0007/REQ-207, ------------
    // revised 2026-07-18: birth-year slicing replaced LIMIT/OFFSET paging,
    // and this method's error contract is deliberately the OPPOSITE of the
    // intersection queries' — it throws on failure so the import job can
    // never mistake a swallowed timeout for end-of-data again).

    [Test]
    public async Task QueryPlayerPoolBirthYearAsync_SentQuery_ContainsBoundedOneYearWindow()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerPoolBirthYearAsync(1977);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("?dateOfBirth >= \"1977-01-01T00:00:00Z\"^^xsd:dateTime"));
        Assert.That(sentQuery, Does.Contain("?dateOfBirth < \"1978-01-01T00:00:00Z\"^^xsd:dateTime"),
            "the window's upper bound must be exclusive at the next year's Jan 1");
    }

    [Test]
    public async Task QueryPlayerPoolBirthYearAsync_SentQuery_NeverContainsOrderByLimitOffsetOrSubquery()
    {
        // The heart of the 2026-07-18 fix: the original paged query's
        // `ORDER BY ?player LIMIT/OFFSET` over an inner subquery forced WDQS
        // to sort the ENTIRE unfiltered pool on every page, blowing its hard
        // ~60s server-side timeout on every single request — so every run
        // imported zero rows. A birth-year slice needs none of them.
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerPoolBirthYearAsync(1977);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Not.Contain("ORDER BY"));
        Assert.That(sentQuery, Does.Not.Contain("LIMIT"));
        Assert.That(sentQuery, Does.Not.Contain("OFFSET"));
        Assert.That(sentQuery, Does.Not.Contain("SELECT DISTINCT"), "no inner subquery — one flat bounded pattern");
    }

    [Test]
    public async Task QueryPlayerPoolBirthYearAsync_SentQuery_FiltersToMaleOnly()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerPoolBirthYearAsync(1977);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P21 wd:Q6581097"));
    }

    [Test]
    public async Task QueryPlayerPoolBirthYearAsync_SentQuery_QueriesBroadOccupationWithNoClubCountryOrImageFetch()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerPoolBirthYearAsync(1977);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P106 wd:Q937857"));
        // PlayerNameIndex is not the place for club (P54) data — that's
        // PlayerAttribute's job (ADR-0007) — and this query has no club/
        // country filter at all, unlike the two intersection queries above.
        Assert.That(sentQuery, Does.Not.Contain("P54"));
        Assert.That(sentQuery, Does.Not.Contain("P27 wd:"));
        // P18 (photo) was dropped 2026-07-18: the autocomplete contract
        // never exposes a photo, so fetching it was pure join/row cost.
        Assert.That(sentQuery, Does.Not.Contain("P18"));
    }

    [Test]
    public async Task QueryPlayerPoolBirthYearAsync_ParsesBirthYearAndNationality()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  {
                    "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" },
                    "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                    "birthYear": { "type": "literal", "datatype": "http://www.w3.org/2001/XMLSchema#integer", "value": "1977" },
                    "countryLabel": { "type": "literal", "value": "France" }
                  }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerPoolBirthYearAsync(1977);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].WikidataQid, Is.EqualTo("Q1519"));
        Assert.That(result[0].FullName, Is.EqualTo("Thierry Henry"));
        Assert.That(result[0].BirthYear, Is.EqualTo(1977));
        Assert.That(result[0].Nationality, Is.EqualTo("France"));
    }

    [Test]
    public async Task QueryPlayerPoolBirthYearAsync_MultipleRowsForSamePlayer_GroupsIntoOneEntry_KeepingFirstNonNullNationality()
    {
        // A player with more than one P27 citizenship produces more than one
        // binding row for the same ?player — these must collapse into one
        // WikidataNameIndexEntry, not one row per citizenship.
        const string json = """
            {
              "results": {
                "bindings": [
                  {
                    "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" },
                    "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                    "birthYear": { "type": "literal", "value": "1977" },
                    "countryLabel": { "type": "literal", "value": "France" }
                  },
                  {
                    "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" },
                    "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                    "birthYear": { "type": "literal", "value": "1977" },
                    "countryLabel": { "type": "literal", "value": "Guadeloupe" }
                  }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerPoolBirthYearAsync(1977);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Nationality, Is.EqualTo("France"), "the first non-null nationality seen wins, rather than producing a duplicate row per citizenship");
    }

    [Test]
    public async Task QueryPlayerPoolBirthYearAsync_NoBindings_ReturnsEmptyWithoutThrowing()
    {
        // An empty year is a genuinely valid result (sparse early years) —
        // NOT a failure. Only actual timeout/HTTP/parse failures throw.
        const string json = """{ "results": { "bindings": [] } }""";
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerPoolBirthYearAsync(1939);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QueryPlayerPoolBirthYearAsync_HttpErrorStatus_ThrowsWikidataQueryException()
    {
        // Opposite of the intersection queries' swallow-to-[] contract — a
        // swallowed failure here is what made the import job exit 0 having
        // imported nothing (NOTES.md 2026-07-18).
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningStatus(System.Net.HttpStatusCode.InternalServerError)));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerPoolBirthYearAsync(1977));
    }

    [Test]
    public void QueryPlayerPoolBirthYearAsync_Timeout_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerPoolBirthYearAsync(1977));
    }

    [Test]
    public void QueryPlayerPoolBirthYearAsync_MalformedJson_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("not valid json")));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerPoolBirthYearAsync(1977));
    }

    [Test]
    public void QueryPlayerPoolBirthYearAsync_RejectsBirthYearBeforePoolFloor()
    {
        // ADR-0025's 1939 pool floor, enforced at the client so a buggy
        // caller can't silently widen the pool.
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.QueryPlayerPoolBirthYearAsync(1938));
    }
}

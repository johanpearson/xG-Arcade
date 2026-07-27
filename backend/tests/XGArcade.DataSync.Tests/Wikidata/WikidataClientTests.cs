using System.Text.RegularExpressions;
using XGArcade.DataSync.Wikidata;
using XGArcade.TestSupport;

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
    private const string TrophyQid = "Q166177"; // Ballon d'Or (unverified this session — see ReferenceDataSeeder)

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

    // REQ-211 (2026-07-27 fix): throwOnTimeout defaults to false, so every
    // call site above that doesn't pass it explicitly keeps the original
    // swallow-to-[] contract completely unaffected.
    [Test]
    public async Task QueryCountryClubIntersectionAsync_ThrowOnTimeoutFalse_Timeout_ReturnsEmptyWithoutThrowing()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid, throwOnTimeout: false);

        Assert.That(result, Is.Empty);
    }

    // REQ-211 (2026-07-27 fix): the guess-time fallback's own opt-in — a
    // timeout throws WikidataQueryException instead of swallowing to [],
    // so a genuine timeout is distinguishable from "Wikidata answered and
    // found nothing."
    [Test]
    public void QueryCountryClubIntersectionAsync_ThrowOnTimeoutTrue_Timeout_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));

        Assert.ThrowsAsync<WikidataQueryException>(async () =>
            await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid, throwOnTimeout: true));
    }

    // A non-timeout failure (HTTP error/malformed JSON) must still swallow
    // to [] even with throwOnTimeout: true — that parameter only ever
    // changes the TIMEOUT branch's behavior, never the HTTP/parse-error
    // branch's (see RunIntersectionQueryAsync's own comment on why).
    [Test]
    public async Task QueryCountryClubIntersectionAsync_ThrowOnTimeoutTrue_HttpErrorStatus_StillReturnsEmptyWithoutThrowing()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningStatus(System.Net.HttpStatusCode.InternalServerError)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid, throwOnTimeout: true);

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

    // ---- REQ-214: P18 (photo) carried through the same intersection query -

    [Test]
    public async Task REQ214_QueryCountryClubIntersectionAsync_ParsesPhotoUrl_WhenP18Present()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "photo": { "type": "uri", "value": "http://commons.wikimedia.org/wiki/Special:FilePath/Thierry%20Henry.jpg" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].PhotoUrl, Is.EqualTo("http://commons.wikimedia.org/wiki/Special:FilePath/Thierry%20Henry.jpg"));
    }

    [Test]
    public async Task REQ214_QueryCountryClubIntersectionAsync_PhotoUrlIsNull_WhenP18Absent()
    {
        // No "photo" binding at all — a player with no Wikidata image
        // (REQ-214's explicit "no photo is a normal case, never an error").
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
        Assert.That(result[0].PhotoUrl, Is.Null);
    }

    [Test]
    public async Task REQ214_QueryCountryClubIntersectionAsync_SentQuery_FetchesP18ImageAsOptional()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("OPTIONAL { ?player wdt:P18 ?photo. }"),
            "P18 must be OPTIONAL, same as alias — a player with no photo must still match the rest of the query");
    }

    // ---- ADR-0042/S-079: P580/P582/P1350 qualifiers on ?clubStatement, ----
    // carried through the same intersection query — the "PlayerCareerStint"
    // data model story.

    [Test]
    public async Task QueryCountryClubIntersectionAsync_SentQuery_FetchesCareerStintQualifiersAsOptional()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("OPTIONAL { ?clubStatement pq:P580 ?startTime. }"));
        Assert.That(sentQuery, Does.Contain("OPTIONAL { ?clubStatement pq:P582 ?endTime. }"));
        Assert.That(sentQuery, Does.Contain("OPTIONAL { ?clubStatement pq:P1350 ?numberOfMatches. }"),
            "all three qualifiers must be OPTIONAL — a stint with no P1350 (or even no P580/P582) must still match the rest of the query");
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_ParsesCareerStint_WhenStartAndEndTimeAndAppearanceCountPresent()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  {
                    "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" },
                    "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                    "startTime": { "type": "literal", "value": "1999-08-03T00:00:00Z" },
                    "endTime": { "type": "literal", "value": "2007-06-30T00:00:00Z" },
                    "numberOfMatches": { "type": "literal", "value": "254" }
                  }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CareerStints, Has.Count.EqualTo(1));
        Assert.That(result[0].CareerStints[0].StartYear, Is.EqualTo(1999));
        Assert.That(result[0].CareerStints[0].EndYear, Is.EqualTo(2007));
        Assert.That(result[0].CareerStints[0].AppearanceCount, Is.EqualTo(254));
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_CareerStintAppearanceCountIsNull_WhenP1350Absent()
    {
        // Wikidata's P1350 coverage is inconsistent — a stint with a known
        // date range but no recorded appearance count must get null, never
        // a placeholder 0 (ADR-0042).
        const string json = """
            {
              "results": {
                "bindings": [
                  {
                    "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" },
                    "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                    "startTime": { "type": "literal", "value": "1999-08-03T00:00:00Z" },
                    "endTime": { "type": "literal", "value": "2007-06-30T00:00:00Z" }
                  }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CareerStints, Has.Count.EqualTo(1));
        Assert.That(result[0].CareerStints[0].AppearanceCount, Is.Null);
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_CareerStintEndYearIsNull_WhenOngoing()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  {
                    "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" },
                    "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                    "startTime": { "type": "literal", "value": "2021-01-01T00:00:00Z" }
                  }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CareerStints, Has.Count.EqualTo(1));
        Assert.That(result[0].CareerStints[0].StartYear, Is.EqualTo(2021));
        Assert.That(result[0].CareerStints[0].EndYear, Is.Null);
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_NoCareerStint_WhenStartTimeAbsent()
    {
        // A row with none of the three qualifiers bound carries zero
        // information — must not fabricate a stint (StartYear is
        // non-nullable on the entity, so there is nothing valid to write).
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
        Assert.That(result[0].CareerStints, Is.Empty);
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_DedupesDuplicateCareerStintTuplesAcrossMultipleRows()
    {
        // SPARQL's OPTIONAL semantics mean a player with N aliases and M
        // distinct qualifier combinations can produce up to N×M rows —
        // identical (start, end, count) tuples across different rows must
        // collapse into one stint, the same way alias rows already dedupe.
        const string json = """
            {
              "results": {
                "bindings": [
                  {
                    "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                    "alias": { "type": "literal", "value": "Titi" },
                    "startTime": { "type": "literal", "value": "1999-08-03T00:00:00Z" }, "endTime": { "type": "literal", "value": "2007-06-30T00:00:00Z" }, "numberOfMatches": { "type": "literal", "value": "254" }
                  },
                  {
                    "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                    "alias": { "type": "literal", "value": "TH14" },
                    "startTime": { "type": "literal", "value": "1999-08-03T00:00:00Z" }, "endTime": { "type": "literal", "value": "2007-06-30T00:00:00Z" }, "numberOfMatches": { "type": "literal", "value": "254" }
                  }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Aliases, Has.Count.EqualTo(2), "both aliases must still be captured");
        Assert.That(result[0].CareerStints, Has.Count.EqualTo(1), "the identical stint tuple carried by both rows must collapse into one");
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_TwoDistinctCareerStints_BothParsed()
    {
        // Two genuinely different stints (e.g. a loan, then a permanent
        // return) — must NOT collapse, since their tuples differ.
        const string json = """
            {
              "results": {
                "bindings": [
                  {
                    "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                    "startTime": { "type": "literal", "value": "1999-08-03T00:00:00Z" }, "endTime": { "type": "literal", "value": "2007-06-30T00:00:00Z" }, "numberOfMatches": { "type": "literal", "value": "254" }
                  },
                  {
                    "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                    "startTime": { "type": "literal", "value": "2012-01-01T00:00:00Z" }, "endTime": { "type": "literal", "value": "2013-01-01T00:00:00Z" }
                  }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CareerStints, Has.Count.EqualTo(2));
        Assert.That(result[0].CareerStints, Has.Some.Matches<CareerStintQualifiers>(s => s.StartYear == 1999 && s.EndYear == 2007 && s.AppearanceCount == 254));
        Assert.That(result[0].CareerStints, Has.Some.Matches<CareerStintQualifiers>(s => s.StartYear == 2012 && s.EndYear == 2013 && s.AppearanceCount == null));
    }

    [Test]
    public async Task QueryClubClubIntersectionAsync_SentQuery_FetchesCareerStintQualifiersAsOptional()
    {
        // Present in the shared query text (BuildIntersectionQuery's
        // footer), but structurally never binds for this query shape — see
        // WikidataPlayerMatch.CareerStints' own doc comment.
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("pq:P580"));
        Assert.That(sentQuery, Does.Contain("pq:P582"));
        Assert.That(sentQuery, Does.Contain("pq:P1350"));
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

    // ---- QueryNationalTeamClubIntersectionAsync (REQ-114/ADR-0035) --------
    // England/Scotland/Wales/Northern Ireland's P1532 counterpart of
    // QueryCountryClubIntersectionAsync above — reuses the same
    // RunIntersectionQueryAsync/ParseBindings plumbing, so this only tests
    // what's actually different: the SPARQL predicate.

    private const string NationalTeamQid = "Q21"; // England (unverified this session — see ReferenceDataSeeder)

    [Test]
    public async Task REQ114_QueryNationalTeamClubIntersectionAsync_SentQuery_UsesTruthyP1532AndNeverP27()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryNationalTeamClubIntersectionAsync(NationalTeamQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain($"?player wdt:P1532 wd:{NationalTeamQid}."));
        Assert.That(sentQuery, Does.Not.Contain("P27"),
            "the national-team path must query P1532 ('country for sport') exclusively — P27 ('country of citizenship') " +
            "can't distinguish England/Scotland/Wales/Northern Ireland since their players' P27 is uniformly United Kingdom");
    }

    [Test]
    public async Task REQ114_QueryNationalTeamClubIntersectionAsync_SentQuery_MatchesClubViaFullStatementPathExcludingOnlyDeprecatedRank()
    {
        // Same non-negotiable "ever played for" reasoning as
        // REQ113_QueryCountryClubIntersectionAsync_SentQuery_
        // MatchesClubViaFullStatementPathExcludingOnlyDeprecatedRank — the
        // national-team query path only swaps the country predicate, never
        // the club half.
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryNationalTeamClubIntersectionAsync(NationalTeamQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("?player p:P54 ?clubStatement."));
        Assert.That(sentQuery, Does.Contain($"?clubStatement ps:P54 wd:{ClubQid}."));
        Assert.That(sentQuery, Does.Contain("MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }"));
        Assert.That(sentQuery, Does.Not.Contain("wdt:P54"));
    }

    [Test]
    public async Task REQ114_QueryNationalTeamClubIntersectionAsync_SentQuery_NeverContainsLimit()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryNationalTeamClubIntersectionAsync(NationalTeamQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Not.Contain("LIMIT"));
    }

    [Test]
    public async Task REQ114_QueryNationalTeamClubIntersectionAsync_NoMatchingRows_ReturnsEmptyWithoutThrowing()
    {
        const string json = """{ "results": { "bindings": [] } }""";
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryNationalTeamClubIntersectionAsync(NationalTeamQid, ClubQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task REQ114_QueryNationalTeamClubIntersectionAsync_MatchingRows_ParsesSameShapeAsCountryClub()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Harry Kane" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryNationalTeamClubIntersectionAsync(NationalTeamQid, ClubQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].WikidataQid, Is.EqualTo("Q1519"));
        Assert.That(result[0].FullName, Is.EqualTo("Harry Kane"));
    }

    [Test]
    public void REQ114_QueryNationalTeamClubIntersectionAsync_RejectsNonQidNationalTeamValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryNationalTeamClubIntersectionAsync("England", ClubQid));
    }

    [Test]
    public void REQ114_QueryNationalTeamClubIntersectionAsync_RejectsNonQidClubValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryNationalTeamClubIntersectionAsync(NationalTeamQid, "Arsenal"));
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

    // ---- REQ-214: P18 (photo) carried through the same intersection query -
    // Mirrors the QueryCountryClubIntersectionAsync REQ-214 tests above —
    // same ParseBindings code path, different query builder.

    [Test]
    public async Task REQ214_QueryClubClubIntersectionAsync_ParsesPhotoUrl_WhenP18Present()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "photo": { "type": "uri", "value": "http://commons.wikimedia.org/wiki/Special:FilePath/Thierry%20Henry.jpg" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].PhotoUrl, Is.EqualTo("http://commons.wikimedia.org/wiki/Special:FilePath/Thierry%20Henry.jpg"));
    }

    [Test]
    public async Task REQ214_QueryClubClubIntersectionAsync_PhotoUrlIsNull_WhenP18Absent()
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
        Assert.That(result[0].PhotoUrl, Is.Null);
    }

    [Test]
    public async Task REQ214_QueryClubClubIntersectionAsync_SentQuery_FetchesP18ImageAsOptional()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("OPTIONAL { ?player wdt:P18 ?photo. }"),
            "P18 must be OPTIONAL, same as alias — a player with no photo must still match the rest of the query");
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

    // ---- QueryTrophyCountryIntersectionAsync / QueryTrophyClubIntersectionAsync (S-031/REQ-108) ----
    // Same RunIntersectionQueryAsync/ParseBindings code path as every query
    // above, just different query builders (BuildTrophyCountryIntersectionQuery/
    // BuildTrophyClubIntersectionQuery) — so only the query-shape assertions
    // and the QID-validation guards get their own coverage here; the
    // parsing/error-handling behavior (alias grouping, timeout, malformed
    // JSON, etc.) is already proven generically by the tests above.

    [Test]
    public async Task QueryTrophyCountryIntersectionAsync_GroupsMultipleAliasRowsUnderOnePlayer()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "Titi" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "TH14" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryTrophyCountryIntersectionAsync(TrophyQid, CountryQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].WikidataQid, Is.EqualTo("Q1519"));
        Assert.That(result[0].Aliases, Is.EquivalentTo(new[] { "Titi", "TH14" }));
    }

    [Test]
    public async Task QueryTrophyCountryIntersectionAsync_NoMatchingRows_ReturnsEmptyWithoutThrowing()
    {
        const string json = """{ "results": { "bindings": [] } }""";
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryTrophyCountryIntersectionAsync(TrophyQid, CountryQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryTrophyCountryIntersectionAsync_HttpErrorStatus_ReturnsEmptyWithoutThrowing()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningStatus(System.Net.HttpStatusCode.InternalServerError)));

        var result = await client.QueryTrophyCountryIntersectionAsync(TrophyQid, CountryQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryTrophyCountryIntersectionAsync_Timeout_ReturnsEmptyWithoutThrowing()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));

        var result = await client.QueryTrophyCountryIntersectionAsync(TrophyQid, CountryQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryTrophyCountryIntersectionAsync_SentQuery_NeverContainsLimit()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryTrophyCountryIntersectionAsync(TrophyQid, CountryQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Not.Contain("LIMIT"));
    }

    [Test]
    public async Task REQ108_QueryTrophyCountryIntersectionAsync_SentQuery_UsesTruthyP166AndP27()
    {
        // P166 ("award received") is truthy here — a deliberate, documented
        // judgment call (see BuildTrophyCountryIntersectionQuery's own
        // comment): unlike P54, no Wikidata editorial convention marks one
        // award win as "superseding" another, so best-rank and "received
        // this award at all" coincide.
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryTrophyCountryIntersectionAsync(TrophyQid, CountryQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain($"?player wdt:P166 wd:{TrophyQid}."));
        Assert.That(sentQuery, Does.Contain($"?player wdt:P27 wd:{CountryQid}."));
        Assert.That(sentQuery, Does.Not.Contain("P54"));
    }

    [Test]
    public void QueryTrophyCountryIntersectionAsync_RejectsNonQidTrophyValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryTrophyCountryIntersectionAsync("Ballon d'Or", CountryQid));
    }

    [Test]
    public void QueryTrophyCountryIntersectionAsync_RejectsNonQidCountryValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryTrophyCountryIntersectionAsync(TrophyQid, "France"));
    }

    [Test]
    public async Task QueryTrophyClubIntersectionAsync_GroupsMultipleAliasRowsUnderOnePlayer()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "Titi" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "TH14" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryTrophyClubIntersectionAsync(TrophyQid, ClubQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].WikidataQid, Is.EqualTo("Q1519"));
        Assert.That(result[0].Aliases, Is.EquivalentTo(new[] { "Titi", "TH14" }));
    }

    [Test]
    public async Task QueryTrophyClubIntersectionAsync_NoMatchingRows_ReturnsEmptyWithoutThrowing()
    {
        const string json = """{ "results": { "bindings": [] } }""";
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryTrophyClubIntersectionAsync(TrophyQid, ClubQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryTrophyClubIntersectionAsync_HttpErrorStatus_ReturnsEmptyWithoutThrowing()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningStatus(System.Net.HttpStatusCode.InternalServerError)));

        var result = await client.QueryTrophyClubIntersectionAsync(TrophyQid, ClubQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryTrophyClubIntersectionAsync_Timeout_ReturnsEmptyWithoutThrowing()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));

        var result = await client.QueryTrophyClubIntersectionAsync(TrophyQid, ClubQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task REQ108_QueryTrophyClubIntersectionAsync_SentQuery_UsesTruthyP166AndFullStatementPathP54()
    {
        // P166 stays truthy (same reasoning as the Trophy x Country query
        // above); P54 must NOT go truthy — same non-negotiable "ever played
        // for," not "currently plays for," reasoning as every other P54 use
        // in this client.
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryTrophyClubIntersectionAsync(TrophyQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain($"?player wdt:P166 wd:{TrophyQid}."));
        Assert.That(sentQuery, Does.Contain("?player p:P54 ?clubStatement."));
        Assert.That(sentQuery, Does.Contain($"?clubStatement ps:P54 wd:{ClubQid}."));
        Assert.That(sentQuery, Does.Contain("MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }"));
        Assert.That(sentQuery, Does.Not.Contain("wdt:P54"),
            "truthy wdt:P54 is best-rank-only — reintroducing it silently reduces 'ever played for' to 'currently plays for'");
        Assert.That(sentQuery, Does.Not.Contain("P27"));
    }

    [Test]
    public void QueryTrophyClubIntersectionAsync_RejectsNonQidTrophyValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryTrophyClubIntersectionAsync("Ballon d'Or", ClubQid));
    }

    [Test]
    public void QueryTrophyClubIntersectionAsync_RejectsNonQidClubValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryTrophyClubIntersectionAsync(TrophyQid, "Arsenal"));
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
    public void QueryPlayerPoolBirthYearAsync_CallerCancellation_PropagatesAsOperationCanceledException_NotWikidataQueryException()
    {
        // The load-bearing counterpart of the Timeout test above: the catch
        // filter in QueryPlayerPoolBirthYearAsync classifies only its OWN
        // query timeout as a WikidataQueryException — caller cancellation
        // (Ctrl+C, host shutdown) must propagate as an OCE so
        // PlayerNameIndexImporter never retries it or records it as a failed
        // year. "Simplifying" the filter back to the intersection queries'
        // shape breaks exactly this test.
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromSeconds(30));
        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var ex = Assert.CatchAsync(() => client.QueryPlayerPoolBirthYearAsync(1977, callerCts.Token));

        Assert.That(ex, Is.InstanceOf<OperationCanceledException>());
        Assert.That(ex, Is.Not.InstanceOf<WikidataQueryException>(),
            "caller cancellation misclassified as a query failure would make the importer retry a cancelled run");
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

    // ---- QueryPlayerPhotosByQidsAsync (REQ-214 backfill, S-045) ------------
    // Batched, direct-by-QID lookup — a VALUES clause, not an intersection
    // query — with the SAME throw-on-failure contract as
    // QueryPlayerPoolBirthYearAsync above, for the same reason (this is a
    // batch job whose success metric is a backfilled-row count).

    [Test]
    public async Task REQ214_QueryPlayerPhotosByQidsAsync_SentQuery_ContainsValuesClauseOverEveryQid()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerPhotosByQidsAsync(["Q1519", "Q9617", "Q7156"]);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("VALUES ?player { wd:Q1519 wd:Q9617 wd:Q7156 }"));
        Assert.That(sentQuery, Does.Contain("OPTIONAL { ?player wdt:P18 ?photo. }"));
    }

    [Test]
    public async Task REQ214_QueryPlayerPhotosByQidsAsync_SentQuery_NeverContainsOrderByLimitOrOffset()
    {
        // Same bounded-query discipline as every other query in this
        // client — implementation-document.md §6a.
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerPhotosByQidsAsync(["Q1519"]);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Not.Contain("ORDER BY"));
        Assert.That(sentQuery, Does.Not.Contain("LIMIT"));
        Assert.That(sentQuery, Does.Not.Contain("OFFSET"));
    }

    [Test]
    public async Task REQ214_QueryPlayerPhotosByQidsAsync_ReturnsDictionaryKeyedByQid_ForQidsWithAPhoto()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "photo": { "type": "uri", "value": "http://commons.wikimedia.org/wiki/Special:FilePath/Thierry%20Henry.jpg" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q9617" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerPhotosByQidsAsync(["Q1519", "Q9617"]);

        Assert.That(result, Has.Count.EqualTo(1), "a QID with no photo binding must be absent, not present with a null/empty value");
        Assert.That(result["Q1519"], Is.EqualTo("http://commons.wikimedia.org/wiki/Special:FilePath/Thierry%20Henry.jpg"));
        Assert.That(result.ContainsKey("Q9617"), Is.False);
    }

    [Test]
    public async Task REQ214_QueryPlayerPhotosByQidsAsync_EmptyQidList_ReturnsEmptyDictionaryWithoutSendingARequest()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        var result = await client.QueryPlayerPhotosByQidsAsync([]);

        Assert.That(result, Is.Empty);
        Assert.That(handler.LastRequest, Is.Null);
    }

    [Test]
    public void REQ214_QueryPlayerPhotosByQidsAsync_HttpErrorStatus_ThrowsWikidataQueryException()
    {
        // Opposite of the intersection queries' swallow-to-[] contract —
        // same reasoning as QueryPlayerPoolBirthYearAsync's own test.
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningStatus(System.Net.HttpStatusCode.InternalServerError)));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerPhotosByQidsAsync(["Q1519"]));
    }

    [Test]
    public void REQ214_QueryPlayerPhotosByQidsAsync_Timeout_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerPhotosByQidsAsync(["Q1519"]));
    }

    [Test]
    public void REQ214_QueryPlayerPhotosByQidsAsync_MalformedJson_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("not valid json")));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerPhotosByQidsAsync(["Q1519"]));
    }

    [Test]
    public void REQ214_QueryPlayerPhotosByQidsAsync_RejectsNonQidValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryPlayerPhotosByQidsAsync(["Q1519", "Arsenal"]));
    }
}

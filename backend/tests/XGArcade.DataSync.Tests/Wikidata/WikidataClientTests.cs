using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
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
    private const string TeamTrophyQid = "Q19317"; // FIFA World Cup (unverified this session — see ReferenceDataSeeder, ADR-0061)

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

    // Observability fix (2026-07-27): a swallowed timeout used to log
    // nothing at all, unlike the HTTP/parse-error branch just below it in
    // WikidataClient — this made warm-player-cache's own aggregate summary
    // ("N queried live") unable to distinguish "queried live and found
    // nothing" from "queried live and silently timed out." This test proves
    // the log line is still emitted, not just that the timeout is swallowed
    // (already covered by the test above).
    //
    // Level fix (2026-08-01, ADR-0052): downgraded from Warning to Debug —
    // this same per-pair line, once per failing pair across a few hundred
    // pairs, was the dominant contributor to cache-warming's unreadable
    // logs. Debug is filtered out by this project's default "Information"
    // log level, so a normal run's console stays quiet; this test now pins
    // the level down explicitly so a future change can't silently promote
    // it back to Warning and reintroduce the noise.
    [Test]
    public async Task QueryCountryClubIntersectionAsync_Timeout_LogsAtDebugLevel()
    {
        var logger = new CapturingLogger<WikidataClient>();
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50),
            logger: logger);

        await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(logger.Entries, Has.Exactly(1).Matches<(LogLevel Level, string Message)>(
            e => e.Level == LogLevel.Debug && e.Message.Contains("country-club") && e.Message.Contains(CountryQid)
                && e.Message.Contains(ClubQid) && e.Message.Contains("timed out")));
    }

    // Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
    // "don't over-mock") — captures the formatted message text (and, since
    // 2026-08-01, the LogLevel) of each Log call, which is all this file's
    // tests need to assert against.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Add(message);
            Entries.Add((logLevel, message));
        }
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
        // ADR-0046 follow-up: throwOnTimeout: true uses
        // guessTimeFallbackQueryTimeout, not queryTimeout — must be set here
        // too, or this test would wait out the real (28s default) budget.
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50),
            guessTimeFallbackQueryTimeout: TimeSpan.FromMilliseconds(50));

        Assert.ThrowsAsync<WikidataQueryException>(async () =>
            await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid, throwOnTimeout: true));
    }

    // ADR-0046 follow-up (2026-07-27): a real report (guessing "Clarence
    // Seedorf" for Ajax x AC Milan) showed a throwOnTimeout: true call
    // reusing queryTimeout would fail every retry for a query shape that
    // legitimately needs longer than 15s (ADR-0011's documented 9-27s WDQS
    // range) — this proves the two budgets are genuinely independent, not
    // just that a timeout eventually throws: a short queryTimeout must NOT
    // cut a throwOnTimeout: true call off early once guessTimeFallbackQueryTimeout
    // is set wider than it.
    [Test]
    public async Task QueryCountryClubIntersectionAsync_ThrowOnTimeoutTrue_UsesGuessTimeFallbackBudget_NotQueryTimeout()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50),
            guessTimeFallbackQueryTimeout: TimeSpan.FromMilliseconds(400));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.ThrowsAsync<WikidataQueryException>(async () =>
            await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid, throwOnTimeout: true));
        stopwatch.Stop();

        Assert.That(stopwatch.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(300),
            "a short queryTimeout (50ms) must not cut this call off before the wider guessTimeFallbackQueryTimeout (400ms) elapses");
        Assert.That(ex!.Message, Does.Contain("0s."), "the reported duration should reflect the actual (guessTimeFallback) budget used, not queryTimeout");
    }

    // ---- REQ-110 (2026-07-28 "cache-warming-specific timeout + same-run ---
    // retry" extension): WikidataQueryTimeoutTier.CacheWarming resolves to a
    // THIRD, distinct budget — never queryTimeout (round generation's 15s)
    // or guessTimeFallbackQueryTimeout (REQ-211's 28s) — used only alongside
    // throwOnTimeout: false (cache warming never throws).

    [Test]
    public async Task QueryCountryClubIntersectionAsync_TimeoutTierCacheWarming_UsesCacheWarmingBudget_NotQueryTimeout()
    {
        // A short queryTimeout (50ms) alongside a much wider
        // cacheWarmingQueryTimeout (400ms) — would fail fast under
        // queryTimeout, so this test would fail if the two timeouts were
        // ever collapsed back into one (this REQ's own explicit "Test
        // level" requirement).
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50),
            cacheWarmingQueryTimeout: TimeSpan.FromMilliseconds(400));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await client.QueryCountryClubIntersectionAsync(
            CountryQid, ClubQid, throwOnTimeout: false, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
        stopwatch.Stop();

        Assert.That(stopwatch.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(300),
            "a short queryTimeout (50ms) must not cut a CacheWarming-tier call off before the wider cacheWarmingQueryTimeout (400ms) elapses");
        Assert.That(result, Is.Empty, "cache warming never throws on timeout (throwOnTimeout: false) — it swallows to an empty match list");
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_TimeoutTierDefault_IgnoresCacheWarmingBudget_UsesQueryTimeout()
    {
        // The reverse of the test above: a Default-tier call (the implicit
        // choice for every existing caller — round generation, guess-time
        // fallback) must keep using queryTimeout even when a much wider
        // cacheWarmingQueryTimeout happens to be configured — proves the
        // selection is genuinely driven by timeoutTier, not by "whichever
        // budget is currently the widest."
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50),
            cacheWarmingQueryTimeout: TimeSpan.FromMilliseconds(2000));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await client.QueryCountryClubIntersectionAsync(
            CountryQid, ClubQid, throwOnTimeout: false, timeoutTier: WikidataQueryTimeoutTier.Default);
        stopwatch.Stop();

        Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(1000),
            "a Default-tier call must time out at queryTimeout (50ms), not wait out the much wider cacheWarmingQueryTimeout (2000ms)");
        Assert.That(result, Is.Empty);
    }

    // ---- REQ-110 (2026-07-28 "technical-failure visibility" extension): ---
    // onTechnicalFailure fires on a timeout and on an HTTP/JSON-parse error,
    // and never on a genuine successful response (with or without matches).

    [Test]
    public async Task QueryCountryClubIntersectionAsync_Timeout_InvokesOnTechnicalFailure()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));
        var invoked = false;

        await client.QueryCountryClubIntersectionAsync(
            CountryQid, ClubQid, throwOnTimeout: false, onTechnicalFailure: () => invoked = true);

        Assert.That(invoked, Is.True, "a swallowed timeout is a technical failure, distinct from a successful zero-match response");
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_HttpErrorStatus_InvokesOnTechnicalFailure()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningStatus(System.Net.HttpStatusCode.InternalServerError)));
        var invoked = false;

        await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid, onTechnicalFailure: () => invoked = true);

        Assert.That(invoked, Is.True);
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_MalformedJson_InvokesOnTechnicalFailure()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("not valid json")));
        var invoked = false;

        await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid, onTechnicalFailure: () => invoked = true);

        Assert.That(invoked, Is.True);
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_SuccessfulZeroMatchResponse_DoesNotInvokeOnTechnicalFailure()
    {
        const string json = """{ "results": { "bindings": [] } }""";
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));
        var invoked = false;

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid, onTechnicalFailure: () => invoked = true);

        Assert.That(result, Is.Empty, "a genuine zero-match answer, not a failure");
        Assert.That(invoked, Is.False,
            "a query that answered successfully and simply found nothing must never be reported as a technical failure");
    }

    [Test]
    public async Task QueryCountryClubIntersectionAsync_SuccessfulResponseWithMatches_DoesNotInvokeOnTechnicalFailure()
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
        var invoked = false;

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid, onTechnicalFailure: () => invoked = true);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(invoked, Is.False);
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

    // 2026-08-01 fix (ADR-0052): see BuildClubClubIntersectionQuery's own
    // comment for the full incident — a plain join on two independent P54
    // statement-path patterns multiplied rows by (statements at club A) x
    // (statements at club B) x aliases PER PLAYER, producing a real
    // 250,000+ row WDQS response for two clubs with a large,
    // historically-overlapping squad (NOTES.md's 2026-08-01 entry). FILTER
    // EXISTS turns each club's match into an existence check instead of a
    // join, so ?clubAStatement/?clubBStatement never bind in the outer
    // pattern and neither club's statement count can multiply rows.
    [Test]
    public async Task REQ110_QueryClubClubIntersectionAsync_SentQuery_WrapsEachClubMatchInFilterExistsToAvoidStatementCrossProduct()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(Regex.Matches(sentQuery, Regex.Escape("FILTER EXISTS {")).Count, Is.EqualTo(2),
            "each club's P54 statement-path match must be wrapped in its own FILTER EXISTS block, not a plain join");
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

    // ---- QueryTeamTrophyCountryIntersectionAsync / QueryTeamTrophyNationalTeamIntersectionAsync /
    // QueryTeamTrophyClubIntersectionAsync / QueryTrophyNationalTeamIntersectionAsync (ADR-0061) ----
    // Same RunIntersectionQueryAsync/ParseBindings code path as every query
    // above (alias grouping, timeout, malformed JSON, etc. already proven
    // generically) — only the query-shape assertions and QID-validation
    // guards get dedicated coverage here, same precedent
    // QueryTrophyCountryIntersectionAsync/QueryTrophyClubIntersectionAsync
    // already established.

    [Test]
    public async Task REQ108_QueryTeamTrophyCountryIntersectionAsync_SentQuery_UsesP27PlayerSideAndP1532WinnerSide()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryTeamTrophyCountryIntersectionAsync(TeamTrophyQid, CountryQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain($"?player wdt:P27 wd:{CountryQid}."));
        Assert.That(sentQuery, Does.Contain("?player wdt:P1344 ?edition."));
        Assert.That(sentQuery, Does.Contain($"?edition wdt:P3450 wd:{TeamTrophyQid}."));
        Assert.That(sentQuery, Does.Contain("?edition wdt:P1346 ?winner."));
        Assert.That(sentQuery, Does.Contain($"?winner wdt:P1532 wd:{CountryQid}."),
            "the winner-side join must always be P1532 — a P1346 winner value for a national-team competition is a national-team item, never the country item directly");
        Assert.That(sentQuery, Does.Not.Contain("P166"), "team competitions have no P166 equivalent");
        Assert.That(sentQuery, Does.Not.Contain("P54"), "the Country variant has no club-membership clause");
    }

    [Test]
    public async Task QueryTeamTrophyCountryIntersectionAsync_NoMatchingRows_ReturnsEmptyWithoutThrowing()
    {
        const string json = """{ "results": { "bindings": [] } }""";
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryTeamTrophyCountryIntersectionAsync(TeamTrophyQid, CountryQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task QueryTeamTrophyCountryIntersectionAsync_SentQuery_NeverContainsLimit()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryTeamTrophyCountryIntersectionAsync(TeamTrophyQid, CountryQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Not.Contain("LIMIT"));
    }

    [Test]
    public void QueryTeamTrophyCountryIntersectionAsync_RejectsNonQidTrophyValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryTeamTrophyCountryIntersectionAsync("FIFA World Cup", CountryQid));
    }

    [Test]
    public void QueryTeamTrophyCountryIntersectionAsync_RejectsNonQidCountryValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryTeamTrophyCountryIntersectionAsync(TeamTrophyQid, "France"));
    }

    [Test]
    public async Task REQ108_QueryTeamTrophyNationalTeamIntersectionAsync_SentQuery_UsesP1532OnBothPlayerAndWinnerSide()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryTeamTrophyNationalTeamIntersectionAsync(TeamTrophyQid, NationalTeamQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain($"?player wdt:P1532 wd:{NationalTeamQid}."));
        Assert.That(sentQuery, Does.Contain("?player wdt:P1344 ?edition."));
        Assert.That(sentQuery, Does.Contain($"?edition wdt:P3450 wd:{TeamTrophyQid}."));
        Assert.That(sentQuery, Does.Contain("?edition wdt:P1346 ?winner."));
        Assert.That(sentQuery, Does.Contain($"?winner wdt:P1532 wd:{NationalTeamQid}."));
        Assert.That(sentQuery, Does.Not.Contain("P27"),
            "a flagged country must query P1532 exclusively on the player side, same as QueryNationalTeamClubIntersectionAsync");
    }

    [Test]
    public async Task QueryTeamTrophyNationalTeamIntersectionAsync_NoMatchingRows_ReturnsEmptyWithoutThrowing()
    {
        const string json = """{ "results": { "bindings": [] } }""";
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryTeamTrophyNationalTeamIntersectionAsync(TeamTrophyQid, NationalTeamQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QueryTeamTrophyNationalTeamIntersectionAsync_RejectsNonQidTrophyValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryTeamTrophyNationalTeamIntersectionAsync("FIFA World Cup", NationalTeamQid));
    }

    [Test]
    public void QueryTeamTrophyNationalTeamIntersectionAsync_RejectsNonQidCountryValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryTeamTrophyNationalTeamIntersectionAsync(TeamTrophyQid, "England"));
    }

    [Test]
    public async Task REQ108_QueryTeamTrophyClubIntersectionAsync_SentQuery_KeepsP54ClubMembershipAlongsideEditionWinnerJoin()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryTeamTrophyClubIntersectionAsync(TeamTrophyQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("?player p:P54 ?clubStatement."));
        Assert.That(sentQuery, Does.Contain($"?clubStatement ps:P54 wd:{ClubQid}."));
        Assert.That(sentQuery, Does.Contain("MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }"));
        Assert.That(sentQuery, Does.Contain("?player wdt:P1344 ?edition."));
        Assert.That(sentQuery, Does.Contain($"?edition wdt:P3450 wd:{TeamTrophyQid}."));
        Assert.That(sentQuery, Does.Contain($"?edition wdt:P1346 wd:{ClubQid}."),
            "the Club variant matches the edition winner directly against the club QID — no P1532 indirection needed");
        Assert.That(sentQuery, Does.Not.Contain("wdt:P54"),
            "truthy wdt:P54 is best-rank-only — reintroducing it silently reduces 'ever played for' to 'currently plays for'");
        Assert.That(sentQuery, Does.Not.Contain("P166"));
    }

    [Test]
    public async Task QueryTeamTrophyClubIntersectionAsync_NoMatchingRows_ReturnsEmptyWithoutThrowing()
    {
        const string json = """{ "results": { "bindings": [] } }""";
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryTeamTrophyClubIntersectionAsync(TeamTrophyQid, ClubQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QueryTeamTrophyClubIntersectionAsync_RejectsNonQidTrophyValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryTeamTrophyClubIntersectionAsync("FIFA World Cup", ClubQid));
    }

    [Test]
    public void QueryTeamTrophyClubIntersectionAsync_RejectsNonQidClubValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryTeamTrophyClubIntersectionAsync(TeamTrophyQid, "Arsenal"));
    }

    // Judgment call (see IWikidataClient.QueryTrophyNationalTeamIntersectionAsync's
    // own doc comment) — the individual-award P166 counterpart of
    // QueryTeamTrophyNationalTeamIntersectionAsync, needed to fully close
    // ADR-0035's follow-up note for the pre-existing S-031 P166 path.

    [Test]
    public async Task REQ114_QueryTrophyNationalTeamIntersectionAsync_SentQuery_UsesTruthyP166AndP1532()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryTrophyNationalTeamIntersectionAsync(TrophyQid, NationalTeamQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain($"?player wdt:P166 wd:{TrophyQid}."));
        Assert.That(sentQuery, Does.Contain($"?player wdt:P1532 wd:{NationalTeamQid}."));
        Assert.That(sentQuery, Does.Not.Contain("P27"));
        Assert.That(sentQuery, Does.Not.Contain("P54"));
        Assert.That(sentQuery, Does.Not.Contain("P1344"), "this is the individual-award shape, not the team-competition edition join");
    }

    [Test]
    public async Task QueryTrophyNationalTeamIntersectionAsync_NoMatchingRows_ReturnsEmptyWithoutThrowing()
    {
        const string json = """{ "results": { "bindings": [] } }""";
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryTrophyNationalTeamIntersectionAsync(TrophyQid, NationalTeamQid);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QueryTrophyNationalTeamIntersectionAsync_RejectsNonQidTrophyValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryTrophyNationalTeamIntersectionAsync("Ballon d'Or", NationalTeamQid));
    }

    [Test]
    public void QueryTrophyNationalTeamIntersectionAsync_RejectsNonQidCountryValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryTrophyNationalTeamIntersectionAsync(TrophyQid, "England"));
    }

    // ---- REQ-1207/S-082: P413 ("position") OPTIONAL binding + BirthYear ---
    // extraction from the existing ?dateOfBirth binding, carried through
    // every one of the five intersection query builders (Country x Club,
    // National-team x Club, Club x Club, Trophy x Country, Trophy x Club) —
    // all five share BuildIntersectionQuery's SELECT/OPTIONAL footer, so
    // each gets its own "SentQuery" assertion (a copy-paste that dropped the
    // shared footer for one builder would otherwise go unnoticed) plus one
    // shared set of ParseBindings-level tests (Position present/absent,
    // BirthYear extraction) run once against QueryCountryClubIntersectionAsync
    // — the same "only the query-shape assertions differ per builder"
    // precedent QueryTrophyCountryIntersectionAsync/QueryTrophyClubIntersectionAsync
    // already establish above.

    [Test]
    public async Task REQ1207_QueryCountryClubIntersectionAsync_SentQuery_FetchesP413PositionAsOptional()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("OPTIONAL { ?player wdt:P413 ?position. }"),
            "P413 must be OPTIONAL, same as photo/alias — a player with no recorded position must still match the rest of the query");
        Assert.That(sentQuery, Does.Contain("?dateOfBirth"), "no new binding needed for BirthYear — ?dateOfBirth is already a mandatory part of the WHERE clause");
        // Bug fix (2026-08-02, bug-bundle): ?position alone is a raw QID
        // URI, never a human-readable string — ?positionLabel (auto-resolved
        // by the existing SERVICE wikibase:label block, same as
        // ?playerLabel) is what must actually be requested, or Player.Position
        // ends up persisted as e.g. "http://www.wikidata.org/entity/Q336286"
        // instead of "midfielder".
        Assert.That(sentQuery, Does.Contain("?positionLabel"),
            "the raw ?position binding is a QID URI, not a label — ?positionLabel must be requested so the label service resolves it");
    }

    [Test]
    public async Task REQ1207_QueryNationalTeamClubIntersectionAsync_SentQuery_FetchesP413PositionAsOptional()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryNationalTeamClubIntersectionAsync(NationalTeamQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("OPTIONAL { ?player wdt:P413 ?position. }"));
    }

    [Test]
    public async Task REQ1207_QueryClubClubIntersectionAsync_SentQuery_FetchesP413PositionAsOptional()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryClubClubIntersectionAsync(ClubAQid, ClubBQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("OPTIONAL { ?player wdt:P413 ?position. }"));
    }

    [Test]
    public async Task REQ1207_QueryTrophyCountryIntersectionAsync_SentQuery_FetchesP413PositionAsOptional()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryTrophyCountryIntersectionAsync(TrophyQid, CountryQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("OPTIONAL { ?player wdt:P413 ?position. }"),
            "this query shape has no P54 clause at all, but P413/dateOfBirth are on the shared player-level predicates, not the club-membership half, so they must still be present");
    }

    [Test]
    public async Task REQ1207_QueryTrophyClubIntersectionAsync_SentQuery_FetchesP413PositionAsOptional()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryTrophyClubIntersectionAsync(TrophyQid, ClubQid);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("OPTIONAL { ?player wdt:P413 ?position. }"));
    }

    [Test]
    public async Task REQ1207_QueryCountryClubIntersectionAsync_ParsesPosition_WhenP413Present()
    {
        // "positionLabel", not "position" (bug fix, 2026-08-02) — WDQS's
        // label service resolves ?position (a raw QID) into ?positionLabel
        // (the human-readable string), and that's the binding ParseBindings
        // actually reads.
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "positionLabel": { "type": "literal", "value": "forward" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Position, Is.EqualTo("forward"));
    }

    [Test]
    public async Task REQ1207_QueryCountryClubIntersectionAsync_PositionIsNull_WhenP413Absent()
    {
        // No "positionLabel" binding at all — a player with no Wikidata P413
        // statement (REQ-1207's explicit "null is a valid, expected value,
        // never an error").
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
        Assert.That(result[0].Position, Is.Null);
    }

    [Test]
    public async Task REQ1207_QueryCountryClubIntersectionAsync_ParsesBirthYear_FromExistingDateOfBirthBinding()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "dateOfBirth": { "type": "literal", "value": "1977-08-17T00:00:00Z" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].BirthYear, Is.EqualTo(1977));
    }

    [Test]
    public async Task REQ1207_QueryCountryClubIntersectionAsync_BirthYearIsNull_WhenDateOfBirthBindingAbsent()
    {
        // Every real player match requires ?dateOfBirth to be bound
        // (ADR-0025's mandatory pool filter) — this exercises the parser's
        // own defensive "absent means null" path, e.g. against a
        // hand-crafted/malformed response.
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
        Assert.That(result[0].BirthYear, Is.Null);
    }

    [Test]
    public async Task REQ1207_QueryCountryClubIntersectionAsync_PositionAndBirthYear_TakeFirstNonNullValueSeenAcrossMultipleRows()
    {
        // Same "first non-null value seen" shape PhotoUrl already uses — a
        // player with N aliases can produce multiple result rows for the
        // same player, only one of which needs to carry positionLabel/
        // dateOfBirth for the value to be captured.
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "Titi" }, "positionLabel": { "type": "literal", "value": "forward" }, "dateOfBirth": { "type": "literal", "value": "1977-08-17T00:00:00Z" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "TH14" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryCountryClubIntersectionAsync(CountryQid, ClubQid);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Position, Is.EqualTo("forward"));
        Assert.That(result[0].BirthYear, Is.EqualTo(1977));
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

    // ---- QueryPlayerPhotoByNameAsync (REQ-216/ADR-0057, wrong-guess photo
    // lookup) --------------------------------------------------------------
    // A single, name-based (not QID-based) lookup — the one case
    // QueryPlayerPhotosByQidsAsync above can't serve, since a wrong-but-real
    // guess has no existing Player row (and so no WikidataQid) to look up
    // by. Same throw-on-failure contract as QueryPlayerPhotosByQidsAsync,
    // but unlike every other query in this file, this one both filters by a
    // free-text string and caps its result set (LIMIT 1) — see
    // IWikidataClient's own doc comment for why.

    [Test]
    public async Task REQ216_QueryPlayerPhotoByNameAsync_SentQuery_MatchesFootballerByCaseInsensitiveLabelOrAlias_AndLimitsToOne()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerPhotoByNameAsync("Clarence Seedorf");

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P106 wd:Q937857"));
        Assert.That(sentQuery, Does.Contain("rdfs:label ?matchedLabel"));
        Assert.That(sentQuery, Does.Contain("skos:altLabel ?matchedLabel"));
        Assert.That(sentQuery, Does.Contain("LCASE(STR(?matchedLabel)) = LCASE(\"Clarence Seedorf\")"));
        Assert.That(sentQuery, Does.Contain("LIMIT 1"));
    }

    [Test]
    public async Task REQ216_QueryPlayerPhotoByNameAsync_SentQuery_FetchesP18ImageAsOptional()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerPhotoByNameAsync("Clarence Seedorf");

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("OPTIONAL { ?player wdt:P18 ?photo. }"));
    }

    [Test]
    public async Task REQ216_QueryPlayerPhotoByNameAsync_NoMatchingRows_ReturnsNullWithoutThrowing()
    {
        var client = new WikidataClient(BuildHttpClient(
            FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""")));

        var result = await client.QueryPlayerPhotoByNameAsync("Nobody Real");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task REQ216_QueryPlayerPhotoByNameAsync_ReturnsCanonicalNameAndPhoto_WhenBothPresent()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q188207" },
                    "playerLabel": { "type": "literal", "value": "Clarence Seedorf" },
                    "photo": { "type": "uri", "value": "http://commons.wikimedia.org/wiki/Special:FilePath/Clarence%20Seedorf.jpg" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerPhotoByNameAsync("clarence seedorf");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.FullName, Is.EqualTo("Clarence Seedorf"));
        Assert.That(result.PhotoUrl, Is.EqualTo("http://commons.wikimedia.org/wiki/Special:FilePath/Clarence%20Seedorf.jpg"));
    }

    [Test]
    public async Task REQ216_QueryPlayerPhotoByNameAsync_ReturnsNameWithNullPhoto_WhenP18Absent()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q188207" },
                    "playerLabel": { "type": "literal", "value": "Clarence Seedorf" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerPhotoByNameAsync("clarence seedorf");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.FullName, Is.EqualTo("Clarence Seedorf"));
        Assert.That(result.PhotoUrl, Is.Null, "a resolved name with no P18 statement is a normal, error-free outcome (ADR-0057)");
    }

    [Test]
    public async Task REQ216_QueryPlayerPhotoByNameAsync_BlankName_ReturnsNullWithoutSendingARequest()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        var result = await client.QueryPlayerPhotoByNameAsync("   ");

        Assert.That(result, Is.Null);
        Assert.That(handler.LastRequest, Is.Null);
    }

    [Test]
    public void REQ216_QueryPlayerPhotoByNameAsync_HttpErrorStatus_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningStatus(System.Net.HttpStatusCode.InternalServerError)));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerPhotoByNameAsync("Clarence Seedorf"));
    }

    [Test]
    public void REQ216_QueryPlayerPhotoByNameAsync_Timeout_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerPhotoByNameAsync("Clarence Seedorf"));
    }

    [Test]
    public void REQ216_QueryPlayerPhotoByNameAsync_MalformedJson_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("not valid json")));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerPhotoByNameAsync("Clarence Seedorf"));
    }

    [Test]
    public async Task REQ216_QueryPlayerPhotoByNameAsync_NameContainingQuote_EscapesItInTheSentQuery()
    {
        // This file's first query to interpolate free, player-supplied text
        // — a guessed name containing a double quote must not be able to
        // break out of the SPARQL string literal.
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerPhotoByNameAsync("O\"Malley");

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("LCASE(\"O\\\"Malley\")"));
    }

    // ---- QueryPlayerCareerStintsByQidsAsync (ADR-0054, xG Path's own
    // direct career fetch) -----------------------------------------------
    // Batched, direct-by-QID lookup — a VALUES clause, not an intersection
    // query — with the SAME throw-on-failure contract as
    // QueryPlayerPhotosByQidsAsync above. Unlike that method (and unlike
    // every intersection query's ?clubStatement footer), ?club is itself
    // part of the SELECT here — this is what makes it "every club," not
    // "this one club."

    [Test]
    public async Task ADR0054_QueryPlayerCareerStintsByQidsAsync_SentQuery_ContainsValuesClauseOverEveryQid()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerCareerStintsByQidsAsync(["Q1519", "Q9617", "Q7156"]);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("VALUES ?player { wd:Q1519 wd:Q9617 wd:Q7156 }"));
    }

    // Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203 follow-up,
    // ADR-0059): ?club must be projected in the SELECT (it was already
    // bound in the query body via ?clubStatement ps:P54 ?club, just not
    // previously selected) — the underlying QID is what a caller with
    // access to ClubDefinition canonicalizes ClubName against. See
    // WikidataCareerStintEntry.ClubQid's own doc comment for the full
    // "why."
    [Test]
    public async Task REQ1203_QueryPlayerCareerStintsByQidsAsync_SentQuery_SelectsClubQid()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("SELECT ?player ?club ?clubLabel"));
    }

    // Full P54 statement path, not the truthy wdt:P54 shortcut — same
    // "must never silently drop to best-rank-only" reasoning as
    // BuildCountryClubIntersectionQuery's own test coverage. This is the one
    // property that would make a "full career" fetch just as incomplete as
    // the byproduct data it exists to replace.
    [Test]
    public async Task ADR0054_QueryPlayerCareerStintsByQidsAsync_SentQuery_UsesFullP54StatementPath_NotTruthyShortcut()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("?player p:P54 ?clubStatement."));
        Assert.That(sentQuery, Does.Contain("?clubStatement ps:P54 ?club."));
        Assert.That(sentQuery, Does.Contain("MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }"));
        Assert.That(sentQuery, Does.Not.Contain("?player wdt:P54"));
    }

    // Bug fix (2026-08-02, bug-bundle, REQ-1203): Wikidata models national
    // team caps under this same P54 property, so without an explicit
    // exclusion, "Switzerland men's national football team" (or any other
    // national side) would come back as a "club" stint — directly violating
    // REQ-1203's "national team caps/appearances are never revealed as a
    // clue for this game" acceptance criterion. Uses the transitive P279*
    // subclass path (not a direct P31 check) since a specific national
    // team's own P31 is typically a narrower subclass of Q6979593, not that
    // class itself — see NationalTeamClassWikidataQid's own comment in
    // WikidataClient.cs.
    [Test]
    public async Task REQ1203_QueryPlayerCareerStintsByQidsAsync_SentQuery_ExcludesNationalTeams()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("MINUS { ?club wdt:P31/wdt:P279* wd:Q6979593. }"),
            "must exclude Q6979593 ('national association football team') and its subclasses from ?club, " +
            "or a national team caps stint leaks into xG Path's club-reveal clues");
    }

    [Test]
    public async Task ADR0054_QueryPlayerCareerStintsByQidsAsync_SentQuery_NeverContainsOrderByLimitOrOffset()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Not.Contain("ORDER BY"));
        Assert.That(sentQuery, Does.Not.Contain("LIMIT"));
        Assert.That(sentQuery, Does.Not.Contain("OFFSET"));
    }

    [Test]
    public async Task ADR0054_QueryPlayerCareerStintsByQidsAsync_ReturnsEveryDistinctClubStint_GroupedByQid()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "clubLabel": { "type": "literal", "value": "Monaco" }, "startTime": { "type": "literal", "value": "1994-01-01T00:00:00Z" }, "endTime": { "type": "literal", "value": "1999-01-01T00:00:00Z" }, "numberOfMatches": { "type": "literal", "value": "105" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "clubLabel": { "type": "literal", "value": "Arsenal" }, "startTime": { "type": "literal", "value": "1999-01-01T00:00:00Z" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]);

        Assert.That(result["Q1519"], Has.Count.EqualTo(2));
        Assert.That(result["Q1519"], Does.Contain(new WikidataCareerStintEntry("Monaco", 1994, 1999, 105)));
        Assert.That(result["Q1519"], Does.Contain(new WikidataCareerStintEntry("Arsenal", 1999, null, null)));
    }

    // Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203 follow-up,
    // ADR-0059): a ?club binding must be extracted into ClubQid the same
    // way ?player is — the trailing URI segment. This is the QID a
    // canonicalization-capable caller (PlayerCareerStintRefreshService/
    // PlayerCareerPrefetchService) resolves against ClubDefinition.
    [Test]
    public async Task REQ1203_QueryPlayerCareerStintsByQidsAsync_ExtractsClubQidFromClubBinding()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "club": { "type": "uri", "value": "http://www.wikidata.org/entity/Q704" }, "clubLabel": { "type": "literal", "value": "Olympique Lyonnais" }, "startTime": { "type": "literal", "value": "2000-01-01T00:00:00Z" }, "endTime": { "type": "literal", "value": "2003-01-01T00:00:00Z" }, "numberOfMatches": { "type": "literal", "value": "90" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]);

        Assert.That(result["Q1519"], Has.Count.EqualTo(1));
        Assert.That(result["Q1519"][0].ClubQid, Is.EqualTo("Q704"));
        Assert.That(result["Q1519"][0].ClubName, Is.EqualTo("Olympique Lyonnais"),
            "the client itself never canonicalizes against ClubDefinition — see WikidataCareerStintEntry.ClubQid's own doc comment for why that boundary lives one layer up");
    }

    // A row missing the ?club binding entirely (defensive-only case —
    // should not happen in production, ?club is a mandatory,
    // non-OPTIONAL match) must not be dropped: it simply carries a null
    // ClubQid, same "fall back to the best-effort label" contract as an
    // unresolved QID.
    [Test]
    public async Task REQ1203_QueryPlayerCareerStintsByQidsAsync_RowWithNoClubBinding_HasNullClubQid_IsNotDropped()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "clubLabel": { "type": "literal", "value": "Some Club" }, "startTime": { "type": "literal", "value": "2000-01-01T00:00:00Z" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]);

        Assert.That(result["Q1519"], Has.Count.EqualTo(1));
        Assert.That(result["Q1519"][0].ClubQid, Is.Null);
    }

    // Regression test for the exact bug reported with a screenshot: an xG
    // Path puzzle showed the same real career stint as two separate path
    // nodes — one labeled "Liverpool," one labeled "Liverpool F.C." —
    // because BuildPlayerCareerStintsByQidsQuery never selects the
    // underlying ?club QID (only ?clubLabel), so ParseCareerStintBindings'
    // HashSet dedup had no way to recognize the two rows as the same real
    // stint. Two rows, identical (startTime, endTime, numberOfMatches) but
    // differing only by the club-name legal-suffix variant, must collapse
    // into exactly one WikidataCareerStintEntry, keyed on the normalized
    // ("Liverpool") form.
    [Test]
    public async Task REQ1203_QueryPlayerCareerStintsByQidsAsync_CollapsesSameStint_WhenClubLabelDiffersOnlyByLegalSuffix()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "clubLabel": { "type": "literal", "value": "Liverpool" }, "startTime": { "type": "literal", "value": "2010-01-01T00:00:00Z" }, "endTime": { "type": "literal", "value": "2015-01-01T00:00:00Z" }, "numberOfMatches": { "type": "literal", "value": "25" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "clubLabel": { "type": "literal", "value": "Liverpool F.C." }, "startTime": { "type": "literal", "value": "2010-01-01T00:00:00Z" }, "endTime": { "type": "literal", "value": "2015-01-01T00:00:00Z" }, "numberOfMatches": { "type": "literal", "value": "25" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]);

        Assert.That(result["Q1519"], Has.Count.EqualTo(1),
            "the 'Liverpool'/'Liverpool F.C.' rows are the same real stint and must collapse into one entry");
        Assert.That(result["Q1519"], Does.Contain(new WikidataCareerStintEntry("Liverpool", 2010, 2015, 25)));
    }

    // Same normalization, but exercised through varied suffix forms and
    // positions to pin down NormalizeClubName's exact contract, not just
    // the one screenshot scenario above.
    [TestCase("Liverpool FC", "Liverpool")]
    [TestCase("Liverpool A.F.C.", "Liverpool")]
    [TestCase("Bournemouth AFC", "Bournemouth")]
    [TestCase("AFC Bournemouth", "AFC Bournemouth", Description = "a leading 'AFC' is a different, legitimate naming convention and must NOT be stripped")]
    [TestCase("Deportivo Alavés", "Deportivo Alavés", Description = "must not match 'FC' as a substring inside an unrelated word")]
    [TestCase("FC", "FC", Description = "a label that IS exactly the suffix token, with nothing preceding it, must be left untouched (the trimmed.Length <= suffix.Length guard)")]
    [TestCase("AFC", "AFC", Description = "same guard, exercised against the 3-character suffix token")]
    public async Task REQ1203_QueryPlayerCareerStintsByQidsAsync_NormalizesClubLegalSuffix(string rawLabel, string expectedNormalized)
    {
        var json = $$"""
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "clubLabel": { "type": "literal", "value": "{{rawLabel}}" }, "startTime": { "type": "literal", "value": "2010-01-01T00:00:00Z" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]);

        Assert.That(result["Q1519"][0].ClubName, Is.EqualTo(expectedNormalized));
    }

    // Locks in a KNOWN, ACCEPTED limitation of the club-name normalization
    // fix above (quality-gate finding, 2026-08-03) — see
    // ParseCareerStintBindings' own comment for the full reasoning. Dedup
    // is still keyed on the FULL (ClubName, StartYear, EndYear,
    // AppearanceCount) tuple: normalizing ClubName alone only collapses
    // rows that also agree on every other field. Two rows for what could
    // plausibly be the same real stint (same normalized club, same
    // start/end) but that disagree on AppearanceCount — one row's P1350
    // qualifier absent (null), the other's present (25) — currently do
    // NOT merge, and both survive as separate entries. This is
    // deliberate, not an oversight: treating null as "matches anything"
    // would risk merging two GENUINELY different stints at the same club
    // with matching dates but different, both-known appearance counts.
    // This test exists so that a future change to this behavior is a
    // conscious decision (with its own test update), not an accidental
    // regression.
    [Test]
    public async Task REQ1203_QueryPlayerCareerStintsByQidsAsync_DoesNotMergeSameClubAndDates_WhenAppearanceCountDiffers()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "clubLabel": { "type": "literal", "value": "Liverpool" }, "startTime": { "type": "literal", "value": "2010-01-01T00:00:00Z" }, "endTime": { "type": "literal", "value": "2015-01-01T00:00:00Z" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "clubLabel": { "type": "literal", "value": "Liverpool F.C." }, "startTime": { "type": "literal", "value": "2010-01-01T00:00:00Z" }, "endTime": { "type": "literal", "value": "2015-01-01T00:00:00Z" }, "numberOfMatches": { "type": "literal", "value": "25" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]);

        Assert.That(result["Q1519"], Has.Count.EqualTo(2),
            "documented current limitation: a null vs. known AppearanceCount still prevents merging, even though ClubName and dates now normalize/match");
        Assert.That(result["Q1519"], Does.Contain(new WikidataCareerStintEntry("Liverpool", 2010, 2015, null)));
        Assert.That(result["Q1519"], Does.Contain(new WikidataCareerStintEntry("Liverpool", 2010, 2015, 25)));
    }

    [Test]
    public async Task ADR0054_QueryPlayerCareerStintsByQidsAsync_QidWithNoP54Data_IsAbsentFromResult()
    {
        const string json = """{ "results": { "bindings": [] } }""";
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]);

        Assert.That(result.ContainsKey("Q1519"), Is.False);
    }

    // A row with no startTime binding carries zero usable information
    // (StartYear is non-nullable on WikidataCareerStintEntry) and must not
    // produce a stint — same "zero information, never persisted" contract
    // ParseBindings' own CareerStints field follows.
    [Test]
    public async Task ADR0054_QueryPlayerCareerStintsByQidsAsync_RowWithNoStartTime_IsSkipped()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "clubLabel": { "type": "literal", "value": "Arsenal" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]);

        Assert.That(result.ContainsKey("Q1519"), Is.False);
    }

    [Test]
    public async Task ADR0054_QueryPlayerCareerStintsByQidsAsync_EmptyQidList_ReturnsEmptyDictionaryWithoutSendingARequest()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        var result = await client.QueryPlayerCareerStintsByQidsAsync([]);

        Assert.That(result, Is.Empty);
        Assert.That(handler.LastRequest, Is.Null);
    }

    [Test]
    public void ADR0054_QueryPlayerCareerStintsByQidsAsync_HttpErrorStatus_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningStatus(System.Net.HttpStatusCode.InternalServerError)));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]));
    }

    [Test]
    public void ADR0054_QueryPlayerCareerStintsByQidsAsync_Timeout_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]));
    }

    [Test]
    public void ADR0054_QueryPlayerCareerStintsByQidsAsync_MalformedJson_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("not valid json")));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerCareerStintsByQidsAsync(["Q1519"]));
    }

    [Test]
    public void ADR0054_QueryPlayerCareerStintsByQidsAsync_RejectsNonQidValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QueryPlayerCareerStintsByQidsAsync(["Q1519", "Arsenal"]));
    }

    // ---- QueryPlayerPoolByNationalityAsync (ADR-0055, xG Path candidate-pool
    // widening) -------------------------------------------------------------
    // The nationality-scoped sibling of QueryPlayerPoolBirthYearAsync — same
    // throw-on-failure contract, same bounded-query discipline, filtered by
    // P27/P1532 instead of sliced by birth year.

    [Test]
    public async Task ADR0055_QueryPlayerPoolByNationalityAsync_UsesP27_WhenNotCountryForSportProperty()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerPoolByNationalityAsync("Q142", useCountryForSportProperty: false);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("?player wdt:P27 wd:Q142."));
        Assert.That(sentQuery, Does.Not.Contain("wdt:P1532"));
    }

    // REQ-114/ADR-0035: England/Scotland/Wales/Northern Ireland use P1532
    // ("country for sport"), not P27 — same split
    // QueryNationalTeamClubIntersectionAsync's own test coverage pins for
    // the intersection queries.
    [Test]
    public async Task ADR0055_QueryPlayerPoolByNationalityAsync_UsesP1532_WhenCountryForSportProperty()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerPoolByNationalityAsync("Q21", useCountryForSportProperty: true);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("?player wdt:P1532 wd:Q21."));
        Assert.That(sentQuery, Does.Not.Contain("wdt:P27"));
    }

    [Test]
    public async Task ADR0055_QueryPlayerPoolByNationalityAsync_SentQuery_NeverContainsOrderByLimitOrOffset()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerPoolByNationalityAsync("Q142", useCountryForSportProperty: false);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Not.Contain("ORDER BY"));
        Assert.That(sentQuery, Does.Not.Contain("LIMIT"));
        Assert.That(sentQuery, Does.Not.Contain("OFFSET"));
    }

    [Test]
    public async Task ADR0055_QueryPlayerPoolByNationalityAsync_ParsesEveryDistinctPlayer()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "birthYear": { "type": "literal", "value": "1977" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q9617" }, "playerLabel": { "type": "literal", "value": "Someone Else" }, "birthYear": { "type": "literal", "value": "1990" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerPoolByNationalityAsync("Q142", useCountryForSportProperty: false);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(r => r.WikidataQid), Is.EquivalentTo(new[] { "Q1519", "Q9617" }));
    }

    // Bug fix (2026-08-03, user-tester report): a real report showed Michael
    // Owen's (the footballer, actually born 1979) autocomplete suggestion
    // carrying BirthYear 1976. wdt:P569 already collapses to one preferred-
    // rank statement whenever Wikidata has one, so two rows for the same
    // ?player disagreeing on birth year within one response (this query has
    // no per-year window, unlike QueryPlayerPoolBirthYearAsync, so both of a
    // player's conflicting P569 statements can land as separate rows here)
    // means Wikidata itself has more than one non-deprecated, non-preferred
    // statement with no stated preference between them. There is no
    // principled way to pick between them from this response alone — see
    // ParseNameIndexBindings' own doc comment for why the ambiguous value is
    // nulled out instead of keeping whichever row WDQS happened to list
    // first (an artifact of its own internal ordering, not a correctness
    // signal).
    [Test]
    public async Task ParseNameIndexBindings_ConflictingBirthYearRowsForSameQid_NullsOutBirthYear_RatherThanKeepingWhicheverArrivedFirst()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q184895" }, "playerLabel": { "type": "literal", "value": "Michael Owen" }, "birthYear": { "type": "literal", "value": "1976" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q184895" }, "playerLabel": { "type": "literal", "value": "Michael Owen" }, "birthYear": { "type": "literal", "value": "1979" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerPoolByNationalityAsync("Q145", useCountryForSportProperty: false);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].FullName, Is.EqualTo("Michael Owen"));
        Assert.That(result[0].BirthYear, Is.Null,
            "neither conflicting value is trustworthy, so the ambiguous birth year is dropped rather than guessed");
    }

    [Test]
    public void ADR0055_QueryPlayerPoolByNationalityAsync_HttpErrorStatus_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningStatus(System.Net.HttpStatusCode.InternalServerError)));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerPoolByNationalityAsync("Q142", useCountryForSportProperty: false));
    }

    [Test]
    public void ADR0055_QueryPlayerPoolByNationalityAsync_Timeout_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerPoolByNationalityAsync("Q142", useCountryForSportProperty: false));
    }

    [Test]
    public void ADR0055_QueryPlayerPoolByNationalityAsync_MalformedJson_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("not valid json")));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerPoolByNationalityAsync("Q142", useCountryForSportProperty: false));
    }

    // ---- QueryPlayerPositionsAndBirthYearsByQidsAsync (REQ-1207 backfill) --
    // positionLabel-specific coverage: the query-shape/error-contract tests
    // for this method never existed before this bug-bundle fix (the batch
    // query itself only had ParsePositionBirthYearBindings-level coverage
    // indirectly via PlayerPositionBirthYearBackfillServiceTests' fake) — see
    // BuildPlayerPositionsAndBirthYearsByQidsQuery's own comment for why this
    // query specifically had no SERVICE wikibase:label block at all before
    // this fix, unlike every other query in this file.

    [Test]
    public async Task REQ1207_QueryPlayerPositionsAndBirthYearsByQidsAsync_SentQuery_RequestsPositionLabelViaLabelService()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerPositionsAndBirthYearsByQidsAsync(["Q1519"]);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("OPTIONAL { ?player wdt:P413 ?position. }"));
        Assert.That(sentQuery, Does.Contain("?positionLabel"),
            "the raw ?position binding is a QID URI, not a label — ?positionLabel must be requested");
        Assert.That(sentQuery, Does.Contain("SERVICE wikibase:label"),
            "this query had no label service at all before the bug fix — without it, ?positionLabel can never resolve");
    }

    [Test]
    public async Task REQ1207_QueryPlayerPositionsAndBirthYearsByQidsAsync_ParsesPositionLabel_NotRawPosition()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "positionLabel": { "type": "literal", "value": "midfielder" }, "dateOfBirth": { "type": "literal", "value": "1977-08-17T00:00:00Z" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerPositionsAndBirthYearsByQidsAsync(["Q1519"]);

        Assert.That(result["Q1519"].Position, Is.EqualTo("midfielder"));
        Assert.That(result["Q1519"].BirthYear, Is.EqualTo(1977));
    }

    // ---- QuerySitelinkCountsByQidsAsync (ADR-0056, xG Path's familiarity ---
    // signal) — same VALUES-clause-over-a-bounded-batch shape and
    // throw-on-failure error contract as QueryPlayerPhotosByQidsAsync/
    // QueryPlayerPositionsAndBirthYearsByQidsAsync above.

    [Test]
    public async Task ADR0056_QuerySitelinkCountsByQidsAsync_SentQuery_ContainsValuesClauseOverEveryQid()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QuerySitelinkCountsByQidsAsync(["Q1519", "Q9617"]);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("VALUES ?player { wd:Q1519 wd:Q9617 }"));
        Assert.That(sentQuery, Does.Contain("wikibase:sitelinks"));
    }

    [Test]
    public async Task ADR0056_QuerySitelinkCountsByQidsAsync_SentQuery_NeverContainsOrderByLimitOrOffset()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QuerySitelinkCountsByQidsAsync(["Q1519"]);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Not.Contain("ORDER BY"));
        Assert.That(sentQuery, Does.Not.Contain("LIMIT"));
        Assert.That(sentQuery, Does.Not.Contain("OFFSET"));
    }

    [Test]
    public async Task ADR0056_QuerySitelinkCountsByQidsAsync_ReturnsDictionaryKeyedByQid_ForQidsWithASitelinkCount()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "sitelinks": { "type": "literal", "value": "87" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q9617" }, "sitelinks": { "type": "literal", "value": "3" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QuerySitelinkCountsByQidsAsync(["Q1519", "Q9617"]);

        Assert.That(result["Q1519"], Is.EqualTo(87));
        Assert.That(result["Q9617"], Is.EqualTo(3));
    }

    [Test]
    public async Task ADR0056_QuerySitelinkCountsByQidsAsync_QidWithNoSitelinksBinding_IsAbsentFromResult()
    {
        const string json = """{ "results": { "bindings": [] } }""";
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QuerySitelinkCountsByQidsAsync(["Q1519"]);

        Assert.That(result.ContainsKey("Q1519"), Is.False,
            "absent means 'unknown,' never 'confirmed 0' — the caller must not treat a missing row as a familiar/unfamiliar verdict");
    }

    [Test]
    public async Task ADR0056_QuerySitelinkCountsByQidsAsync_EmptyQidList_ReturnsEmptyDictionaryWithoutSendingARequest()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        var result = await client.QuerySitelinkCountsByQidsAsync([]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ADR0056_QuerySitelinkCountsByQidsAsync_HttpErrorStatus_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningStatus(System.Net.HttpStatusCode.InternalServerError)));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QuerySitelinkCountsByQidsAsync(["Q1519"]));
    }

    [Test]
    public void ADR0056_QuerySitelinkCountsByQidsAsync_Timeout_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QuerySitelinkCountsByQidsAsync(["Q1519"]));
    }

    [Test]
    public void ADR0056_QuerySitelinkCountsByQidsAsync_MalformedJson_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("not valid json")));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QuerySitelinkCountsByQidsAsync(["Q1519"]));
    }

    [Test]
    public void ADR0056_QuerySitelinkCountsByQidsAsync_RejectsNonQidValue()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("{}")));

        Assert.ThrowsAsync<ArgumentException>(() => client.QuerySitelinkCountsByQidsAsync(["Q1519", "Arsenal"]));
    }

    // ---- QueryPlayerCareerAndNationalityByNameAsync (REQ-509/REQ-510,
    // S-090, admin review live lookup) ---------------------------------------
    // Combines QueryPlayerPhotoByNameAsync's name-match shape with P27
    // citizenship and P54's full statement-path club history — see
    // IWikidataClient's own doc comment for the full error-contract
    // reasoning (always throws, no throwOnTimeout param, unlike the five
    // intersection queries).

    [Test]
    public async Task REQ509_QueryPlayerCareerAndNationalityByNameAsync_SentQuery_MatchesFootballerByCaseInsensitiveLabelOrAlias_AndLimitsSubqueryToOne()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerCareerAndNationalityByNameAsync("Clarence Seedorf");

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P106 wd:Q937857"));
        Assert.That(sentQuery, Does.Contain("rdfs:label ?matchedLabel"));
        Assert.That(sentQuery, Does.Contain("skos:altLabel ?matchedLabel"));
        Assert.That(sentQuery, Does.Contain("LCASE(STR(?matchedLabel)) = LCASE(\"Clarence Seedorf\")"));
        Assert.That(sentQuery, Does.Contain("LIMIT 1"));
    }

    [Test]
    public async Task REQ509_QueryPlayerCareerAndNationalityByNameAsync_SentQuery_FetchesP27AndFullStatementPathP54()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        await client.QueryPlayerCareerAndNationalityByNameAsync("Clarence Seedorf");

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P27 ?nationality"));
        Assert.That(sentQuery, Does.Contain("p:P54 ?clubStatement"));
        Assert.That(sentQuery, Does.Contain("ps:P54 ?club"));
        Assert.That(sentQuery, Does.Contain("wikibase:DeprecatedRank"), "must exclude deprecated-rank P54 statements, same as every other full-statement-path P54 query in this file");
    }

    [Test]
    public async Task REQ509_QueryPlayerCareerAndNationalityByNameAsync_NoMatchingRows_ReturnsNullWithoutThrowing()
    {
        var client = new WikidataClient(BuildHttpClient(
            FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""")));

        var result = await client.QueryPlayerCareerAndNationalityByNameAsync("Nobody Real");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task REQ509_QueryPlayerCareerAndNationalityByNameAsync_ReturnsQidNameNationalityAndClubs()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q188207" },
                    "playerLabel": { "type": "literal", "value": "Clarence Seedorf" },
                    "nationalityLabel": { "type": "literal", "value": "Netherlands" },
                    "club": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1543" },
                    "clubLabel": { "type": "literal", "value": "AC Milan" },
                    "startTime": { "type": "literal", "value": "2002-01-01T00:00:00Z" },
                    "endTime": { "type": "literal", "value": "2009-01-01T00:00:00Z" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q188207" },
                    "playerLabel": { "type": "literal", "value": "Clarence Seedorf" },
                    "nationalityLabel": { "type": "literal", "value": "Netherlands" },
                    "club": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1543" },
                    "clubLabel": { "type": "literal", "value": "AC Milan" },
                    "startTime": { "type": "literal", "value": "2002-01-01T00:00:00Z" },
                    "endTime": { "type": "literal", "value": "2009-01-01T00:00:00Z" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q188207" },
                    "playerLabel": { "type": "literal", "value": "Clarence Seedorf" },
                    "nationalityLabel": { "type": "literal", "value": "Netherlands" },
                    "club": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1420" },
                    "clubLabel": { "type": "literal", "value": "Real Madrid" },
                    "startTime": { "type": "literal", "value": "1996-01-01T00:00:00Z" },
                    "endTime": { "type": "literal", "value": "2002-01-01T00:00:00Z" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerCareerAndNationalityByNameAsync("clarence seedorf");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.WikidataQid, Is.EqualTo("Q188207"));
        Assert.That(result.FullName, Is.EqualTo("Clarence Seedorf"));
        Assert.That(result.Nationality, Is.EqualTo("Netherlands"));
        Assert.That(result.Clubs, Has.Count.EqualTo(2), "the duplicated AC Milan row (same clubLabel) must dedupe to one club name, HashSet<string> shape");
        Assert.That(result.Clubs, Is.EquivalentTo(new[] { "AC Milan", "Real Madrid" }));
    }

    // Bug fix (2026-08-08, REQ-509/510, found in hand-review before this
    // story's frontend/test work started): not every real P54
    // club-membership statement carries a P580 start-time qualifier —
    // plenty of lesser-known clubs (exactly the data-completeness gap this
    // admin feature exists to help fill, per MVP-SCOPE.md) have the
    // membership fact recorded with no start/end date at all. Club
    // detection must never be gated on ?startTime also being bound — see
    // WikidataPlayerCareerLookupResult's own doc comment for the full
    // "why" this method's Clubs is a plain club-name list rather than
    // WikidataCareerStintEntry.
    [Test]
    public async Task REQ509_QueryPlayerCareerAndNationalityByNameAsync_IncludesClub_WhenStartTimeNeverBound()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q188207" },
                    "playerLabel": { "type": "literal", "value": "Clarence Seedorf" },
                    "nationalityLabel": { "type": "literal", "value": "Netherlands" },
                    "club": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1543" },
                    "clubLabel": { "type": "literal", "value": "AC Milan" },
                    "startTime": { "type": "literal", "value": "2002-01-01T00:00:00Z" },
                    "endTime": { "type": "literal", "value": "2009-01-01T00:00:00Z" } },
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q188207" },
                    "playerLabel": { "type": "literal", "value": "Clarence Seedorf" },
                    "nationalityLabel": { "type": "literal", "value": "Netherlands" },
                    "clubLabel": { "type": "literal", "value": "Some Lesser-Known Club" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerCareerAndNationalityByNameAsync("clarence seedorf");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Clubs, Is.EquivalentTo(new[] { "AC Milan", "Some Lesser-Known Club" }),
            "a club with no bound startTime qualifier must still be recorded, not silently dropped");
    }

    [Test]
    public async Task REQ509_QueryPlayerCareerAndNationalityByNameAsync_ReturnsNationalityNullAndClubsEmpty_WhenNeitherBound()
    {
        const string json = """
            {
              "results": {
                "bindings": [
                  { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q188207" },
                    "playerLabel": { "type": "literal", "value": "Clarence Seedorf" } }
                ]
              }
            }
            """;
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson(json)));

        var result = await client.QueryPlayerCareerAndNationalityByNameAsync("clarence seedorf");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Nationality, Is.Null, "a matched player with no P27 statement is a normal, error-free outcome");
        Assert.That(result.Clubs, Is.Empty, "a matched player with no P54 statement is a normal, error-free outcome");
    }

    [Test]
    public async Task REQ509_QueryPlayerCareerAndNationalityByNameAsync_BlankName_ReturnsNullWithoutSendingARequest()
    {
        var handler = FakeHttpMessageHandler.ReturningJson("""{ "results": { "bindings": [] } }""");
        var client = new WikidataClient(BuildHttpClient(handler));

        var result = await client.QueryPlayerCareerAndNationalityByNameAsync("   ");

        Assert.That(result, Is.Null);
        Assert.That(handler.LastRequest, Is.Null);
    }

    [Test]
    public void REQ509_QueryPlayerCareerAndNationalityByNameAsync_HttpErrorStatus_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningStatus(System.Net.HttpStatusCode.InternalServerError)));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerCareerAndNationalityByNameAsync("Clarence Seedorf"));
    }

    [Test]
    public void REQ509_QueryPlayerCareerAndNationalityByNameAsync_Timeout_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(
            BuildHttpClient(FakeHttpMessageHandler.NeverResponding()),
            queryTimeout: TimeSpan.FromMilliseconds(50));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerCareerAndNationalityByNameAsync("Clarence Seedorf"));
    }

    [Test]
    public void REQ509_QueryPlayerCareerAndNationalityByNameAsync_MalformedJson_ThrowsWikidataQueryException()
    {
        var client = new WikidataClient(BuildHttpClient(FakeHttpMessageHandler.ReturningJson("not valid json")));

        Assert.ThrowsAsync<WikidataQueryException>(() => client.QueryPlayerCareerAndNationalityByNameAsync("Clarence Seedorf"));
    }
}

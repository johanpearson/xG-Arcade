using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace XGArcade.DataSync.Wikidata;

// SPARQL against Wikidata Query Service — a fundamentally different shape
// from the REST clients elsewhere in DataSync.Clients (implementation-
// document.md §6a). Injected via HttpClient with BaseAddress
// https://query.wikidata.org/ (see Program.cs's AddHttpClient registration).
public class WikidataClient(
    HttpClient httpClient,
    TimeSpan? queryTimeout = null,
    TimeSpan? guessTimeFallbackQueryTimeout = null,
    ILogger<WikidataClient>? logger = null) : IWikidataClient
{
    // Optional param (like queryTimeout) so tests can construct a client
    // without wiring DI's logging; falls back to a real ILogger<T> in
    // production via the AddHttpClient<IWikidataClient, WikidataClient>
    // registration in Program.cs, which supplies one automatically.
    private readonly ILogger<WikidataClient> _logger = logger ?? NullLogger<WikidataClient>.Instance;

    // ADR-0025: Tier 0's player pool is restricted to male footballers born
    // in 1939 or later — Q6581097 is Wikidata's "male" item for P21 (sex or
    // gender). A fixed date, not a rolling window relative to "now" — a
    // deliberate, one-time product decision, not a moving "last N years"
    // rule, so there is no clock/TimeProvider dependency involved.
    private const string MaleWikidataQid = "Q6581097";
    private const string DateOfBirthCutoff = "1939-01-01T00:00:00Z";

    // The same ADR-0025 pool floor as DateOfBirthCutoff above, as a plain
    // year — QueryPlayerPoolBirthYearAsync slices the bulk import by birth
    // year, and PlayerNameIndexImporter iterates from this year to the
    // current one. Keep the two constants in sync.
    public const int FirstEligibleBirthYear = 1939;

    // ADR-0011's original "e.g. 5-10s" was only an illustrative example;
    // the ADR's own evidence (WDQS queries observed taking 9-27s under
    // load) argues for a longer default — 8-10s would treat a large share
    // of genuinely-successful-but-slow queries as timeouts, pushing
    // otherwise-answerable lookups to the Tier 1 fallback unnecessarily.
    // 15s covers most of that reported range without blocking grid
    // generation indefinitely — see ADR-0011's 2026-07-09 addendum.
    // Overridable (constructor param, not a hardcoded const) so tests can
    // exercise the timeout path without waiting out a real multi-second delay.
    private readonly TimeSpan _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(15);

    // ADR-0046 follow-up (2026-07-27): 15s above is REQ-103/grid-generation's
    // budget only — a real report (guessing "Clarence Seedorf" for
    // Ajax x AC Milan) hit exactly the failure mode ADR-0011's own evidence
    // predicted: this club-club shape's two full P54 statement-path joins
    // (BuildClubClubIntersectionQuery's own comment explains why they can't
    // use the cheaper truthy wdt:P54 shortcut) is one of the query shapes
    // that can land in that documented 9-27s range, and 15s doesn't cover
    // it. ADR-0046 already made a timeout here fail closed as "unknown, try
    // again" (LiveLookupUnavailable, HTTP 503) rather than silently
    // persisting a wrong "incorrect" — but at 15s, a query that
    // consistently takes ~20s would fail *every* retry, never actually
    // answering the guess. This second, longer timeout is used ONLY when
    // throwOnTimeout is true (i.e. only WikidataLookupOrigin
    // .GuessTimeFallback) — REQ-103/grid-generation's 15s budget above is
    // completely unaffected. 28s comfortably covers ADR-0011's documented
    // worst case (27s observed) with a small margin, while staying well
    // under Azure Container Apps' ingress idle timeout (no explicit limit
    // configured in infra/bicep/modules/backend-container-app.bicep, and
    // this repo's own default is far more generous than 28s) and the
    // frontend's guess-submission fetch (no client-side AbortSignal/timeout
    // configured on it), so widening this doesn't reintroduce a "failed to
    // fetch" network-level failure — it should just mean an honest 503
    // becomes rare instead of routine for this query shape. Same
    // overridable-for-tests shape as _queryTimeout above.
    private readonly TimeSpan _guessTimeFallbackQueryTimeout = guessTimeFallbackQueryTimeout ?? TimeSpan.FromSeconds(28);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
        string countryWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default)
    {
        if (!WikidataQid.IsValid(countryWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{countryWikidataQid}'", nameof(countryWikidataQid));
        if (!WikidataQid.IsValid(clubWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{clubWikidataQid}'", nameof(clubWikidataQid));

        var query = BuildCountryClubIntersectionQuery(countryWikidataQid, clubWikidataQid);
        return await RunIntersectionQueryAsync("country-club", countryWikidataQid, clubWikidataQid, query, throwOnTimeout, cancellationToken);
    }

    // REQ-114/ADR-0035: England/Scotland/Wales/Northern Ireland's P1532
    // counterpart of QueryCountryClubIntersectionAsync above.
    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryNationalTeamClubIntersectionAsync(
        string nationalTeamWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default)
    {
        if (!WikidataQid.IsValid(nationalTeamWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{nationalTeamWikidataQid}'", nameof(nationalTeamWikidataQid));
        if (!WikidataQid.IsValid(clubWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{clubWikidataQid}'", nameof(clubWikidataQid));

        var query = BuildNationalTeamClubIntersectionQuery(nationalTeamWikidataQid, clubWikidataQid);
        return await RunIntersectionQueryAsync("national-team-club", nationalTeamWikidataQid, clubWikidataQid, query, throwOnTimeout, cancellationToken);
    }

    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryClubClubIntersectionAsync(
        string clubAWikidataQid,
        string clubBWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default)
    {
        if (!WikidataQid.IsValid(clubAWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{clubAWikidataQid}'", nameof(clubAWikidataQid));
        if (!WikidataQid.IsValid(clubBWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{clubBWikidataQid}'", nameof(clubBWikidataQid));

        var query = BuildClubClubIntersectionQuery(clubAWikidataQid, clubBWikidataQid);
        return await RunIntersectionQueryAsync("club-club", clubAWikidataQid, clubBWikidataQid, query, throwOnTimeout, cancellationToken);
    }

    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyCountryIntersectionAsync(
        string trophyWikidataQid,
        string countryWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default)
    {
        if (!WikidataQid.IsValid(trophyWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{trophyWikidataQid}'", nameof(trophyWikidataQid));
        if (!WikidataQid.IsValid(countryWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{countryWikidataQid}'", nameof(countryWikidataQid));

        var query = BuildTrophyCountryIntersectionQuery(trophyWikidataQid, countryWikidataQid);
        return await RunIntersectionQueryAsync("trophy-country", trophyWikidataQid, countryWikidataQid, query, throwOnTimeout, cancellationToken);
    }

    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyClubIntersectionAsync(
        string trophyWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default)
    {
        if (!WikidataQid.IsValid(trophyWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{trophyWikidataQid}'", nameof(trophyWikidataQid));
        if (!WikidataQid.IsValid(clubWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{clubWikidataQid}'", nameof(clubWikidataQid));

        var query = BuildTrophyClubIntersectionQuery(trophyWikidataQid, clubWikidataQid);
        return await RunIntersectionQueryAsync("trophy-club", trophyWikidataQid, clubWikidataQid, query, throwOnTimeout, cancellationToken);
    }

    private async Task<IReadOnlyList<WikidataPlayerMatch>> RunIntersectionQueryAsync(
        string queryKind, string qidA, string qidB, string query, bool throwOnTimeout, CancellationToken cancellationToken)
    {
        var requestUri = $"sparql?query={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

        // ADR-0046 follow-up: throwOnTimeout is exactly "this is the
        // guess-time fallback" (see WikidataLookupService's own throwOnTimeout
        // assignment), so it doubles as the signal for which budget applies
        // — REQ-103/Sync callers always get _queryTimeout, unchanged.
        var effectiveTimeout = throwOnTimeout ? _guessTimeFallbackQueryTimeout : _queryTimeout;
        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var response = await httpClient.SendAsync(request, linkedCts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
            var parsed = await JsonSerializer.DeserializeAsync<SparqlResponse>(stream, JsonOptions, linkedCts.Token);

            return ParseBindings(parsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Timeout — treated as "no match," never surfaced as a failure,
            // UNLESS throwOnTimeout (REQ-211, 2026-07-27 fix): see this
            // method's own throwOnTimeout parameter doc comment (on the
            // interface) for the full reasoning. REQ-103: for every other
            // caller, this falls through to the API-Football fallback
            // (Tier 1) or the combination is discarded (REQ-101), same as a
            // genuine miss. The added `!cancellationToken.IsCancellationRequested`
            // guard (matching QueryPlayerPoolBirthYearAsync's own pattern
            // below) makes sure a genuine caller-initiated cancellation
            // (e.g. request aborted) is never mistaken for this client's own
            // timeout and propagates as an ordinary OperationCanceledException
            // instead, regardless of throwOnTimeout.
            if (throwOnTimeout)
            {
                throw new WikidataQueryException(
                    $"Wikidata {queryKind} intersection query for {qidA}/{qidB} timed out after {effectiveTimeout.TotalSeconds:0}s.");
            }

            // Observability fix (2026-07-27): this branch previously logged
            // nothing at all, unlike the HTTP/parse-error branch just below —
            // warm-player-cache's own aggregate summary ("N queried live")
            // couldn't distinguish "queried live and found nothing" from
            // "queried live and silently timed out," making its per-pair
            // outcome undiagnosable from the log alone. Same level/shape as
            // the HTTP-error branch's warning, just without an exception to
            // attach (a timeout is expected/swallowed here, not exceptional).
            _logger.LogWarning(
                "Wikidata {QueryKind} SPARQL query timed out after {TimeoutSeconds:0}s for {QidA}/{QidB}; treating as no match.",
                queryKind, _queryTimeout.TotalSeconds, qidA, qidB);

            return [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // Deliberately still returns [] rather than throwing (REQ-103's
            // "never block grid generation on a Wikidata failure" contract),
            // but a non-success status or unparseable body is just as likely
            // to be a bad SPARQL query (a real bug) as a transient WDQS
            // outage — log so that distinction is visible during development
            // instead of silently looking identical to a genuine no-match.
            _logger.LogWarning(ex,
                "Wikidata {QueryKind} SPARQL query failed for {QidA}/{QidB}; treating as no match.",
                queryKind, qidA, qidB);
            return [];
        }
    }

    // Shared shape between the two intersection query builders below —
    // extracted after REQ-214's P18 addition had to be hand-duplicated into
    // both (flagged in quality-gate review as exactly the kind of place
    // that's easy to silently diverge on next time). `candidateClauses` is
    // the one thing that actually differs per builder: which
    // country/club(s) a player must match. Everything else — the shared
    // predicates and the OPTIONAL/SERVICE footer — lives here so a future
    // addition (another OPTIONAL property, say) can only land once.
    //
    // No LIMIT — non-negotiable, see implementation-document.md §6a: the
    // result set IS the cell's complete answer key. Fetches skos:altLabel
    // in the same query so aliases cost nothing extra (REQ-208's alias
    // value, free). P106 = occupation (association football player),
    // P21 = sex or gender, P569 = date of birth (ADR-0025's male-only/
    // born-1939-or-later player pool restriction — REQ-112), P18 = image
    // (REQ-214's photo reveal — OPTIONAL, same as alias, so a player with
    // no photo still matches the rest of the query instead of being
    // dropped). P106/P21/P569 stay truthy (wdt:) on purpose: for those,
    // best-rank semantics match product intent (current citizenship, the
    // best-supported date of birth) — see each caller's own comment for why
    // P54 (club membership) can't use the same truthy shortcut.
    //
    // ADR-0042/S-079: ?startTime/?endTime/?numberOfMatches are P580/P582/
    // P1350 — qualifiers on the ?clubStatement variable, OPTIONAL so a
    // player still matches the rest of the query when they're absent (same
    // reasoning as alias/photo above). This is the one shared footer for
    // every candidateClauses builder below, so it only "just works" for the
    // three builders whose candidateClauses bind a club membership
    // statement to that exact variable name (BuildCountryClubIntersectionQuery,
    // BuildNationalTeamClubIntersectionQuery, BuildTrophyClubIntersectionQuery)
    // — BuildClubClubIntersectionQuery uses two distinctly-named statement
    // variables (?clubAStatement/?clubBStatement) and
    // BuildTrophyCountryIntersectionQuery has no P54 clause at all, so
    // ?clubStatement simply never binds for either and these three
    // qualifiers are silently absent from their results — not a bug, just
    // an empty binding (see WikidataLookupService's own scope note for why
    // only the country/nationality x club path persists this data as of
    // S-079).
    //
    // REQ-1207/S-082: ?position is P413 ("position played on team /
    // speciality") — OPTIONAL, same reasoning as ?photo above, so a player
    // with no P413 statement still matches the rest of the query. ?dateOfBirth
    // is NOT a new binding — the WHERE clause already required it (ADR-0025's
    // pool filter, the FILTER line below); it just wasn't previously listed
    // in the SELECT projection, so ParseBindings never saw it. Adding it to
    // SELECT is the only change needed to make Player.BirthYear derivable —
    // no new triple pattern, no new round-trip, per REQ-1207's "no new
    // binding added to the query for this field at all."
    private static string BuildIntersectionQuery(string candidateClauses) => $$"""
        SELECT ?player ?playerLabel ?alias ?photo ?position ?dateOfBirth ?startTime ?endTime ?numberOfMatches WHERE {
          ?player wdt:P106 wd:Q937857.
        {{candidateClauses}}
          ?player wdt:P21 wd:{{MaleWikidataQid}}.
          ?player wdt:P569 ?dateOfBirth.
          FILTER(?dateOfBirth >= "{{DateOfBirthCutoff}}"^^xsd:dateTime)
          OPTIONAL {
            ?player skos:altLabel ?alias.
            FILTER(LANG(?alias) = "en")
          }
          OPTIONAL { ?player wdt:P18 ?photo. }
          OPTIONAL { ?player wdt:P413 ?position. }
          OPTIONAL { ?clubStatement pq:P580 ?startTime. }
          OPTIONAL { ?clubStatement pq:P582 ?endTime. }
          OPTIONAL { ?clubStatement pq:P1350 ?numberOfMatches. }
          SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
        }
        """;

    // P54 deliberately uses the full statement path (p:P54/ps:P54,
    // excluding only deprecated rank), NOT the truthy wdt:P54 shortcut
    // BuildIntersectionQuery's shared predicates use — do not "simplify" it
    // back. Wikidata's truthy wdt: graph contains only best-rank
    // statements: the moment any P54 statement on a player is marked
    // preferred rank (editors routinely mark the *current* club
    // preferred), every normal-rank historical club silently vanishes from
    // wdt:P54. That turned "ever played for this club" into "currently
    // plays for this club" for exactly those players (e.g. Sandro Tonali x
    // AC Milan), leaving the persisted answer key incomplete and correct
    // guesses scored incorrect (REQ-113's ever-played-for semantics,
    // REQ-101/REQ-203's correctness contract). Both grid generation and
    // REQ-211's guess-time live lookup route through both builders below,
    // so the statement path covers both.
    private static string BuildCountryClubIntersectionQuery(string countryQid, string clubQid) =>
        BuildIntersectionQuery($$"""
              ?player wdt:P27 wd:{{countryQid}}.
              ?player p:P54 ?clubStatement.
              ?clubStatement ps:P54 wd:{{clubQid}}.
              MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
            """);

    // REQ-114/ADR-0035: England/Scotland/Wales/Northern Ireland aren't
    // sovereign states, so P27 ("country of citizenship") can't distinguish
    // them — every English/Scottish/Welsh/Northern Irish player's P27 is
    // uniformly United Kingdom (Q145). P1532 ("country for sport") is
    // Wikidata's own property for "country represented in competition,"
    // which is exactly what a football trivia game means by "England."
    // Deliberately uses the truthy wdt:P1532 shortcut, unlike P54's full
    // statement path above — P1532 doesn't have P54's "current club" rank-
    // hiding problem: there's no Wikidata editorial convention of marking
    // one P1532 statement "preferred rank" to mean "the country they
    // currently represent" the way editors routinely do for a player's
    // *current* club on P54 (see BuildCountryClubIntersectionQuery's own
    // comment for that incident). A player either represented a given
    // national team or they didn't — best-rank semantics and "represented
    // this country at all" coincide here, the same reasoning
    // BuildTrophyCountryIntersectionQuery's comment gives for P166's truthy
    // shortcut. Same P54 full-statement-path club-membership half as every
    // other club-involving query in this file — do not "simplify" that half
    // to wdt:P54.
    private static string BuildNationalTeamClubIntersectionQuery(string nationalTeamQid, string clubQid) =>
        BuildIntersectionQuery($$"""
              ?player wdt:P1532 wd:{{nationalTeamQid}}.
              ?player p:P54 ?clubStatement.
              ?clubStatement ps:P54 wd:{{clubQid}}.
              MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
            """);

    // S-030: "ever played for both clubs" — P54 checked twice instead of
    // once against P27, same full-statement-path-not-truthy P54 rule as
    // BuildCountryClubIntersectionQuery above (see its comment for why
    // wdt:P54 is wrong here). Two distinct statement variables, one per
    // club — a single shared variable could never bind (one statement
    // can't point at two clubs).
    private static string BuildClubClubIntersectionQuery(string clubAQid, string clubBQid) =>
        BuildIntersectionQuery($$"""
              ?player p:P54 ?clubAStatement.
              ?clubAStatement ps:P54 wd:{{clubAQid}}.
              MINUS { ?clubAStatement wikibase:rank wikibase:DeprecatedRank. }
              ?player p:P54 ?clubBStatement.
              ?clubBStatement ps:P54 wd:{{clubBQid}}.
              MINUS { ?clubBStatement wikibase:rank wikibase:DeprecatedRank. }
            """);

    // S-031/REQ-108: P166 ("award received") — deliberately uses the truthy
    // wdt:P166 shortcut, unlike P54 above. This is a real judgment call, not
    // a reflexive "truthy is simpler": P54's truthy shortcut is unsafe
    // specifically because Wikidata editors routinely mark a player's
    // *current* club statement preferred rank, which silently drops every
    // normal-rank historical club from the best-rank-only wdt: graph (see
    // BuildCountryClubIntersectionQuery's own comment for the Sandro Tonali
    // incident this pins down). A repeatable individual award like Ballon
    // d'Or has no equivalent editorial convention — there's no "this win
    // supersedes that win" preferred-rank practice on P166 statements the
    // way there is for "this is my current club" on P54 — so best-rank
    // semantics and "received this award at all" coincide here, and truthy
    // is safe. If a future trophy turns out to have its own rank quirk,
    // this reasoning (and the truthy shortcut) needs re-checking per-trophy,
    // not assumed to hold universally just because it holds for Ballon d'Or.
    private static string BuildTrophyCountryIntersectionQuery(string trophyQid, string countryQid) =>
        BuildIntersectionQuery($$"""
              ?player wdt:P166 wd:{{trophyQid}}.
              ?player wdt:P27 wd:{{countryQid}}.
            """);

    // S-031/REQ-108: P166 (truthy, see BuildTrophyCountryIntersectionQuery's
    // comment) + P54 (full statement path, excluding only deprecated rank —
    // the same non-negotiable "ever played for," not "currently plays for,"
    // reasoning as every other P54 use in this file). Do not "simplify" the
    // P54 half to wdt:P54.
    private static string BuildTrophyClubIntersectionQuery(string trophyQid, string clubQid) =>
        BuildIntersectionQuery($$"""
              ?player wdt:P166 wd:{{trophyQid}}.
              ?player p:P54 ?clubStatement.
              ?clubStatement ps:P54 wd:{{clubQid}}.
              MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
            """);

    public async Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolBirthYearAsync(
        int birthYear, CancellationToken cancellationToken = default)
    {
        if (birthYear < FirstEligibleBirthYear)
            throw new ArgumentOutOfRangeException(nameof(birthYear), birthYear,
                $"birthYear must be {FirstEligibleBirthYear} or later (ADR-0025's player-pool floor).");

        var query = BuildPlayerPoolBirthYearQuery(birthYear);
        var requestUri = $"sparql?query={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

        using var timeoutCts = new CancellationTokenSource(_queryTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // Unlike every other public method on this client, this one THROWS
        // (WikidataQueryException) on timeout/HTTP/parse failure instead of
        // returning [] — an empty list from this method means exactly "no
        // eligible players born this year" (a real thing for sparse early
        // years), never "the query failed." The original swallow-to-[]
        // contract made a WDQS timeout indistinguishable from end-of-data,
        // and the import-player-name-index job exited 0 having imported
        // nothing (NOTES.md 2026-07-18). The intersection queries above keep
        // their never-throw contract untouched — REQ-103's "never block grid
        // generation on a Wikidata failure" depends on it; this bulk-import
        // method has the opposite requirement (fail loudly, re-run the job).
        try
        {
            using var response = await httpClient.SendAsync(request, linkedCts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
            var parsed = await JsonSerializer.DeserializeAsync<SparqlResponse>(stream, JsonOptions, linkedCts.Token);

            return ParseNameIndexBindings(parsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new WikidataQueryException(
                $"Wikidata player-pool query for birth year {birthYear} timed out after {_queryTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new WikidataQueryException(
                $"Wikidata player-pool query for birth year {birthYear} failed: {ex.Message}", ex);
        }
    }

    // S-032/ADR-0007's broad "association football player" pool query, no
    // country/club filter — revised 2026-07-18: sliced by BIRTH YEAR, not
    // paged by LIMIT/OFFSET. The original shape (inner
    // `SELECT DISTINCT ?player ... ORDER BY ?player LIMIT n OFFSET m`)
    // forced WDQS to materialize and sort the ENTIRE unfiltered pool
    // (hundreds of thousands of items) on every page request, which blew
    // WDQS's hard ~60s server-side timeout on every single page — no
    // client-side timeout can raise that cap, so every run imported zero
    // rows (NOTES.md 2026-07-18). A one-year P569 window instead bounds each
    // query to a few thousand rows with NO ORDER BY, OFFSET, LIMIT, or inner
    // subquery — the same size/shape class as the intersection queries that
    // already work in production. Same male-only filter and 1939 floor as
    // the intersection queries (ADR-0025/REQ-112). A player with two P569
    // statements in different years appears in two slices; the importer's
    // deterministic-PlayerId upsert dedups that (see PlayerNameIndexImporter).
    // A player with more than one P27 citizenship produces more than one
    // result row for the same ?player — ParseNameIndexBindings groups by
    // qid, taking the first non-null value seen. Deliberately no P54 (club)
    // — that's PlayerAttribute's job, not this index's (ADR-0007) — and,
    // since 2026-07-18, no P18 (photo) either: the autocomplete contract
    // never exposes photos (design-document.md's SCREEN-02 note), so
    // fetching P18 was pure join/row cost for a column nothing read.
    private static string BuildPlayerPoolBirthYearQuery(int birthYear) => $$"""
        SELECT ?player ?playerLabel ?birthYear ?countryLabel WHERE {
          ?player wdt:P106 wd:Q937857.
          ?player wdt:P21 wd:{{MaleWikidataQid}}.
          ?player wdt:P569 ?dateOfBirth.
          FILTER(?dateOfBirth >= "{{birthYear}}-01-01T00:00:00Z"^^xsd:dateTime && ?dateOfBirth < "{{birthYear + 1}}-01-01T00:00:00Z"^^xsd:dateTime)
          BIND(YEAR(?dateOfBirth) AS ?birthYear)
          OPTIONAL { ?player wdt:P27 ?country. }
          SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
        }
        """;

    private static IReadOnlyList<WikidataNameIndexEntry> ParseNameIndexBindings(SparqlResponse? response)
    {
        if (response?.Results?.Bindings is null)
            return [];

        var byQid = new Dictionary<string, (string FullName, int? BirthYear, string? Nationality)>();

        foreach (var binding in response.Results.Bindings)
        {
            if (!binding.TryGetValue("player", out var playerValue) || string.IsNullOrEmpty(playerValue.Value))
                continue;

            var qid = playerValue.Value.Split('/').Last();

            if (!byQid.TryGetValue(qid, out var entry))
            {
                var label = binding.TryGetValue("playerLabel", out var labelValue) ? labelValue.Value : qid;
                int? birthYear = binding.TryGetValue("birthYear", out var birthYearValue)
                    && int.TryParse(birthYearValue.Value, out var parsedBirthYear)
                        ? parsedBirthYear
                        : null;
                entry = (label, birthYear, null);
            }

            // A player with more than one citizenship produces more than one
            // binding row — keep the first non-null value seen, rather than
            // overwriting with a later (possibly blank) one.
            if (entry.Nationality is null && binding.TryGetValue("countryLabel", out var countryValue)
                && !string.IsNullOrWhiteSpace(countryValue.Value))
                entry.Nationality = countryValue.Value;

            byQid[qid] = entry;
        }

        return byQid
            .Select(kv => new WikidataNameIndexEntry(kv.Key, kv.Value.FullName, kv.Value.BirthYear, kv.Value.Nationality))
            .ToList();
    }

    // REQ-214 backfill (S-045): batched, direct-by-QID photo lookup — see
    // IWikidataClient's own doc comment for why this is a different query
    // shape from the intersection queries above and why its error contract
    // (throw, not swallow-to-empty) matches QueryPlayerPoolBirthYearAsync
    // rather than them.
    public async Task<IReadOnlyDictionary<string, string>> QueryPlayerPhotosByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default)
    {
        if (wikidataQids.Count == 0)
            return new Dictionary<string, string>();

        foreach (var qid in wikidataQids)
        {
            if (!WikidataQid.IsValid(qid))
                throw new ArgumentException($"Not a valid Wikidata QID: '{qid}'", nameof(wikidataQids));
        }

        var query = BuildPlayerPhotosByQidsQuery(wikidataQids);
        var requestUri = $"sparql?query={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

        using var timeoutCts = new CancellationTokenSource(_queryTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // Same throw-on-failure contract as QueryPlayerPoolBirthYearAsync
        // (see that method's own comment) — the opposite of the
        // intersection queries' swallow-to-[] contract, deliberately: this
        // is a batch job whose success metric is a backfilled-row count, so
        // a swallowed failure would be indistinguishable from "none of
        // these QIDs have a photo."
        try
        {
            using var response = await httpClient.SendAsync(request, linkedCts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
            var parsed = await JsonSerializer.DeserializeAsync<SparqlResponse>(stream, JsonOptions, linkedCts.Token);

            return ParsePhotoBindings(parsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new WikidataQueryException(
                $"Wikidata player-photo batch query for {wikidataQids.Count} QID(s) timed out after {_queryTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new WikidataQueryException(
                $"Wikidata player-photo batch query for {wikidataQids.Count} QID(s) failed: {ex.Message}", ex);
        }
    }

    // A VALUES clause over the batch, not a candidate-matching pattern —
    // deliberately no male/date-of-birth/occupation filter here, unlike
    // every other query in this file: every QID in the batch is already a
    // real Player row this codebase itself created via the intersection
    // queries (which DID apply those filters at the time), so re-filtering
    // here would only risk a false negative if Wikidata's own P21/P569 data
    // changed since. No LIMIT/ORDER BY/OFFSET, same bounded-query
    // discipline as every other query here — the caller
    // (PlayerPhotoBackfillService) is responsible for keeping each batch
    // small (BatchSize = 200), not this method.
    private static string BuildPlayerPhotosByQidsQuery(IReadOnlyList<string> qids)
    {
        var valuesClause = string.Join(" ", qids.Select(qid => $"wd:{qid}"));
        return $$"""
            SELECT ?player ?photo WHERE {
              VALUES ?player { {{valuesClause}} }
              OPTIONAL { ?player wdt:P18 ?photo. }
            }
            """;
    }

    // Keyed by QID (not grouped/deduped the way ParseBindings's byQid
    // dictionary is) — VALUES + OPTIONAL yields exactly one row per QID in
    // the batch regardless of match, so there is no multi-row-per-player
    // grouping concern here the way there is for the intersection queries'
    // alias fetch. Still takes the first non-null value seen per QID (same
    // defensive shape as ParseBindings/ParseNameIndexBindings) in case a
    // player somehow has more than one P18 statement. A QID with no "photo"
    // binding (no P18 statement) is simply absent from the result — never
    // an error, never a placeholder entry.
    private static IReadOnlyDictionary<string, string> ParsePhotoBindings(SparqlResponse? response)
    {
        var photoUrlsByQid = new Dictionary<string, string>();
        if (response?.Results?.Bindings is null)
            return photoUrlsByQid;

        foreach (var binding in response.Results.Bindings)
        {
            if (!binding.TryGetValue("player", out var playerValue) || string.IsNullOrEmpty(playerValue.Value))
                continue;
            if (!binding.TryGetValue("photo", out var photoValue) || string.IsNullOrWhiteSpace(photoValue.Value))
                continue;

            var qid = playerValue.Value.Split('/').Last();
            photoUrlsByQid.TryAdd(qid, photoValue.Value);
        }

        return photoUrlsByQid;
    }

    private static IReadOnlyList<WikidataPlayerMatch> ParseBindings(SparqlResponse? response)
    {
        if (response?.Results?.Bindings is null)
            return [];

        var byQid = new Dictionary<string, (string FullName, HashSet<string> Aliases, string? PhotoUrl, string? Position, int? BirthYear, HashSet<CareerStintQualifiers> CareerStints)>();

        foreach (var binding in response.Results.Bindings)
        {
            if (!binding.TryGetValue("player", out var playerValue) || string.IsNullOrEmpty(playerValue.Value))
                continue;

            var qid = playerValue.Value.Split('/').Last();

            if (!byQid.TryGetValue(qid, out var entry))
            {
                var label = binding.TryGetValue("playerLabel", out var labelValue) ? labelValue.Value : qid;
                entry = (label, [], null, null, null, []);
            }

            if (binding.TryGetValue("alias", out var aliasValue) && !string.IsNullOrWhiteSpace(aliasValue.Value))
                entry.Aliases.Add(aliasValue.Value);

            // REQ-214: one row can carry the photo binding while a different
            // row (for the same player, joined against a different alias)
            // does not — OPTIONAL joins independently, same reasoning as
            // ParseNameIndexBindings' "keep the first non-null value seen"
            // comment. wdt:P18 is single-valued in practice for a Wikidata
            // person item, so "first non-null" is not a lossy simplification
            // here the way it can be for a genuinely multi-valued property.
            if (entry.PhotoUrl is null && binding.TryGetValue("photo", out var photoValue)
                && !string.IsNullOrWhiteSpace(photoValue.Value))
                entry.PhotoUrl = photoValue.Value;

            // REQ-1207/S-082: same "first non-null value seen" shape as
            // PhotoUrl above — wdt:P413 is effectively single-valued in
            // practice for a Wikidata person item.
            if (entry.Position is null && binding.TryGetValue("position", out var positionValue)
                && !string.IsNullOrWhiteSpace(positionValue.Value))
                entry.Position = positionValue.Value;

            // REQ-1207/S-082: ?dateOfBirth is bound on every row for this
            // player (it's a mandatory, non-OPTIONAL match — ADR-0025's pool
            // filter), so every row should agree; "first non-null seen" is
            // still the defensive shape used throughout this method.
            if (entry.BirthYear is null && binding.TryGetValue("dateOfBirth", out var dateOfBirthValue)
                && !string.IsNullOrWhiteSpace(dateOfBirthValue.Value)
                && TryParseXsdDateTimeYear(dateOfBirthValue.Value, out var birthYear))
                entry.BirthYear = birthYear;

            // ADR-0042/S-079: SPARQL's OPTIONAL semantics mean a player with
            // N aliases and M distinct qualifier combinations can produce up
            // to N×M result rows — dedupe qualifier tuples per player via
            // the HashSet the same way Aliases is deduped above (records get
            // structural equality for free). Only recorded when startTime is
            // actually bound: a row where all three qualifiers are unbound
            // carries zero information, and PlayerCareerStint.StartYear is
            // non-nullable, so there is nothing valid to write.
            if (binding.TryGetValue("startTime", out var startTimeValue) && TryParseXsdDateTimeYear(startTimeValue.Value, out var startYear))
            {
                int? endYear = binding.TryGetValue("endTime", out var endTimeValue)
                    && TryParseXsdDateTimeYear(endTimeValue.Value, out var parsedEndYear)
                        ? parsedEndYear
                        : null;
                int? appearanceCount = binding.TryGetValue("numberOfMatches", out var numberOfMatchesValue)
                    && int.TryParse(numberOfMatchesValue.Value, out var parsedAppearanceCount)
                        ? parsedAppearanceCount
                        : null;

                entry.CareerStints.Add(new CareerStintQualifiers(startYear, endYear, appearanceCount));
            }

            byQid[qid] = entry;
        }

        return byQid
            .Select(kv => new WikidataPlayerMatch(
                kv.Key, kv.Value.FullName, kv.Value.Aliases.ToList(), kv.Value.PhotoUrl, kv.Value.CareerStints.ToList())
            {
                Position = kv.Value.Position,
                BirthYear = kv.Value.BirthYear,
            })
            .ToList();
    }

    // ADR-0042/S-079: Wikidata's P580/P582 qualifiers come back as full
    // xsd:dateTime strings (e.g. "2015-07-01T00:00:00Z") — REQ-1201-
    // REQ-1206 only needs the year for chronological ordering and a
    // displayable year range, never month/day precision, so this takes just
    // the leading 4 digits rather than parsing the full timestamp.
    private static bool TryParseXsdDateTimeYear(string xsdDateTime, out int year) =>
        int.TryParse(xsdDateTime.AsSpan(0, Math.Min(4, xsdDateTime.Length)), out year);

    private sealed record SparqlResponse([property: JsonPropertyName("results")] SparqlResults? Results);

    private sealed record SparqlResults([property: JsonPropertyName("bindings")] List<Dictionary<string, SparqlValue>>? Bindings);

    private sealed record SparqlValue([property: JsonPropertyName("value")] string Value);
}

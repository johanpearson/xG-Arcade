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
    ILogger<WikidataClient>? logger = null,
    TimeSpan? cacheWarmingQueryTimeout = null) : IWikidataClient
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

    // Bug fix (2026-08-02, bug-bundle, REQ-1203): Wikidata models national
    // team caps under the same P54 ("member of sports team") property as
    // club membership — a national team like "Switzerland men's national
    // football team" is itself a Wikidata item, instance-of (transitively,
    // via P279 subclass chains such as "men's national association football
    // team") this Q6979593 "national association football team" class.
    // BuildPlayerCareerStintsByQidsQuery excludes any ?club matching this
    // class so xG Path's career-stint fetch never surfaces a national team
    // as a "club" — REQ-1203's own acceptance criteria are explicit that
    // "national team caps/appearances are never revealed as a clue for this
    // game." Confirmed via Wikidata's own item page, not training-knowledge
    // recall — see this file's PR description for the verification.
    private const string NationalTeamClassWikidataQid = "Q6979593";

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

    // REQ-110 (2026-07-28 "cache-warming-specific timeout" extension): a
    // third, cache-warming-only budget — see WikidataQueryTimeoutTier's own
    // doc comment for how a caller selects it (timeoutTier, not
    // throwOnTimeout — cache warming keeps throwOnTimeout: false, its
    // fail-open/swallow contract is unchanged). Selected, not just "longer
    // than 28s because nobody's waiting": ADR-0011's own evidence (WDQS
    // queries observed 9-27s under load) is the same evidence
    // _guessTimeFallbackQueryTimeout's 28s already leans on, with only ~1s
    // of margin over the 27s worst case observed there — tight, because a
    // real player IS waiting on that path. Cache warming has no such
    // deadline (ADR-0024: a CLI verb inside a 90-minute GitHub Actions job,
    // not a request anyone is blocked on), so 45s gives a comfortably wider
    // margin above that same 9-27s range — enough that a query landing at
    // the slow end of ADR-0011's observed range, or running slightly slower
    // than guess-time's single-shot queries under this job's own sequential
    // sweep of every reference pair, still gets a real answer instead of a
    // timeout — while staying bounded enough that a genuinely hung query
    // doesn't dominate a single pair's budget (this timeout, times up to two
    // attempts under REQ-110's own same-run retry, is still a small fraction
    // of the workflow's 90-minute ceiling even for the worst pair). Same
    // overridable-for-tests shape as the two timeouts above.
    private readonly TimeSpan _cacheWarmingQueryTimeout = cacheWarmingQueryTimeout ?? TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
        string countryWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default)
    {
        if (!WikidataQid.IsValid(countryWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{countryWikidataQid}'", nameof(countryWikidataQid));
        if (!WikidataQid.IsValid(clubWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{clubWikidataQid}'", nameof(clubWikidataQid));

        var query = BuildCountryClubIntersectionQuery(countryWikidataQid, clubWikidataQid);
        return await RunIntersectionQueryAsync("country-club", countryWikidataQid, clubWikidataQid, query, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);
    }

    // REQ-114/ADR-0035: England/Scotland/Wales/Northern Ireland's P1532
    // counterpart of QueryCountryClubIntersectionAsync above.
    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryNationalTeamClubIntersectionAsync(
        string nationalTeamWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default)
    {
        if (!WikidataQid.IsValid(nationalTeamWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{nationalTeamWikidataQid}'", nameof(nationalTeamWikidataQid));
        if (!WikidataQid.IsValid(clubWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{clubWikidataQid}'", nameof(clubWikidataQid));

        var query = BuildNationalTeamClubIntersectionQuery(nationalTeamWikidataQid, clubWikidataQid);
        return await RunIntersectionQueryAsync("national-team-club", nationalTeamWikidataQid, clubWikidataQid, query, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);
    }

    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryClubClubIntersectionAsync(
        string clubAWikidataQid,
        string clubBWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default)
    {
        if (!WikidataQid.IsValid(clubAWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{clubAWikidataQid}'", nameof(clubAWikidataQid));
        if (!WikidataQid.IsValid(clubBWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{clubBWikidataQid}'", nameof(clubBWikidataQid));

        var query = BuildClubClubIntersectionQuery(clubAWikidataQid, clubBWikidataQid);
        return await RunIntersectionQueryAsync("club-club", clubAWikidataQid, clubBWikidataQid, query, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);
    }

    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyCountryIntersectionAsync(
        string trophyWikidataQid,
        string countryWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default)
    {
        if (!WikidataQid.IsValid(trophyWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{trophyWikidataQid}'", nameof(trophyWikidataQid));
        if (!WikidataQid.IsValid(countryWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{countryWikidataQid}'", nameof(countryWikidataQid));

        var query = BuildTrophyCountryIntersectionQuery(trophyWikidataQid, countryWikidataQid);
        return await RunIntersectionQueryAsync("trophy-country", trophyWikidataQid, countryWikidataQid, query, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);
    }

    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyClubIntersectionAsync(
        string trophyWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default)
    {
        if (!WikidataQid.IsValid(trophyWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{trophyWikidataQid}'", nameof(trophyWikidataQid));
        if (!WikidataQid.IsValid(clubWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{clubWikidataQid}'", nameof(clubWikidataQid));

        var query = BuildTrophyClubIntersectionQuery(trophyWikidataQid, clubWikidataQid);
        return await RunIntersectionQueryAsync("trophy-club", trophyWikidataQid, clubWikidataQid, query, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);
    }

    private async Task<IReadOnlyList<WikidataPlayerMatch>> RunIntersectionQueryAsync(
        string queryKind, string qidA, string qidB, string query, bool throwOnTimeout, CancellationToken cancellationToken,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default)
    {
        var requestUri = $"sparql?query={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

        // REQ-110 (2026-07-28 "cache-warming-specific timeout" extension):
        // timeoutTier now makes this decision explicit rather than
        // overloading throwOnTimeout for a third tier (see
        // WikidataQueryTimeoutTier's own doc comment for the full history).
        // Default preserves the original ADR-0046-era resolution exactly —
        // throwOnTimeout doubles as the budget selector for every existing
        // caller (REQ-103/Sync gets _queryTimeout, REQ-211/GuessTimeFallback
        // gets _guessTimeFallbackQueryTimeout) — so neither of those two
        // callers' behavior changes. Only PlayerCacheWarmingService (via
        // WikidataLookupService) passes CacheWarming explicitly, always
        // alongside throwOnTimeout: false.
        var effectiveTimeout = timeoutTier switch
        {
            WikidataQueryTimeoutTier.CacheWarming => _cacheWarmingQueryTimeout,
            _ => throwOnTimeout ? _guessTimeFallbackQueryTimeout : _queryTimeout,
        };
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
            // outcome undiagnosable from the log alone. Logs effectiveTimeout
            // (2026-07-28 fix), not the old hardcoded _queryTimeout — since
            // timeoutTier can now select a different budget
            // (WikidataQueryTimeoutTier.CacheWarming), the old constant would
            // have logged a misleading "timed out after 15s" for a query
            // that actually ran the full 45s cache-warming budget before
            // giving up.
            //
            // Log level fix (2026-08-01, ADR-0052): Debug, not Warning — a
            // cache-warming run against a few hundred pairs can log this
            // once per failing pair, and at Warning that reliably drowned
            // the one line that actually matters (WarmAsync's own
            // Information-level run summary, which already reports the
            // technical-failure count and names every failing pair) under
            // thousands of lines of per-pair noise. Debug is filtered out
            // by this project's default "Information" log level
            // (appsettings.json), so a normal run's console output stays
            // readable; set Logging:LogLevel:Default to Debug to see these
            // again when actually troubleshooting a specific pair.
            _logger.LogDebug(
                "Wikidata {QueryKind} SPARQL query timed out after {TimeoutSeconds:0}s for {QidA}/{QidB}; treating as no match.",
                queryKind, effectiveTimeout.TotalSeconds, qidA, qidB);

            // REQ-110 (2026-07-28): a genuine technical failure, distinct
            // from a successful-but-empty response — see onTechnicalFailure's
            // own doc comment on IWikidataClient for why this is a callback
            // rather than a return-type change.
            onTechnicalFailure?.Invoke();
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
            //
            // Log level fix (2026-08-01, ADR-0052): Debug, not Warning — see
            // the timeout branch's own comment above for the full
            // "why this drowned out the run summary" reasoning. This
            // branch's exception (a JSON parse failure in particular can be
            // a 15-20 line stack trace) was the single biggest contributor
            // to unreadable cache-warming logs; still attached to the Debug
            // call so it's there when Debug is turned on to investigate.
            _logger.LogDebug(ex,
                "Wikidata {QueryKind} SPARQL query failed for {QidA}/{QidB}; treating as no match.",
                queryKind, qidA, qidB);

            // REQ-110 (2026-07-28): same technical-failure signal as the
            // timeout branch above.
            onTechnicalFailure?.Invoke();
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
    //
    // Bug fix (2026-08-02, bug-bundle): ?positionLabel, not ?position, is
    // what actually gets projected into Player.Position (see ParseBindings
    // below) — ?position alone is the raw P413 object, a bare entity URI
    // (e.g. "http://www.wikidata.org/entity/Q336286"), never a human-readable
    // string. The SERVICE wikibase:label block below was already resolving
    // ?playerLabel/?clubLabel this whole time; ?positionLabel only needed to
    // be added to the SELECT list to make the same auto-label join happen for
    // ?position too — no new triple pattern. Real xG Path play surfaced this:
    // the position clue rendered the literal QID URI instead of a position
    // name.
    private static string BuildIntersectionQuery(string candidateClauses) => $$"""
        SELECT ?player ?playerLabel ?alias ?photo ?positionLabel ?dateOfBirth ?startTime ?endTime ?numberOfMatches WHERE {
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
    //
    // 2026-08-01 fix (ADR-0052): each club's match is wrapped in its own
    // FILTER EXISTS block instead of a plain join. A plain join binds
    // ?clubAStatement/?clubBStatement in the outer pattern, so a player
    // with multiple non-deprecated P54 statements at club A (loan spells, a
    // return transfer) times multiple at club B produces one result ROW PER
    // (clubAStatement, clubBStatement) COMBINATION per player — on top of
    // the per-alias multiplication BuildIntersectionQuery's OPTIONAL
    // alt-label fetch already applies. For two clubs with a large,
    // well-documented, historically-overlapping squad this combination
    // produced a real 250,000+ row WDQS response that neither WDQS nor this
    // client's JSON parser could finish inside any reasonable timeout, and
    // the same doomed pair got re-attempted on every future
    // warm-player-cache run since nothing persisted its failure (see
    // PairLookupFailure, ADR-0052, for that half of the fix). FILTER EXISTS
    // checks "does at least one qualifying statement exist" without binding
    // ?clubAStatement/?clubBStatement in the outer pattern, so neither
    // club's statement count can multiply rows — the result is exactly one
    // row per matching player before the still-intentional per-alias
    // multiplication. This is safe specifically because club-club never
    // reads the shared footer's per-statement qualifiers (?clubStatement,
    // singular — a different variable, never bound by this builder either
    // way, see BuildIntersectionQuery's own qualifier comment); a builder
    // that DOES need those qualifiers (country-club, national-team-club,
    // trophy-club) cannot use this same trick without losing them. Never
    // simplify this back to a plain join.
    private static string BuildClubClubIntersectionQuery(string clubAQid, string clubBQid) =>
        BuildIntersectionQuery($$"""
              FILTER EXISTS {
                ?player p:P54 ?clubAStatement.
                ?clubAStatement ps:P54 wd:{{clubAQid}}.
                MINUS { ?clubAStatement wikibase:rank wikibase:DeprecatedRank. }
              }
              FILTER EXISTS {
                ?player p:P54 ?clubBStatement.
                ?clubBStatement ps:P54 wd:{{clubBQid}}.
                MINUS { ?clubBStatement wikibase:rank wikibase:DeprecatedRank. }
              }
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

    // ADR-0055: PlayerCareerPrefetchService's per-country pool query — see
    // IWikidataClient's own doc comment for why this is a nationality-scoped
    // sibling to QueryPlayerPoolBirthYearAsync above, not a replacement.
    public async Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByNationalityAsync(
        string nationalityWikidataQid, bool useCountryForSportProperty, CancellationToken cancellationToken = default)
    {
        var query = BuildPlayerPoolByNationalityQuery(nationalityWikidataQid, useCountryForSportProperty);
        var requestUri = $"sparql?query={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

        using var timeoutCts = new CancellationTokenSource(_queryTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // Same throw-on-failure contract as QueryPlayerPoolBirthYearAsync —
        // see this method's own doc comment (IWikidataClient) for why.
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
                $"Wikidata player-pool query for nationality {nationalityWikidataQid} timed out after {_queryTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new WikidataQueryException(
                $"Wikidata player-pool query for nationality {nationalityWikidataQid} failed: {ex.Message}", ex);
        }
    }

    // ADR-0055: the nationality-scoped sibling of BuildPlayerPoolBirthYearQuery
    // — same bounded shape (no ORDER BY/LIMIT/OFFSET), same male/
    // born-1939-or-later filter (ADR-0025/REQ-112, via the shared
    // DateOfBirthCutoff constant this time, rather than a per-year window),
    // filtered by P27 or P1532 instead of sliced by birth year.
    // useCountryForSportProperty selects between them — same flag,
    // same meaning as QueryNationalTeamClubIntersectionAsync/
    // QueryCountryClubIntersectionAsync's own split (ADR-0035). Truthy
    // (wdt:) for both P27 and P1532 — same reasoning
    // BuildNationalTeamClubIntersectionQuery's own comment gives for why
    // P1532 doesn't have P54's "current club" rank-hiding problem, and P27
    // ("citizenship") has no equivalent problem either (a player either
    // holds a citizenship or doesn't; Wikidata has no "current citizenship
    // supersedes past ones" editorial convention the way P54's "current
    // club" does).
    private static string BuildPlayerPoolByNationalityQuery(string nationalityQid, bool useCountryForSportProperty)
    {
        var nationalityProperty = useCountryForSportProperty ? "P1532" : "P27";
        return $$"""
            SELECT ?player ?playerLabel ?birthYear WHERE {
              ?player wdt:P106 wd:Q937857.
              ?player wdt:P21 wd:{{MaleWikidataQid}}.
              ?player wdt:P569 ?dateOfBirth.
              FILTER(?dateOfBirth >= "{{DateOfBirthCutoff}}"^^xsd:dateTime)
              BIND(YEAR(?dateOfBirth) AS ?birthYear)
              ?player wdt:{{nationalityProperty}} wd:{{nationalityQid}}.
              SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
            }
            """;
    }

    // Bug fix (2026-08-03, user-tester report): a real report showed the
    // autocomplete suggestion for Michael Owen (the footballer, actually
    // born 1979) carrying BirthYear 1976. wdt:P569 is a truthy predicate —
    // it already collapses to a single preferred-rank statement whenever
    // Wikidata has one, so this can only happen when an item genuinely
    // carries more than one non-deprecated P569 statement with NEITHER
    // marked preferred (a real, if uncommon, state of Wikidata's own data —
    // e.g. an old/erroneous secondary-sourced date nobody has cleaned up).
    // QueryPlayerPoolByNationalityAsync's query has no per-year window, so
    // both statements land as separate rows for the same ?player in ONE
    // response; before this fix, whichever row happened to come first in
    // WDQS's own (unspecified, engine-internal) result order silently won,
    // with no correctness signal behind that choice at all. See this
    // method's own handling below for the fix.
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

            int? rowBirthYear = binding.TryGetValue("birthYear", out var birthYearValue)
                && int.TryParse(birthYearValue.Value, out var parsedBirthYear)
                    ? parsedBirthYear
                    : null;

            if (!byQid.TryGetValue(qid, out var entry))
            {
                var label = binding.TryGetValue("playerLabel", out var labelValue) ? labelValue.Value : qid;
                entry = (label, rowBirthYear, null);
            }
            else if (entry.BirthYear is not null && rowBirthYear is not null && entry.BirthYear != rowBirthYear)
            {
                // Two rows for the same player disagree on birth year — a
                // genuine ambiguity this query has no way to resolve (see
                // this method's own doc comment above). Rather than keeping
                // whichever value happened to arrive first — an artifact of
                // WDQS's own internal row ordering, not a correctness signal
                // — the birth year is nulled out. Same "omit rather than
                // mislead" convention this codebase already applies
                // elsewhere (e.g. an unknown club appearance count is
                // omitted, never shown as a misleading "0 apps" —
                // PathClubClue's own doc comment). The player's name still
                // surfaces in autocomplete either way; only the (never
                // load-bearing, REQ-207) birth-year hint is dropped.
                entry.BirthYear = null;
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

    // REQ-1207 backfill (bug-bundle fix, 2026-08-02): batched, direct-by-QID
    // position/birth-year lookup — see IWikidataClient's own doc comment for
    // why this is a different query shape from the intersection queries
    // above and why its error contract (throw, not swallow-to-empty) matches
    // QueryPlayerPhotosByQidsAsync rather than them.
    public async Task<IReadOnlyDictionary<string, PlayerPositionBirthYearEntry>> QueryPlayerPositionsAndBirthYearsByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default)
    {
        if (wikidataQids.Count == 0)
            return new Dictionary<string, PlayerPositionBirthYearEntry>();

        foreach (var qid in wikidataQids)
        {
            if (!WikidataQid.IsValid(qid))
                throw new ArgumentException($"Not a valid Wikidata QID: '{qid}'", nameof(wikidataQids));
        }

        var query = BuildPlayerPositionsAndBirthYearsByQidsQuery(wikidataQids);
        var requestUri = $"sparql?query={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

        using var timeoutCts = new CancellationTokenSource(_queryTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // Same throw-on-failure contract as QueryPlayerPhotosByQidsAsync
        // (see that method's own comment) — a swallowed failure here would
        // be indistinguishable from "none of these QIDs have this data."
        try
        {
            using var response = await httpClient.SendAsync(request, linkedCts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
            var parsed = await JsonSerializer.DeserializeAsync<SparqlResponse>(stream, JsonOptions, linkedCts.Token);

            return ParsePositionBirthYearBindings(parsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new WikidataQueryException(
                $"Wikidata player position/birth-year batch query for {wikidataQids.Count} QID(s) timed out after {_queryTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new WikidataQueryException(
                $"Wikidata player position/birth-year batch query for {wikidataQids.Count} QID(s) failed: {ex.Message}", ex);
        }
    }

    // Same "VALUES clause over the batch, no candidate-matching filter" shape
    // as BuildPlayerPhotosByQidsQuery — every QID in the batch is already a
    // real Player row this codebase itself created via the intersection
    // queries (which DID apply the male/date-of-birth/occupation filters at
    // the time), so re-filtering here would only risk a false negative if
    // Wikidata's own data changed since. No LIMIT/ORDER BY/OFFSET, same
    // bounded-query discipline as every other query here — the caller
    // (PlayerPositionBirthYearBackfillService) is responsible for keeping
    // each batch small, not this method.
    // Bug fix (2026-08-02, bug-bundle): this query had no SERVICE
    // wikibase:label block at all — ?position was projected straight into
    // Player.Position as the raw P413 entity URI, never resolved to a
    // human-readable name (the same class of bug BuildIntersectionQuery's
    // own comment describes, but this backfill query never even had the
    // label service every other query in this file already uses for
    // ?playerLabel/?clubLabel). Adding it here, plus ?positionLabel in the
    // SELECT, is what actually fixes it for every Player row this backfill
    // touches — see ParsePositionBirthYearBindings below for the matching
    // read-side change.
    private static string BuildPlayerPositionsAndBirthYearsByQidsQuery(IReadOnlyList<string> qids)
    {
        var valuesClause = string.Join(" ", qids.Select(qid => $"wd:{qid}"));
        return $$"""
            SELECT ?player ?positionLabel ?dateOfBirth WHERE {
              VALUES ?player { {{valuesClause}} }
              OPTIONAL { ?player wdt:P413 ?position. }
              OPTIONAL { ?player wdt:P569 ?dateOfBirth. }
              SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
            }
            """;
    }

    // Grouped by QID (unlike ParsePhotoBindings' plain "one row per QID"
    // shape) because a player with more than one P413 statement, or more
    // than one P569 statement, can legitimately produce more than one
    // binding row per QID — same "keep the first non-null value seen per
    // field" defensive shape ParseBindings already uses for
    // WikidataPlayerMatch.Position/BirthYear. Only entries where at least
    // one of Position/BirthYear actually resolved are included in the
    // result — a QID with neither is simply absent, never an error, same
    // contract as ParsePhotoBindings.
    private static IReadOnlyDictionary<string, PlayerPositionBirthYearEntry> ParsePositionBirthYearBindings(SparqlResponse? response)
    {
        var entriesByQid = new Dictionary<string, (string? Position, int? BirthYear)>();
        if (response?.Results?.Bindings is null)
            return new Dictionary<string, PlayerPositionBirthYearEntry>();

        foreach (var binding in response.Results.Bindings)
        {
            if (!binding.TryGetValue("player", out var playerValue) || string.IsNullOrEmpty(playerValue.Value))
                continue;

            var qid = playerValue.Value.Split('/').Last();
            (string? Position, int? BirthYear) entry = entriesByQid.TryGetValue(qid, out var existing) ? existing : default;

            // Reads "positionLabel" (bug fix, 2026-08-02) — see
            // BuildPlayerPositionsAndBirthYearsByQidsQuery's own comment.
            if (entry.Position is null && binding.TryGetValue("positionLabel", out var positionValue)
                && !string.IsNullOrWhiteSpace(positionValue.Value))
                entry.Position = positionValue.Value;

            if (entry.BirthYear is null && binding.TryGetValue("dateOfBirth", out var dateOfBirthValue)
                && !string.IsNullOrWhiteSpace(dateOfBirthValue.Value)
                && TryParseXsdDateTimeYear(dateOfBirthValue.Value, out var birthYear))
                entry.BirthYear = birthYear;

            entriesByQid[qid] = entry;
        }

        return entriesByQid
            .Where(kv => kv.Value.Position is not null || kv.Value.BirthYear is not null)
            .ToDictionary(kv => kv.Key, kv => new PlayerPositionBirthYearEntry(kv.Value.Position, kv.Value.BirthYear));
    }

    // ADR-0054: xG Path's own direct career fetch — see IWikidataClient's own
    // doc comment for why this is a different query shape from every other
    // method in this file.
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>>> QueryPlayerCareerStintsByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default)
    {
        if (wikidataQids.Count == 0)
            return new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>();

        foreach (var qid in wikidataQids)
        {
            if (!WikidataQid.IsValid(qid))
                throw new ArgumentException($"Not a valid Wikidata QID: '{qid}'", nameof(wikidataQids));
        }

        var query = BuildPlayerCareerStintsByQidsQuery(wikidataQids);
        var requestUri = $"sparql?query={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

        using var timeoutCts = new CancellationTokenSource(_queryTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // Same throw-on-failure contract as QueryPlayerPhotosByQidsAsync/
        // QueryPlayerPositionsAndBirthYearsByQidsAsync — see IWikidataClient's
        // own doc comment for why.
        try
        {
            using var response = await httpClient.SendAsync(request, linkedCts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
            var parsed = await JsonSerializer.DeserializeAsync<SparqlResponse>(stream, JsonOptions, linkedCts.Token);

            return ParseCareerStintBindings(parsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new WikidataQueryException(
                $"Wikidata player career-stint batch query for {wikidataQids.Count} QID(s) timed out after {_queryTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new WikidataQueryException(
                $"Wikidata player career-stint batch query for {wikidataQids.Count} QID(s) failed: {ex.Message}", ex);
        }
    }

    // Full P54 statement path (p:P54/ps:P54, MINUS deprecated rank), same
    // non-negotiable "ever played for," not "currently plays for" reasoning
    // as every other P54 use in this file — see
    // BuildCountryClubIntersectionQuery's own comment. ?club (not a
    // caller-supplied QID this time) is itself part of the SELECT, with
    // ?clubLabel resolved by the same label service every other builder in
    // this file already uses — this is what makes the query "every club,"
    // not "this one club."
    // Bug fix (2026-08-02, bug-bundle, REQ-1203): excludes national teams
    // from ?club — see NationalTeamClassWikidataQid's own comment for why
    // this is necessary at all (P54 covers both club and international
    // caps) and why the exclusion needs the transitive P279* subclass path,
    // not just a direct P31 check, since a specific national team's P31 is
    // typically a narrower subclass (e.g. "men's national association
    // football team") rather than Q6979593 itself.
    private static string BuildPlayerCareerStintsByQidsQuery(IReadOnlyList<string> qids)
    {
        var valuesClause = string.Join(" ", qids.Select(qid => $"wd:{qid}"));
        return $$"""
            SELECT ?player ?clubLabel ?startTime ?endTime ?numberOfMatches WHERE {
              VALUES ?player { {{valuesClause}} }
              ?player p:P54 ?clubStatement.
              ?clubStatement ps:P54 ?club.
              MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
              MINUS { ?club wdt:P31/wdt:P279* wd:{{NationalTeamClassWikidataQid}}. }
              OPTIONAL { ?clubStatement pq:P580 ?startTime. }
              OPTIONAL { ?clubStatement pq:P582 ?endTime. }
              OPTIONAL { ?clubStatement pq:P1350 ?numberOfMatches. }
              SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
            }
            """;
    }

    // Grouped by QID, one list entry per distinct (ClubName, StartYear,
    // EndYear, AppearanceCount) tuple — same HashSet-based dedup
    // ParseBindings' CareerStints field uses, for the same reason (SPARQL's
    // OPTIONAL semantics can otherwise multiply rows). A row where
    // startTime never bound carries zero information (StartYear is
    // non-nullable on WikidataCareerStintEntry) and is skipped, same as
    // ParseBindings' own CareerStintQualifiers construction. A row with no
    // clubLabel binding at all (should not happen — ?club is a mandatory,
    // non-OPTIONAL match) is also skipped defensively rather than persisting
    // a stint with a blank club name.
    //
    // Bug fix (2026-08-03, xG Path duplicate-stint bug, REQ-1203): the
    // ClubName the HashSet dedups (and every caller ultimately persists) is
    // ?clubLabel, since BuildPlayerCareerStintsByQidsQuery never selects the
    // underlying ?club QID itself — this method has no QID to dedupe or key
    // on, only the rendered label string. Observed in production (bug
    // report with screenshot): one real stint surfaced across two rows as
    // "Liverpool" on one and "Liverpool F.C." on the other — otherwise
    // identical (start, end, appearance count), so the two rows are
    // structurally the same real stint but fail this HashSet's exact
    // string/record equality and show up as two path nodes. WHY Wikidata's
    // own statements carry two label variants for what is presumably one
    // underlying ?club (or two ?club items both resolving to "the same"
    // real club) isn't diagnosable from this sandbox without a live SPARQL
    // query against wikidata.org — see NormalizeClubName's own comment for
    // the (deliberately narrow) fix applied to the observed symptom.
    //
    // Known, ACCEPTED limitation of the above fix (quality-gate finding,
    // 2026-08-03): the HashSet dedup two paragraphs below is still keyed
    // on the FULL (ClubName, StartYear, EndYear, AppearanceCount) tuple.
    // Normalizing ClubName only collapses duplicate rows that also agree
    // on every other field. Two rows for what is really the same stint
    // but that disagree on AppearanceCount (e.g. one row's P1350
    // qualifier absent -> null, the other's present -> 25 -- plausible,
    // since two Wikidata statements for "the same" stint can carry
    // differently-complete qualifiers) or on EndYear will still fail to
    // merge and reproduce the duplicate-node symptom for that variant.
    // This is deliberately NOT widened here: doing so (e.g. treating a
    // null AppearanceCount as "matches anything") would risk silently
    // merging two GENUINELY different stints at the same club with
    // matching dates but different, both-known appearance counts -- a
    // correctness risk, not just a display one, and a strictly worse
    // failure mode than the display duplicate this fix targets. If this
    // variant is observed in practice it needs its own deliberate merge
    // rule (and test), not a silent loosening of this tuple.
    private static IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>> ParseCareerStintBindings(SparqlResponse? response)
    {
        var stintsByQid = new Dictionary<string, HashSet<WikidataCareerStintEntry>>();
        if (response?.Results?.Bindings is null)
            return new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>();

        foreach (var binding in response.Results.Bindings)
        {
            if (!binding.TryGetValue("player", out var playerValue) || string.IsNullOrEmpty(playerValue.Value))
                continue;

            if (!binding.TryGetValue("clubLabel", out var clubLabelValue) || string.IsNullOrWhiteSpace(clubLabelValue.Value))
                continue;

            if (!binding.TryGetValue("startTime", out var startTimeValue) || !TryParseXsdDateTimeYear(startTimeValue.Value, out var startYear))
                continue;

            int? endYear = binding.TryGetValue("endTime", out var endTimeValue)
                && TryParseXsdDateTimeYear(endTimeValue.Value, out var parsedEndYear)
                    ? parsedEndYear
                    : null;
            int? appearanceCount = binding.TryGetValue("numberOfMatches", out var numberOfMatchesValue)
                && int.TryParse(numberOfMatchesValue.Value, out var parsedAppearanceCount)
                    ? parsedAppearanceCount
                    : null;

            var qid = playerValue.Value.Split('/').Last();
            if (!stintsByQid.TryGetValue(qid, out var stints))
                stintsByQid[qid] = stints = [];

            // Normalize BEFORE the HashSet sees it: this is the club name
            // that both dedup and every downstream persistence use — see
            // WikidataCareerStintEntry's own doc comment and this class's
            // NormalizeClubName for why the canonical (not raw) form is
            // what gets stored.
            stints.Add(new WikidataCareerStintEntry(NormalizeClubName(clubLabelValue.Value), startYear, endYear, appearanceCount));
        }

        return stintsByQid.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<WikidataCareerStintEntry>)kv.Value.ToList());
    }

    // Legal-suffix variants Wikidata is observed to use interchangeably for
    // what is the same real club (e.g. "Liverpool" vs "Liverpool F.C.",
    // both attested as ?clubLabel values for the same P54 statement shape —
    // see ParseCareerStintBindings' own comment for the exact bug this
    // fixes). Ordered longest-first so a longer variant (e.g. "A.F.C.")
    // is matched whole rather than partially matching a shorter entry
    // later in the list ("F.C.") first.
    //
    // Deliberately a small, explicit list, not a fuzzy/generic name
    // matcher: a generic matcher risks merging two DIFFERENT clubs that
    // happen to share a prefix (e.g. stripping too aggressively could
    // conflate "Real Madrid" and "Real Sociedad"-style near-collisions).
    // This only ever strips one of these four exact, well-known football
    // legal-suffix tokens, and only when it is the trailing token of the
    // name (preceded by whitespace) — never a substring inside an
    // unrelated word, and never a PREFIX (e.g. "AFC Bournemouth" is a
    // different, legitimate naming convention and is left untouched).
    //
    // Single-pass, not recursive: only ONE trailing suffix is ever
    // stripped, so a hypothetical stacked label like "Club FC A.F.C."
    // would only lose the first match ("A.F.C.") and come back as
    // "Club FC", not "Club". Judged acceptable given this is a narrow,
    // 4-entry list of real football legal suffixes -- a doubly-suffixed
    // label has not been observed and is not expected in practice.
    private static readonly string[] ClubNameLegalSuffixes = ["A.F.C.", "F.C.", "AFC", "FC"];

    private static string NormalizeClubName(string rawClubName)
    {
        var trimmed = rawClubName.Trim();

        foreach (var suffix in ClubNameLegalSuffixes)
        {
            if (trimmed.Length <= suffix.Length)
                continue;

            if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Must be a distinct trailing TOKEN — the character right
            // before the suffix must be whitespace, or this would also
            // strip "FC" out of the middle/end of an unrelated single
            // word.
            if (!char.IsWhiteSpace(trimmed[trimmed.Length - suffix.Length - 1]))
                continue;

            return trimmed[..^suffix.Length].TrimEnd();
        }

        return trimmed;
    }

    // ADR-0056: xG Path's own familiarity signal — batched, direct-by-QID
    // Wikipedia sitelink-count lookup, the same VALUES-clause-over-a-
    // bounded-batch shape as QueryPlayerPhotosByQidsAsync/
    // QueryPlayerPositionsAndBirthYearsByQidsAsync. `wikibase:sitelinks` is
    // WDQS's own computed predicate (count of Wikipedia/sister-project pages
    // linked to the item) — not a stored Wikidata statement, so there is no
    // corresponding P-number, and every item resolves it (0 for one with no
    // sitelinks at all) rather than leaving it unbound; OPTIONAL is kept
    // anyway, matching this file's own defensive style, so a batch entry
    // that somehow fails to resolve still doesn't drop the whole query.
    //
    // Error contract — same throw-on-failure shape as
    // QueryPlayerPhotosByQidsAsync/QueryPlayerPositionsAndBirthYearsByQidsAsync/
    // QueryPlayerCareerStintsByQidsAsync (throw WikidataQueryException on
    // timeout/HTTP/parse failure, not the intersection queries' swallow-to-[]
    // contract): the caller (XGPathGameModule.GetEligiblePlayerIdsAsync) is
    // responsible for deciding a failed familiarity check must never block
    // round generation (REQ-103's established reasoning, same as
    // PlayerCareerStintRefreshService's own catch) — this client method
    // itself must not silently conflate "this player really has 0 sitelinks"
    // with "the query failed."
    public async Task<IReadOnlyDictionary<string, int>> QuerySitelinkCountsByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default)
    {
        if (wikidataQids.Count == 0)
            return new Dictionary<string, int>();

        foreach (var qid in wikidataQids)
        {
            if (!WikidataQid.IsValid(qid))
                throw new ArgumentException($"Not a valid Wikidata QID: '{qid}'", nameof(wikidataQids));
        }

        var query = BuildSitelinkCountsByQidsQuery(wikidataQids);
        var requestUri = $"sparql?query={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

        using var timeoutCts = new CancellationTokenSource(_queryTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var response = await httpClient.SendAsync(request, linkedCts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
            var parsed = await JsonSerializer.DeserializeAsync<SparqlResponse>(stream, JsonOptions, linkedCts.Token);

            return ParseSitelinkCountBindings(parsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new WikidataQueryException(
                $"Wikidata sitelink-count batch query for {wikidataQids.Count} QID(s) timed out after {_queryTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new WikidataQueryException(
                $"Wikidata sitelink-count batch query for {wikidataQids.Count} QID(s) failed: {ex.Message}", ex);
        }
    }

    // Same "VALUES clause over the batch, no candidate-matching filter"
    // shape as BuildPlayerPhotosByQidsQuery/
    // BuildPlayerPositionsAndBirthYearsByQidsQuery — the caller
    // (XGPathGameModule) is responsible for keeping each batch within the
    // same bounded-query class every other batch method in this file uses.
    private static string BuildSitelinkCountsByQidsQuery(IReadOnlyList<string> qids)
    {
        var valuesClause = string.Join(" ", qids.Select(qid => $"wd:{qid}"));
        return $$"""
            SELECT ?player ?sitelinks WHERE {
              VALUES ?player { {{valuesClause}} }
              OPTIONAL { ?player wikibase:sitelinks ?sitelinks. }
            }
            """;
    }

    // Keyed by QID, same "one row per batch entry, absent means none" shape
    // as ParsePhotoBindings — a QID whose sitelink count didn't parse as an
    // integer is treated as absent (never a 0 masquerading as "resolved but
    // unfamiliar"), so XGPathGameModule's threshold check correctly treats
    // it the same as "no data available" rather than "confirmed obscure."
    private static IReadOnlyDictionary<string, int> ParseSitelinkCountBindings(SparqlResponse? response)
    {
        var sitelinkCountsByQid = new Dictionary<string, int>();
        if (response?.Results?.Bindings is null)
            return sitelinkCountsByQid;

        foreach (var binding in response.Results.Bindings)
        {
            if (!binding.TryGetValue("player", out var playerValue) || string.IsNullOrEmpty(playerValue.Value))
                continue;
            if (!binding.TryGetValue("sitelinks", out var sitelinksValue)
                || !int.TryParse(sitelinksValue.Value, out var count))
                continue;

            var qid = playerValue.Value.Split('/').Last();
            sitelinkCountsByQid.TryAdd(qid, count);
        }

        return sitelinkCountsByQid;
    }

    // REQ-216/ADR-0057: see IWikidataClient's own doc comment for the full
    // "why this exists, why LIMIT 1, why it throws" reasoning.
    //
    // Timeout budget: deliberately reuses _queryTimeout (15s), the same
    // budget REQ-103/grid-generation's Sync-origin lookups use — NOT
    // _guessTimeFallbackQueryTimeout's 28s. That longer budget exists
    // specifically because ADR-0046's REQ-211 caller has no fallback (a slow
    // timeout there means the player's genuinely-correct guess gets scored
    // wrong), so it's worth making them wait longer for an honest answer.
    // This trigger has the opposite shape: on any failure it silently shows
    // nothing (ADR-0057), so there's no equivalent "worth waiting longer"
    // argument, and this query's shape (a single label/alias match, no P54
    // full-statement-path join) is far cheaper than the club-membership
    // queries that motivated the 28s budget in the first place. Reusing the
    // existing 15s constant also avoids adding a fourth constructor
    // parameter/timeout field to this class for a single new call site.
    public async Task<WikidataPlayerPhotoLookupResult?> QueryPlayerPhotoByNameAsync(
        string playerName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return null;

        var query = BuildPlayerPhotoByNameQuery(playerName);
        var requestUri = $"sparql?query={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

        using var timeoutCts = new CancellationTokenSource(_queryTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var response = await httpClient.SendAsync(request, linkedCts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
            var parsed = await JsonSerializer.DeserializeAsync<SparqlResponse>(stream, JsonOptions, linkedCts.Token);

            return ParsePlayerPhotoByNameBinding(parsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new WikidataQueryException(
                $"Wikidata wrong-guess photo-by-name query for '{playerName}' timed out after {_queryTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new WikidataQueryException(
                $"Wikidata wrong-guess photo-by-name query for '{playerName}' failed: {ex.Message}", ex);
        }
    }

    // Case-insensitive label-OR-alias match, deliberately LIMIT 1 — see
    // IWikidataClient.QueryPlayerPhotoByNameAsync's own doc comment for why
    // this is the one query in this file that both filters by a free-text
    // string and caps its result set. rdfs:label is Wikidata's primary
    // label triple (distinct from the SERVICE wikibase:label block below,
    // which resolves ?playerLabel for the SELECTed player once a match is
    // already found via either branch of the UNION) — skos:altLabel is the
    // same alias predicate BuildIntersectionQuery already uses elsewhere in
    // this file.
    private static string BuildPlayerPhotoByNameQuery(string playerName)
    {
        var escapedName = EscapeSparqlStringLiteral(playerName);
        return $$"""
            SELECT ?player ?playerLabel ?photo WHERE {
              ?player wdt:P106 wd:Q937857.
              {
                ?player rdfs:label ?matchedLabel.
                FILTER(LANG(?matchedLabel) = "en")
              }
              UNION
              {
                ?player skos:altLabel ?matchedLabel.
                FILTER(LANG(?matchedLabel) = "en")
              }
              FILTER(LCASE(STR(?matchedLabel)) = LCASE("{{escapedName}}"))
              OPTIONAL { ?player wdt:P18 ?photo. }
              SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
            }
            LIMIT 1
            """;
    }

    // This file's first query to interpolate free, player-supplied text
    // (every other query only ever interpolates a QID this codebase itself
    // resolved, or a fixed constant) — escapes the two characters that would
    // otherwise break out of the SPARQL string literal (backslash, double
    // quote) and strips newlines (a submitted guess should never contain
    // one, but a SPARQL string literal can't safely span lines either way).
    private static string EscapeSparqlStringLiteral(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");

    // Single-row result (LIMIT 1) — takes the first (only) binding, same
    // "absent means none, never an error" contract as ParsePhotoBindings.
    // A binding with no playerLabel is defensively treated as no match at
    // all (never seen in practice, since SERVICE wikibase:label always
    // resolves a label for any real player item, but this avoids ever
    // returning a WikidataPlayerPhotoLookupResult with an empty FullName).
    private static WikidataPlayerPhotoLookupResult? ParsePlayerPhotoByNameBinding(SparqlResponse? response)
    {
        var binding = response?.Results?.Bindings?.FirstOrDefault();
        if (binding is null)
            return null;
        if (!binding.TryGetValue("playerLabel", out var labelValue) || string.IsNullOrWhiteSpace(labelValue.Value))
            return null;

        var photoUrl = binding.TryGetValue("photo", out var photoValue) && !string.IsNullOrWhiteSpace(photoValue.Value)
            ? photoValue.Value
            : null;

        return new WikidataPlayerPhotoLookupResult(labelValue.Value, photoUrl);
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
            // practice for a Wikidata person item. Reads "positionLabel"
            // (bug fix, 2026-08-02), not "position" — see BuildIntersectionQuery's
            // own comment for why the raw binding is a bare entity URI, never
            // the human-readable string this field is meant to hold.
            if (entry.Position is null && binding.TryGetValue("positionLabel", out var positionValue)
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

using System.Net.Http.Headers;
using System.Text.Json;
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
    TimeSpan? cacheWarmingQueryTimeout = null,
    TimeSpan? adminLookupQueryTimeout = null) : IWikidataClient
{
    // Optional param (like queryTimeout) so tests can construct a client
    // without wiring DI's logging; falls back to a real ILogger<T> in
    // production via the AddHttpClient<IWikidataClient, WikidataClient>
    // registration in Program.cs, which supplies one automatically.
    private readonly ILogger<WikidataClient> _logger = logger ?? NullLogger<WikidataClient>.Instance;

    // The same ADR-0025 pool floor as SparqlQueryBuilders.DateOfBirthCutoff
    // (S-155 moved that constant out of this file, docs/backlog.md), as a
    // plain year — QueryPlayerPoolBirthYearAsync slices the bulk import by
    // birth year, and PlayerNameIndexImporter iterates from this year to the
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

    // REQ-509/REQ-510 tuning fix (2026-08-09): QueryPlayerCareerAndNationalityByNameAsync
    // was still reusing _queryTimeout (15s) — see that method's own comment
    // for why that was wrong. This is a fourth, admin-lookup-only budget,
    // same overridable-for-tests shape as the three above.
    //
    // 45s, same evidence band as _cacheWarmingQueryTimeout's: at the time
    // this budget was set, this method's query shape was the same kind of
    // broad, unindexed population-wide rdfs:label/skos:altLabel scan across
    // every Wikidata footballer that cache warming's own reference-pair
    // sweep runs into — ADR-0062 (2026-08-09, same day, separate change)
    // has since replaced that scan with an indexed `wikibase:mwapi`
    // EntitySearch call (see BuildPlayerCareerAndNationalityByNameQuery's
    // own comment), but this 45s budget itself is untouched: it's still the
    // right margin for a live, admin-synchronous Wikidata round trip
    // regardless of which mechanism selects the candidate player
    // underneath it, and none of the reasoning below (WDQS's observed
    // latency range, the ~60s server-side cap, the "someone is waiting"
    // framing) depended on the old query shape specifically. Not the narrow
    // per-cell intersection shape ADR-0011's original 15s/28s budgets were
    // tuned for either way. ADR-0011's own evidence (WDQS queries observed 9-27s under
    // load) is the same evidence both _guessTimeFallbackQueryTimeout's 28s
    // and _cacheWarmingQueryTimeout's 45s already lean on; 45s gives this
    // broad shape the same comfortably wider margin above that 9-27s range
    // that cache warming gets, while staying safely under WDQS's own ~60s
    // hard SERVER-side cap (see NOTES.md's 2026-07-18 entry — pushing this
    // much past ~55s risks re-triggering that exact trap, where a client
    // timeout increase does nothing because the query is killed
    // server-side first; 45s leaves real margin under that ceiling, not
    // just under the observed range).
    //
    // Unlike cache warming, though, this is NOT a "nobody's waiting" budget
    // — REQ-509/REQ-510's caller is an admin synchronously blocked on this
    // in a browser tab (AdminSuggestionEndpoints' two /admin/... lookup
    // endpoints), closer in spirit to guess-time fallback's "someone is
    // waiting" framing than to cache warming's unattended CLI-verb-inside-
    // a-90-minute-job framing. But the query itself is shaped like cache
    // warming's (broad, population-wide), not guess-time fallback's
    // (narrow, per-cell) — so 28s (tuned for the narrow shape) would be too
    // tight here, and this can't just reuse _guessTimeFallbackQueryTimeout
    // either, since REQ-509/510 doesn't share REQ-211's ADR-0046 fail-
    // closed-as-incorrect contract. 45s is the deliberate balance: wide
    // enough for the broad query shape this method actually runs, without
    // making an admin who IS waiting sit through cache warming's full
    // margin for a query that (unlike cache warming's unattended sweep)
    // has no larger job budget to be a "small fraction" of.
    private readonly TimeSpan _adminLookupQueryTimeout = adminLookupQueryTimeout ?? TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // S-100 (docs/backlog.md): thin wrapper over QueryIntersectionAsync's
    // shared driver — the QID guard and query-building this used to do
    // inline now live centrally, keyed off (CategoryType.Country,
    // CategoryType.Club) via IntersectionQuerySpecs.ByCategoryPair.
    // Signature and behavior are unchanged for every caller (GridGameModule,
    // XGPathGameModule, WikidataLookupService, WikidataClientTests.cs).
    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
        string countryWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        await QueryIntersectionAsync(CategoryType.Country, CategoryType.Club, countryWikidataQid, clubWikidataQid, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);

    // REQ-114/ADR-0035: England/Scotland/Wales/Northern Ireland's P1532
    // counterpart of QueryCountryClubIntersectionAsync above.
    // S-100: thin wrapper over QueryIntersectionAsync — see
    // QueryCountryClubIntersectionAsync's own comment above for the shape.
    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryNationalTeamClubIntersectionAsync(
        string nationalTeamWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        await QueryIntersectionAsync(CategoryType.NationalTeam, CategoryType.Club, nationalTeamWikidataQid, clubWikidataQid, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);

    // S-100: thin wrapper over QueryIntersectionAsync — see
    // QueryCountryClubIntersectionAsync's own comment above for the shape.
    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryClubClubIntersectionAsync(
        string clubAWikidataQid,
        string clubBWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        await QueryIntersectionAsync(CategoryType.Club, CategoryType.Club, clubAWikidataQid, clubBWikidataQid, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);

    // S-101: thin wrapper over QueryIntersectionAsync — see
    // QueryCountryClubIntersectionAsync's own comment above for the shape.
    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyCountryIntersectionAsync(
        string trophyWikidataQid,
        string countryWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        await QueryIntersectionAsync(CategoryType.Trophy, CategoryType.Country, trophyWikidataQid, countryWikidataQid, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);

    // S-101: thin wrapper over QueryIntersectionAsync — see
    // QueryCountryClubIntersectionAsync's own comment above for the shape.
    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyClubIntersectionAsync(
        string trophyWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        await QueryIntersectionAsync(CategoryType.Trophy, CategoryType.Club, trophyWikidataQid, clubWikidataQid, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);

    // ADR-0061: team-competition trophy x country, player-side P27.
    // S-101: thin wrapper over QueryIntersectionAsync — see
    // QueryCountryClubIntersectionAsync's own comment above for the shape.
    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyCountryIntersectionAsync(
        string trophyWikidataQid,
        string countryWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        await QueryIntersectionAsync(CategoryType.TeamTrophy, CategoryType.Country, trophyWikidataQid, countryWikidataQid, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);

    // ADR-0061: team-competition trophy x country, player-side P1532 (home
    // nations) — the ADR-0035 counterpart of QueryTeamTrophyCountryIntersectionAsync
    // above.
    // S-101: thin wrapper over QueryIntersectionAsync — see
    // QueryCountryClubIntersectionAsync's own comment above for the shape.
    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyNationalTeamIntersectionAsync(
        string trophyWikidataQid,
        string countryWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        await QueryIntersectionAsync(CategoryType.TeamTrophy, CategoryType.NationalTeam, trophyWikidataQid, countryWikidataQid, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);

    // ADR-0061: team-competition trophy x club.
    // S-101: thin wrapper over QueryIntersectionAsync — see
    // QueryCountryClubIntersectionAsync's own comment above for the shape.
    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyClubIntersectionAsync(
        string trophyWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        await QueryIntersectionAsync(CategoryType.TeamTrophy, CategoryType.Club, trophyWikidataQid, clubWikidataQid, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);

    // Judgment call (see IWikidataClient's own doc comment on this method for
    // the full reasoning): the individual-award P166 counterpart of
    // QueryTeamTrophyNationalTeamIntersectionAsync — P1532 player-side,
    // needed to fully close ADR-0035's follow-up note (which was about
    // LookupAndPersistTrophyCountryAsync "in general," not just the
    // team-trophy branch ADR-0061 itself adds).
    // S-101: thin wrapper over QueryIntersectionAsync — see
    // QueryCountryClubIntersectionAsync's own comment above for the shape.
    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyNationalTeamIntersectionAsync(
        string trophyWikidataQid,
        string countryWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        await QueryIntersectionAsync(CategoryType.Trophy, CategoryType.NationalTeam, trophyWikidataQid, countryWikidataQid, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);

    // S-100/S-101 (docs/backlog.md): the shared driver every
    // Query*IntersectionAsync wrapper calls into — looks its spec up in
    // IntersectionQuerySpecs.ByCategoryPair by (typeA, typeB), the same
    // (CategoryType, CategoryType) key the spec table is built around, then
    // centralizes the WikidataQid.IsValid guard that used to be duplicated
    // per method (nine near-identical copies before S-100). All 9 pairs go
    // through this driver as of S-101.
    //
    // The guard's ArgumentException.ParamName is now the generic "qidA"/
    // "qidB" rather than each wrapper's own parameter name (e.g.
    // "countryWikidataQid") — the one deliberate, harmless difference from
    // centralizing a check that used to be able to name its own parameter
    // per call site. No caller (production or WikidataClientTests.cs)
    // asserts on ParamName or message text, only on the exception type.
    private async Task<IReadOnlyList<WikidataPlayerMatch>> QueryIntersectionAsync(
        CategoryType typeA,
        CategoryType typeB,
        string qidA,
        string qidB,
        bool throwOnTimeout,
        CancellationToken cancellationToken,
        Action? onTechnicalFailure,
        WikidataQueryTimeoutTier timeoutTier)
    {
        if (!WikidataQid.IsValid(qidA))
            throw new ArgumentException($"Not a valid Wikidata QID: '{qidA}'", nameof(qidA));
        if (!WikidataQid.IsValid(qidB))
            throw new ArgumentException($"Not a valid Wikidata QID: '{qidB}'", nameof(qidB));

        var spec = IntersectionQuerySpecs.ByCategoryPair[(typeA, typeB)];
        var query = SparqlQueryBuilders.BuildIntersectionQuery(spec.BuildCandidateClauses(qidA, qidB));
        return await RunIntersectionQueryAsync(spec.QueryKind, qidA, qidB, query, throwOnTimeout, cancellationToken, onTechnicalFailure, timeoutTier);
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
            var parsed = await JsonSerializer.DeserializeAsync<SparqlResponseParsers.SparqlResponse>(stream, JsonOptions, linkedCts.Token);

            return SparqlResponseParsers.ParseBindings(parsed);
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

    // S-118 (docs/backlog.md, Epic 9): the shared HTTP/timeout/retry driver
    // for the six "throwing" query methods S-118 scoped in —
    // QueryPlayerPoolBirthYearAsync, QueryPlayerPoolByNationalityAsync,
    // QueryPlayerPositionsAndBirthYearsByQidsAsync, QuerySitelinkCountsByQidsAsync,
    // QueryPlayerCareerStintsByQidsAsync, and QueryPlayerCareerAndNationalityByNameAsync —
    // all of which used to hand-roll this exact HTTP send / timeout-CTS /
    // catch/throw shape independently (nine near-identical copies before
    // S-100/S-101 fixed this for the intersection queries; these six were
    // added afterward by later ADRs and never migrated).
    //
    // S-124: QueryPlayerPhotosByQidsAsync and QueryPlayerPhotoByNameAsync
    // (outside S-118's originally-scoped six-method list) also hand-rolled
    // this identical throw-on-failure HTTP send / timeout-CTS / catch shape
    // and have since been migrated onto this same driver — see each
    // method's own thin-wrapper body below. This is the "throwing" sibling
    // of
    // RunIntersectionQueryAsync above — deliberately a SEPARATE method, not a
    // generalization of RunIntersectionQueryAsync itself, since the two have
    // genuinely different error contracts (swallow-to-[] unless
    // throwOnTimeout vs. always throw WikidataQueryException) that a single
    // shared method would have to reintroduce a flag to distinguish — see
    // this file's own "never conflate a real failure with a genuine empty
    // result" convention (IWikidataClient's per-method doc comments) for why
    // that distinction is worth keeping structurally separate, not just
    // parameterized away.
    //
    // Deliberately takes an explicit TimeSpan `timeout`, not a
    // WikidataQueryTimeoutTier — that enum's whole reason to exist is
    // resolving a timeout from TWO independent axes (throwOnTimeout AND
    // timeoutTier, see WikidataQueryTimeoutTier's own doc comment), which
    // only the five intersection queries need. None of the six callers here
    // have a throwOnTimeout parameter at all (they always throw), so there is
    // only ever one axis: which one of this class's four fixed budget fields
    // (_queryTimeout/_guessTimeFallbackQueryTimeout/_cacheWarmingQueryTimeout/
    // _adminLookupQueryTimeout) a given method always uses. Forcing that
    // single fixed choice through the tier enum would need either reusing
    // Default (misleading — Default's own resolution logic depends on
    // throwOnTimeout, which doesn't exist here) or adding a tier whose value
    // (45s) happens to coincide with CacheWarming's today — but
    // _adminLookupQueryTimeout and _cacheWarmingQueryTimeout are two
    // independently-reasoned budgets that simply happen to share a number
    // right now (see _adminLookupQueryTimeout's own doc comment, which is
    // explicit that this is "not a nobody's waiting" budget the way cache
    // warming's is) — collapsing them into one shared tier would silently
    // couple two call sites that could reasonably diverge in the future.
    // Passing the field directly keeps that decoupling intact and needs no
    // enum change at all.
    //
    // `description` is the query's own human-readable identity (e.g.
    // "Wikidata player-pool query for birth year 1977") — every one of the
    // six callers' pre-refactor timeout/failure exception messages already
    // followed the exact "{description} timed out after {N}s."/
    // "{description} failed: {message}" shape, so building both messages
    // here from one shared description string reproduces each method's
    // original wording byte-for-byte (see WikidataClientTests.cs's existing
    // per-method timeout/error tests, none of which needed to change).
    private async Task<T> RunThrowingQueryAsync<T>(
        string query,
        TimeSpan timeout,
        string description,
        Func<SparqlResponseParsers.SparqlResponse?, T> parseResponse,
        CancellationToken cancellationToken)
    {
        var requestUri = $"sparql?query={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var response = await httpClient.SendAsync(request, linkedCts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
            var parsed = await JsonSerializer.DeserializeAsync<SparqlResponseParsers.SparqlResponse>(stream, JsonOptions, linkedCts.Token);

            return parseResponse(parsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Same caller-cancellation-vs-own-timeout distinction every
            // migrated method's own pre-refactor catch filter already made
            // (see e.g. QueryPlayerPoolBirthYearAsync_CallerCancellation_
            // PropagatesAsOperationCanceledException_NotWikidataQueryException) —
            // a genuine caller cancellation (cancellationToken itself
            // triggered) falls through this filter and propagates as an
            // ordinary OperationCanceledException, never misclassified as
            // this client's own query failure.
            throw new WikidataQueryException($"{description} timed out after {timeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new WikidataQueryException($"{description} failed: {ex.Message}", ex);
        }
    }

    // S-118: thin wrapper over the shared RunThrowingQueryAsync driver — see
    // that method's own doc comment for the shared HTTP/timeout/error-
    // handling shape. Signature and behavior (including the
    // caller-cancellation-vs-own-timeout distinction) are unchanged for
    // every caller (PlayerNameIndexImporter, WikidataClientTests.cs).
    public async Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolBirthYearAsync(
        int birthYear, CancellationToken cancellationToken = default)
    {
        if (birthYear < FirstEligibleBirthYear)
            throw new ArgumentOutOfRangeException(nameof(birthYear), birthYear,
                $"birthYear must be {FirstEligibleBirthYear} or later (ADR-0025's player-pool floor).");

        var query = SparqlQueryBuilders.BuildPlayerPoolBirthYearQuery(birthYear);
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata player-pool query for birth year {birthYear}",
            SparqlResponseParsers.ParseNameIndexBindings, cancellationToken);
    }

    // ADR-0055: PlayerCareerPrefetchService's per-country pool query — see
    // IWikidataClient's own doc comment for why this is a nationality-scoped
    // sibling to QueryPlayerPoolBirthYearAsync above, not a replacement.
    // S-118: thin wrapper over the shared RunThrowingQueryAsync driver — same
    // throw-on-failure contract as QueryPlayerPoolBirthYearAsync, see that
    // method's own doc comment (IWikidataClient) for why. No QID validation
    // here, matching the pre-refactor method exactly — nationalityWikidataQid
    // was never guarded by WikidataQid.IsValid before this refactor either.
    public async Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByNationalityAsync(
        string nationalityWikidataQid, bool useCountryForSportProperty, CancellationToken cancellationToken = default)
    {
        var query = SparqlQueryBuilders.BuildPlayerPoolByNationalityQuery(nationalityWikidataQid, useCountryForSportProperty);
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata player-pool query for nationality {nationalityWikidataQid}",
            SparqlResponseParsers.ParseNameIndexBindings, cancellationToken);
    }

    // ADR-0069: PlayerCareerPrefetchService's per-club pool query — see
    // IWikidataClient's own doc comment for why this is a club-scoped
    // sibling to QueryPlayerPoolByNationalityAsync, not a replacement.
    // Thin wrapper over the shared RunThrowingQueryAsync driver — same
    // throw-on-failure contract as QueryPlayerPoolByNationalityAsync, see
    // that method's own doc comment for why. No QID validation here, same
    // precedent as QueryPlayerPoolByNationalityAsync's own "nationalityWikidataQid
    // was never guarded by WikidataQid.IsValid" note.
    public async Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByClubAsync(
        string clubWikidataQid, CancellationToken cancellationToken = default)
    {
        var query = SparqlQueryBuilders.BuildPlayerPoolByClubQuery(clubWikidataQid);
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata player-pool query for club {clubWikidataQid}",
            SparqlResponseParsers.ParseNameIndexBindings, cancellationToken);
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

        var query = SparqlQueryBuilders.BuildPlayerPhotosByQidsQuery(wikidataQids);

        // S-124: thin wrapper over the shared RunThrowingQueryAsync driver —
        // same throw-on-failure contract as every other batch-by-QID query
        // in this file (see that driver's own doc comment) — the opposite
        // of the intersection queries' swallow-to-[] contract, deliberately:
        // this is a batch job whose success metric is a backfilled-row
        // count, so a swallowed failure would be indistinguishable from
        // "none of these QIDs have a photo."
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata player-photo batch query for {wikidataQids.Count} QID(s)",
            SparqlResponseParsers.ParsePhotoBindings, cancellationToken);
    }

    // REQ-1207 backfill (bug-bundle fix, 2026-08-02): batched, direct-by-QID
    // position/birth-year lookup — see IWikidataClient's own doc comment for
    // why this is a different query shape from the intersection queries
    // above and why its error contract (throw, not swallow-to-empty) matches
    // QueryPlayerPhotosByQidsAsync rather than them.
    // S-118: thin wrapper over the shared RunThrowingQueryAsync driver — same
    // throw-on-failure contract as QueryPlayerPhotosByQidsAsync (see that
    // method's own comment) — a swallowed failure here would be
    // indistinguishable from "none of these QIDs have this data."
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

        var query = SparqlQueryBuilders.BuildPlayerPositionsAndBirthYearsByQidsQuery(wikidataQids);
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata player position/birth-year batch query for {wikidataQids.Count} QID(s)",
            SparqlResponseParsers.ParsePositionBirthYearBindings, cancellationToken);
    }

    // ADR-0054: xG Path's own direct career fetch — see IWikidataClient's own
    // doc comment for why this is a different query shape from every other
    // method in this file.
    // S-118: thin wrapper over the shared RunThrowingQueryAsync driver — same
    // throw-on-failure contract as QueryPlayerPhotosByQidsAsync/
    // QueryPlayerPositionsAndBirthYearsByQidsAsync — see IWikidataClient's
    // own doc comment for why.
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

        var query = SparqlQueryBuilders.BuildPlayerCareerStintsByQidsQuery(wikidataQids);
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata player career-stint batch query for {wikidataQids.Count} QID(s)",
            SparqlResponseParsers.ParseCareerStintBindings, cancellationToken);
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
    // contract): the caller (PathEligibilityService.GetEligiblePlayerIdsAsync) is
    // responsible for deciding a failed familiarity check must never block
    // round generation (REQ-103's established reasoning, same as
    // PlayerCareerStintRefreshService's own catch) — this client method
    // itself must not silently conflate "this player really has 0 sitelinks"
    // with "the query failed."
    // S-118: thin wrapper over the shared RunThrowingQueryAsync driver — same
    // throw-on-failure contract as QueryPlayerPhotosByQidsAsync/
    // QueryPlayerPositionsAndBirthYearsByQidsAsync/QueryPlayerCareerStintsByQidsAsync
    // (see this method's own doc comment on IWikidataClient for the full
    // reasoning).
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

        var query = SparqlQueryBuilders.BuildSitelinkCountsByQidsQuery(wikidataQids);
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata sitelink-count batch query for {wikidataQids.Count} QID(s)",
            SparqlResponseParsers.ParseSitelinkCountBindings, cancellationToken);
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

        var query = SparqlQueryBuilders.BuildPlayerPhotoByNameQuery(playerName);

        // S-124: thin wrapper over the shared RunThrowingQueryAsync driver —
        // same throw-on-failure contract as QueryPlayerPhotosByQidsAsync
        // (see that method's own comment).
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata wrong-guess photo-by-name query for '{playerName}'",
            SparqlResponseParsers.ParsePlayerPhotoByNameBinding, cancellationToken);
    }

    // REQ-509/REQ-510 (S-090): see IWikidataClient's own doc comment for the
    // full "why" — always throws on failure (no throwOnTimeout param, unlike
    // the five intersection queries), since this is a brand-new,
    // single-purpose admin-triggered action with no "never block" caller.
    //
    // Timeout budget: uses _adminLookupQueryTimeout (45s), NOT _queryTimeout
    // (15s) — bug fix (2026-08-09, REQ-509/REQ-510 tuning). At the time that
    // budget was introduced, BuildPlayerCareerAndNationalityByNameQuery's
    // subquery was a case-insensitive rdfs:label/skos:altLabel scan across
    // every Wikidata footballer (an unindexed, population-wide match, the
    // same query shape _cacheWarmingQueryTimeout was tuned for), not the
    // narrow single-player/single-cell shape 15s was tuned for — see
    // _adminLookupQueryTimeout's own doc comment above for the full
    // evidence/reasoning. ADR-0062 (same day, separate change) replaced that
    // subquery's candidate-selection mechanism with an indexed
    // `SERVICE wikibase:mwapi` EntitySearch call specifically to cut the
    // query's actual cost (a production 502 from WDQS's own gateway, not a
    // timeout — see that ADR's Context section), but does NOT touch this
    // 45s budget: the two are orthogonal fixes (query cost vs. client-side
    // budget), and 45s remains the right budget for this method regardless
    // of which mechanism selects the candidate underneath it.
    //
    // This intentionally now has a DIFFERENT budget than
    // QueryPlayerPhotoByNameAsync just above, even though both are by-name
    // lookups sharing the identical label/alias-match shape — that used to
    // be true parity (both reused _queryTimeout), but this method has no
    // equivalent of QueryPlayerPhotoByNameAsync's ADR-0057 wrong-guess-flow
    // constraint (a live reveal inside a guess-submission response, subject
    // to the frontend's request lifecycle and this app's ingress timeout).
    // An admin's by-name lookup has no such ceiling to respect, so it's free
    // to use the wider, broad-query-shape-appropriate budget instead. Do not
    // "fix" this back into parity — see QueryPlayerPhotoByNameAsync's own
    // comment, which independently already reuses _queryTimeout for its own
    // reason (cost/urgency shape), not because the two methods need to
    // match.
    // S-118: thin wrapper over the shared RunThrowingQueryAsync driver — same
    // always-throws contract as every other by-QID/by-name lookup in this
    // interface except the five swallow-to-[] intersection queries (see this
    // method's own doc comment on IWikidataClient). Uses
    // _adminLookupQueryTimeout (45s), NOT _queryTimeout — see that field's
    // own doc comment above for why this method deliberately does not share
    // a budget (or a WikidataQueryTimeoutTier value) with cache warming's
    // _cacheWarmingQueryTimeout, even though the two happen to be the same
    // 45s today.
    public async Task<WikidataPlayerCareerLookupResult?> QueryPlayerCareerAndNationalityByNameAsync(
        string playerName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return null;

        var query = SparqlQueryBuilders.BuildPlayerCareerAndNationalityByNameQuery(playerName);
        return await RunThrowingQueryAsync(
            query, _adminLookupQueryTimeout, $"Wikidata admin career/nationality-by-name query for '{playerName}'",
            SparqlResponseParsers.ParsePlayerCareerAndNationalityByNameBindings, cancellationToken);
    }

    // REQ-513 (GitHub issue #239): see IWikidataClient's own doc comment for
    // the full "why this exists, why it always throws, why it never returns
    // null" reasoning.
    //
    // Timeout budget: deliberately _queryTimeout (15s), NOT
    // _adminLookupQueryTimeout (45s) — even though this is also an
    // admin-triggered, synchronously-awaited action. _adminLookupQueryTimeout's
    // own doc comment is explicit that its 45s budget is earned by QUERY
    // SHAPE (a broad, population-wide candidate scan/search), not merely by
    // "an admin is waiting" — see that field's comment for the full
    // "shape, not caller" reasoning already established there. This query has
    // no candidate search at all (an exact VALUES-clause match on one
    // already-known QID), the same cheap, indexed, bounded shape
    // QueryPlayerPhotosByQidsAsync/QueryPlayerPositionsAndBirthYearsByQidsAsync
    // already use at _queryTimeout — reusing that budget here keeps the
    // "budget follows shape" rule intact rather than special-casing this
    // method onto the broad-scan budget just because its caller happens to
    // be an admin too.
    public async Task<WikidataPlayerRefreshData> QueryPlayerRefreshDataByQidAsync(
        string wikidataQid, CancellationToken cancellationToken = default)
    {
        if (!WikidataQid.IsValid(wikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{wikidataQid}'", nameof(wikidataQid));

        var query = SparqlQueryBuilders.BuildPlayerRefreshDataByQidQuery(wikidataQid);
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata admin refresh query for {wikidataQid}",
            SparqlResponseParsers.ParsePlayerRefreshDataBinding, cancellationToken);
    }
}

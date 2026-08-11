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
    TimeSpan? cacheWarmingQueryTimeout = null,
    TimeSpan? adminLookupQueryTimeout = null) : IWikidataClient
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
        var query = BuildIntersectionQuery(spec.BuildCandidateClauses(qidA, qidB));
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

    // S-118 (docs/backlog.md, Epic 9): the shared HTTP/timeout/retry driver
    // for every "throwing" query method in this file — QueryPlayerPoolBirthYearAsync,
    // QueryPlayerPoolByNationalityAsync, QueryPlayerPositionsAndBirthYearsByQidsAsync,
    // QuerySitelinkCountsByQidsAsync, QueryPlayerCareerStintsByQidsAsync, and
    // QueryPlayerCareerAndNationalityByNameAsync all used to hand-roll this
    // exact HTTP send / timeout-CTS / catch/throw shape independently (nine
    // near-identical copies before S-100/S-101 fixed this for the
    // intersection queries; these six were added afterward by later ADRs and
    // never migrated). This is the "throwing" sibling of
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
        Func<SparqlResponse?, T> parseResponse,
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
            var parsed = await JsonSerializer.DeserializeAsync<SparqlResponse>(stream, JsonOptions, linkedCts.Token);

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

    // S-100/S-101 (docs/backlog.md): every Build*Query candidate-clause
    // method used to live here as a private static method — all 9 are now
    // moved, unchanged, to IntersectionQuerySpecs.cs as the
    // BuildCandidateClauses delegates for IntersectionQuerySpecs' 9 spec
    // entries (CountryClub/NationalTeamClub/ClubClub from S-100;
    // TrophyCountry/TrophyClub/TeamTrophyCountry/TeamTrophyNationalTeam/
    // TeamTrophyClub/TrophyNationalTeam from S-101). Their own rationale
    // comments (P54's full-statement-path vs. wdt:P54 shortcut, P1532 vs.
    // P27, the ADR-0052 FILTER EXISTS fix, P166's truthy shortcut, the
    // P1344/P3450/P1346 edition-winner join) moved with them — see that
    // file, not here, for all 9.

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

        var query = BuildPlayerPoolBirthYearQuery(birthYear);
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata player-pool query for birth year {birthYear}",
            ParseNameIndexBindings, cancellationToken);
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
    // S-118: thin wrapper over the shared RunThrowingQueryAsync driver — same
    // throw-on-failure contract as QueryPlayerPoolBirthYearAsync, see that
    // method's own doc comment (IWikidataClient) for why. No QID validation
    // here, matching the pre-refactor method exactly — nationalityWikidataQid
    // was never guarded by WikidataQid.IsValid before this refactor either.
    public async Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByNationalityAsync(
        string nationalityWikidataQid, bool useCountryForSportProperty, CancellationToken cancellationToken = default)
    {
        var query = BuildPlayerPoolByNationalityQuery(nationalityWikidataQid, useCountryForSportProperty);
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata player-pool query for nationality {nationalityWikidataQid}",
            ParseNameIndexBindings, cancellationToken);
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

        var query = BuildPlayerPositionsAndBirthYearsByQidsQuery(wikidataQids);
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata player position/birth-year batch query for {wikidataQids.Count} QID(s)",
            ParsePositionBirthYearBindings, cancellationToken);
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

        var query = BuildPlayerCareerStintsByQidsQuery(wikidataQids);
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata player career-stint batch query for {wikidataQids.Count} QID(s)",
            ParseCareerStintBindings, cancellationToken);
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
    // ?club added to the SELECT (bug fix, 2026-08-04, xG Path duplicate-node
    // bug, REQ-1203 follow-up): the 2026-08-03 NormalizeClubName fix above
    // only strips a small, hand-picked set of legal-suffix tokens
    // ("FC"/"AFC"/etc.) and does nothing for a genuine alternate-name
    // variant (e.g. "Lyon" vs. "Olympique Lyonnais") — the underlying ?club
    // QID is the only reliable way to recognize "this is the same real
    // club" across such variants, since ClubDefinition.WikidataQid already
    // exists specifically to canonicalize against (see
    // ParseCareerStintBindings' own comment for where this QID is threaded
    // to). ?club was already bound in the query body (?clubStatement ps:P54
    // ?club) — it just wasn't projected.
    private static string BuildPlayerCareerStintsByQidsQuery(IReadOnlyList<string> qids)
    {
        var valuesClause = string.Join(" ", qids.Select(qid => $"wd:{qid}"));
        return $$"""
            SELECT ?player ?club ?clubLabel ?startTime ?endTime ?numberOfMatches WHERE {
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
    // Formerly-ACCEPTED limitation of the above fix (quality-gate finding,
    // 2026-08-03; partially fixed 2026-08-10, bug-bundle): dedup used to be
    // keyed on the FULL (ClubName, StartYear, EndYear, AppearanceCount)
    // tuple via a plain HashSet, so normalizing ClubName alone only
    // collapsed duplicate rows that also agreed on every other field. Two
    // rows for what is really the same stint but that disagree on
    // AppearanceCount (e.g. one row's P1350 qualifier absent -> null, the
    // other's present -> 25 -- plausible, since two Wikidata statements for
    // "the same" stint can carry differently-complete qualifiers) failed to
    // merge and reproduced the duplicate-node symptom for that variant —
    // this is exactly the "AC Milan 25 apps" / "AC Milan 95 apps" and bare
    // "Real Sociedad" / "Real Sociedad 2 apps" shapes from the 2026-08-10
    // bug report.
    //
    // MergeCareerStintEntries below now handles the NULL-vs-populated case:
    // a null AppearanceCount means "unknown," and a populated value seen on
    // another row for the same (ClubName, StartYear, EndYear) is strictly
    // more informative, not a conflict, so those two rows merge into one,
    // keeping the populated count. The genuinely dangerous case — BOTH rows
    // populated but with DIFFERENT AppearanceCount values — is still
    // deliberately left unmerged: treating that as a match would risk
    // silently merging two GENUINELY different stints at the same club with
    // matching dates but different, both-known appearance counts (e.g. a
    // loan-and-return spell recorded as two separate P54 statements) — a
    // correctness risk, not just a display one, and a strictly worse
    // failure mode than the display duplicate this fix targets. See
    // REQ1203_QueryPlayerCareerStintsByQidsAsync_DoesNotMergeSameClubAndDates_WhenBothAppearanceCountsPopulatedButDiffer
    // for the test locking this narrower carve-out in place.
    private static IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>> ParseCareerStintBindings(SparqlResponse? response)
    {
        var rawEntriesByQid = new Dictionary<string, List<WikidataCareerStintEntry>>();
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
            if (!rawEntriesByQid.TryGetValue(qid, out var entries))
                rawEntriesByQid[qid] = entries = [];

            // ?club (bug fix, 2026-08-04, REQ-1203 follow-up): same
            // "trailing URI segment is the QID" extraction as ?player above.
            // Defensively tolerated as absent (null), even though ?club is a
            // mandatory, non-OPTIONAL match in the query body — a test
            // fixture or an unexpected WDQS response shape omitting it must
            // not drop an otherwise-usable row; the caller-side
            // canonicalization step (PlayerCareerStintRefreshService/
            // PlayerCareerPrefetchService) simply falls back to the
            // normalized label when ClubQid is null, same as it does for a
            // QID that doesn't match any seeded ClubDefinition.
            var clubQid = binding.TryGetValue("club", out var clubValue) && !string.IsNullOrEmpty(clubValue.Value)
                ? clubValue.Value.Split('/').Last()
                : null;

            // Normalize BEFORE MergeCareerStintEntries sees it: this is the
            // club name that both merging and every downstream persistence
            // use — see WikidataCareerStintEntry's own doc comment and this
            // class's NormalizeClubName for why the canonical (not raw)
            // form is what gets stored. NormalizeClubName's suffix-strip is
            // still applied here as the best-effort fallback label (used
            // when ClubQid doesn't resolve to a seeded ClubDefinition) —
            // QID-based canonicalization happens one layer up, not in this
            // client, per this class's own "no ClubDefinition dependency"
            // layering convention within COMP-07 (not a documented
            // cross-component boundary rule — see architecture-document.md's
            // numbered boundary list, which has no entry for this).
            entries.Add(new WikidataCareerStintEntry(NormalizeClubName(clubLabelValue.Value), startYear, endYear, appearanceCount, clubQid));
        }

        return rawEntriesByQid.ToDictionary(kv => kv.Key, kv => MergeCareerStintEntries(kv.Value));
    }

    // Bug fix (2026-08-10, bug-bundle): replaces the plain HashSet-based
    // exact-tuple dedup ParseCareerStintBindings used to do directly. Groups
    // a single player's raw parsed entries by (ClubName, StartYear,
    // EndYear) — the same-real-stint identity — and, within each group,
    // applies the deliberate merge rule described in
    // ParseCareerStintBindings' own comment above:
    //   - exactly one distinct POPULATED AppearanceCount present (whether
    //     alongside one or more null-AppearanceCount rows, or alone): merge
    //     down to a single entry carrying that populated count. A null
    //     AppearanceCount elsewhere in the group is informationally
    //     subsumed, never a conflict.
    //   - more than one distinct POPULATED AppearanceCount present: leave
    //     every row as its own entry, unmerged — the correctness-risk case,
    //     a deliberate non-fix, not an oversight.
    //   - no populated AppearanceCount in the group at all (every row
    //     null): nothing to merge; exact structural duplicates still
    //     collapse via Distinct(), same as the old HashSet did for the
    //     whole record.
    private static IReadOnlyList<WikidataCareerStintEntry> MergeCareerStintEntries(List<WikidataCareerStintEntry> entries)
    {
        var merged = new List<WikidataCareerStintEntry>();

        foreach (var group in entries.GroupBy(e => (e.ClubName, e.StartYear, e.EndYear)))
        {
            var rows = group.ToList();
            var distinctPopulatedCounts = rows
                .Where(r => r.AppearanceCount is not null)
                .Select(r => r.AppearanceCount!.Value)
                .Distinct()
                .ToList();

            if (distinctPopulatedCounts.Count == 1)
            {
                var populatedCount = distinctPopulatedCounts[0];
                var clubQid = rows.Select(r => r.ClubQid).FirstOrDefault(qid => qid is not null);
                merged.Add(rows[0] with { AppearanceCount = populatedCount, ClubQid = clubQid });
                continue;
            }

            // Either >1 distinct populated counts (correctness-risk case,
            // left unmerged) or 0 (nothing to merge) — either way, keep
            // every row, only collapsing exact structural duplicates.
            merged.AddRange(rows.Distinct());
        }

        return merged;
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

        var query = BuildSitelinkCountsByQidsQuery(wikidataQids);
        return await RunThrowingQueryAsync(
            query, _queryTimeout, $"Wikidata sitelink-count batch query for {wikidataQids.Count} QID(s)",
            ParseSitelinkCountBindings, cancellationToken);
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
    public async Task<WikidataPlayerCareerLookupResult?> QueryPlayerCareerAndNationalityByNameAsync(
        string playerName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return null;

        var query = BuildPlayerCareerAndNationalityByNameQuery(playerName);
        var requestUri = $"sparql?query={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

        using var timeoutCts = new CancellationTokenSource(_adminLookupQueryTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var response = await httpClient.SendAsync(request, linkedCts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
            var parsed = await JsonSerializer.DeserializeAsync<SparqlResponse>(stream, JsonOptions, linkedCts.Token);

            return ParsePlayerCareerAndNationalityByNameBindings(parsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new WikidataQueryException(
                $"Wikidata admin career/nationality-by-name query for '{playerName}' timed out after {_adminLookupQueryTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new WikidataQueryException(
                $"Wikidata admin career/nationality-by-name query for '{playerName}' failed: {ex.Message}", ex);
        }
    }

    // ADR-0062 (2026-08-09): the candidate-selection subquery below used to
    // scan every Wikidata footballer's rdfs:label/skos:altLabel with a
    // case-insensitive FILTER — an unindexed, population-wide graph scan
    // that a production log confirmed WDQS's own gateway will 502 on once it
    // runs long enough (38.8s, not a client-side timeout — see
    // _adminLookupQueryTimeout's doc comment above, and ADR-0062's Context
    // section for the full incident). It's replaced with a federated
    // `SERVICE wikibase:mwapi { ... }` block that delegates candidate
    // selection to Wikidata's own indexed `EntitySearch` API (the same
    // engine behind Wikidata's search box) instead of a raw literal scan —
    // same single-endpoint SPARQL client, no new external host. The
    // subquery still selects exactly one candidate ?player (LIMIT 1, same
    // as before): EntitySearch returns up to `mwapi:limit` ranked
    // candidates (10 here — deliberately more than 1, since EntitySearch
    // ranks by general relevance and can surface a same/similar-named
    // non-footballer above the actual match), each re-filtered by
    // `wdt:P106 wd:Q937857` (footballers) before the first survivor is
    // taken. This mechanism change does NOT alter the outer query's shape
    // or bindings — see the OPTIONAL P27/P54 comment just below, which is
    // unchanged. Unverified against the real query.wikidata.org endpoint
    // from this sandbox (no live network access) — see ADR-0062's
    // Consequences section for what a human must confirm before this is
    // trusted in production.
    //
    // Combines that mwapi-based candidate subquery (LIMIT 1 — scoped to the
    // SUBQUERY's own ?player projection, so it bounds "how many CANDIDATE
    // PLAYERS this matches" to one, without truncating that one player's own
    // OPTIONAL P27/P54 rows the way a top-level LIMIT 1 would) with
    // QueryPlayerCareerStintsByQidsAsync's full P54 statement-path club
    // history (same MINUS-deprecated-rank/MINUS-national-team-class shape —
    // see that query builder's own comment for why both MINUS clauses are
    // needed) and a P27 citizenship label. ?nationality/?club are both
    // OPTIONAL and independent of each other — a player matched by name with
    // neither, either, or both bound is every one of those a normal, valid
    // outcome (parsed by ParsePlayerCareerAndNationalityByNameBindings below).
    private static string BuildPlayerCareerAndNationalityByNameQuery(string playerName)
    {
        var escapedName = EscapeSparqlStringLiteral(playerName);
        return $$"""
            SELECT ?player ?playerLabel ?nationalityLabel ?club ?clubLabel ?startTime ?endTime ?numberOfMatches WHERE {
              {
                SELECT ?player WHERE {
                  SERVICE wikibase:mwapi {
                    bd:serviceParam wikibase:api "EntitySearch".
                    bd:serviceParam wikibase:endpoint "www.wikidata.org".
                    bd:serviceParam mwapi:search "{{escapedName}}".
                    bd:serviceParam mwapi:language "en".
                    bd:serviceParam mwapi:limit "10".
                    ?player wikibase:apiOutputItem mwapi:item.
                  }
                  ?player wdt:P106 wd:Q937857.
                }
                LIMIT 1
              }
              OPTIONAL { ?player wdt:P27 ?nationality. }
              OPTIONAL {
                ?player p:P54 ?clubStatement.
                ?clubStatement ps:P54 ?club.
                MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
                MINUS { ?club wdt:P31/wdt:P279* wd:{{NationalTeamClassWikidataQid}}. }
                OPTIONAL { ?clubStatement pq:P580 ?startTime. }
                OPTIONAL { ?clubStatement pq:P582 ?endTime. }
                OPTIONAL { ?clubStatement pq:P1350 ?numberOfMatches. }
              }
              SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
            }
            """;
    }

    // Grouped across every row this query can produce (unlike
    // ParsePlayerPhotoByNameBinding's single LIMIT-1 row): the name-match
    // subquery bounds this to exactly one candidate ?player, but that
    // player's own OPTIONAL P54 club rows still multiply rows the same way
    // ParseCareerStintBindings' own comment describes. WikidataQid/FullName/
    // Nationality are read from the first row that binds each (they're
    // constant across every row for a single matched player); Clubs is a
    // HashSet<string> of distinct club-name labels — simpler than
    // ParseCareerStintBindings' HashSet-of-tuples dedup since this method's
    // Clubs is a plain name list (see WikidataPlayerCareerLookupResult's own
    // doc comment for why), so any two rows sharing a ?clubLabel are simply
    // the same club regardless of what else differs between the rows.
    // Returns null only when no row at all was returned — a genuine "no
    // footballer matches this name," never a swallowed failure (see this
    // method's own doc comment on IWikidataClient for the full
    // error-contract reasoning).
    private static WikidataPlayerCareerLookupResult? ParsePlayerCareerAndNationalityByNameBindings(SparqlResponse? response)
    {
        var bindings = response?.Results?.Bindings;
        if (bindings is null || bindings.Count == 0)
            return null;

        string? wikidataQid = null;
        string? fullName = null;
        string? nationality = null;
        var clubNames = new HashSet<string>();

        foreach (var binding in bindings)
        {
            if (wikidataQid is null && binding.TryGetValue("player", out var playerValue) && !string.IsNullOrEmpty(playerValue.Value))
                wikidataQid = playerValue.Value.Split('/').Last();

            if (fullName is null && binding.TryGetValue("playerLabel", out var labelValue) && !string.IsNullOrWhiteSpace(labelValue.Value))
                fullName = labelValue.Value;

            if (nationality is null && binding.TryGetValue("nationalityLabel", out var nationalityValue) && !string.IsNullOrWhiteSpace(nationalityValue.Value))
                nationality = nationalityValue.Value;

            // Bug fix (2026-08-08, REQ-509/510): a club is recorded whenever
            // ?clubLabel is bound AT ALL — deliberately NOT gated on
            // ?startTime also being bound (the original bug: not every real
            // P54 statement carries a P580 start-time qualifier, and gating
            // on it silently dropped those clubs — see
            // WikidataPlayerCareerLookupResult's own doc comment for the
            // full "why"). ?startTime/?endTime/?numberOfMatches are still
            // OPTIONAL-fetched by the query for parity with
            // QueryPlayerCareerStintsByQidsAsync's shape, but this method's
            // Clubs never needed them (only ClubName is ever read by
            // AdminSuggestionEndpoints/CommitPlayerDataRequest.Clubs), so
            // they're intentionally left unparsed here.
            if (binding.TryGetValue("clubLabel", out var clubLabelValue) && !string.IsNullOrWhiteSpace(clubLabelValue.Value))
                clubNames.Add(clubLabelValue.Value);
        }

        // wikidataQid/fullName absent means the name-match subquery itself
        // never bound ?player — no footballer matches this name at all.
        if (wikidataQid is null || fullName is null)
            return null;

        return new WikidataPlayerCareerLookupResult(wikidataQid, fullName, nationality, clubNames.ToList());
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

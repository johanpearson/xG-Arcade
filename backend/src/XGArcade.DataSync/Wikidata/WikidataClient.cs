using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace XGArcade.DataSync.Wikidata;

// SPARQL against Wikidata Query Service — a fundamentally different shape
// from the REST clients elsewhere in DataSync.Clients (implementation-
// document.md §6a). Injected via HttpClient with BaseAddress
// https://query.wikidata.org/ (see Program.cs's AddHttpClient registration).
public partial class WikidataClient(
    HttpClient httpClient,
    TimeSpan? queryTimeout = null,
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
        string countryWikidataQid,
        string clubWikidataQid,
        CancellationToken cancellationToken = default)
    {
        if (!QidPattern().IsMatch(countryWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{countryWikidataQid}'", nameof(countryWikidataQid));
        if (!QidPattern().IsMatch(clubWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{clubWikidataQid}'", nameof(clubWikidataQid));

        var query = BuildCountryClubIntersectionQuery(countryWikidataQid, clubWikidataQid);
        return await RunIntersectionQueryAsync("country-club", countryWikidataQid, clubWikidataQid, query, cancellationToken);
    }

    public async Task<IReadOnlyList<WikidataPlayerMatch>> QueryClubClubIntersectionAsync(
        string clubAWikidataQid,
        string clubBWikidataQid,
        CancellationToken cancellationToken = default)
    {
        if (!QidPattern().IsMatch(clubAWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{clubAWikidataQid}'", nameof(clubAWikidataQid));
        if (!QidPattern().IsMatch(clubBWikidataQid))
            throw new ArgumentException($"Not a valid Wikidata QID: '{clubBWikidataQid}'", nameof(clubBWikidataQid));

        var query = BuildClubClubIntersectionQuery(clubAWikidataQid, clubBWikidataQid);
        return await RunIntersectionQueryAsync("club-club", clubAWikidataQid, clubBWikidataQid, query, cancellationToken);
    }

    private async Task<IReadOnlyList<WikidataPlayerMatch>> RunIntersectionQueryAsync(
        string queryKind, string qidA, string qidB, string query, CancellationToken cancellationToken)
    {
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

            return ParseBindings(parsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Timeout — treated as "no match," never surfaced as a failure.
            // REQ-103: falls through to the API-Football fallback (Tier 1)
            // or the combination is discarded (REQ-101), same as a genuine miss.
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

    // No LIMIT — non-negotiable, see implementation-document.md §6a: the
    // result set IS the cell's complete answer key. Fetches skos:altLabel
    // in the same query so aliases cost nothing extra (REQ-208's alias
    // value, free). P106 = occupation (association football player),
    // P27 = country of citizenship, P54 = member of sports team, P21 = sex
    // or gender, P569 = date of birth (ADR-0025's male-only/born-1939-
    // or-later player pool restriction — REQ-112).
    //
    // P54 deliberately uses the full statement path (p:P54/ps:P54,
    // excluding only deprecated rank), NOT the truthy wdt:P54 shortcut the
    // other properties use — do not "simplify" it back. Wikidata's truthy
    // wdt: graph contains only best-rank statements: the moment any P54
    // statement on a player is marked preferred rank (editors routinely
    // mark the *current* club preferred), every normal-rank historical
    // club silently vanishes from wdt:P54. That turned "ever played for
    // this club" into "currently plays for this club" for exactly those
    // players (e.g. Sandro Tonali x AC Milan), leaving the persisted
    // answer key incomplete and correct guesses scored incorrect
    // (REQ-113's ever-played-for semantics, REQ-101/REQ-203's correctness
    // contract). Both grid generation and REQ-211's guess-time live
    // lookup route through these two builders, so the statement path
    // covers both. P106/P27/P21/P569 stay truthy on purpose: for those,
    // best-rank semantics match product intent (current citizenship, the
    // best-supported date of birth) and the preferred-rank trap doesn't
    // change the answer to the question being asked.
    private static string BuildCountryClubIntersectionQuery(string countryQid, string clubQid) => $$"""
        SELECT ?player ?playerLabel ?alias WHERE {
          ?player wdt:P106 wd:Q937857.
          ?player wdt:P27 wd:{{countryQid}}.
          ?player p:P54 ?clubStatement.
          ?clubStatement ps:P54 wd:{{clubQid}}.
          MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
          ?player wdt:P21 wd:{{MaleWikidataQid}}.
          ?player wdt:P569 ?dateOfBirth.
          FILTER(?dateOfBirth >= "{{DateOfBirthCutoff}}"^^xsd:dateTime)
          OPTIONAL {
            ?player skos:altLabel ?alias.
            FILTER(LANG(?alias) = "en")
          }
          SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
        }
        """;

    // S-030: "ever played for both clubs" — P54 checked twice instead of
    // once against P27, same no-LIMIT/altLabel-in-one-query/male-only/
    // born-1939-or-later rules as above, and the same
    // full-statement-path-not-truthy P54 rule (see the comment on
    // BuildCountryClubIntersectionQuery for why wdt:P54 is wrong here).
    // Two distinct statement variables, one per club — a single shared
    // variable could never bind (one statement can't point at two clubs).
    private static string BuildClubClubIntersectionQuery(string clubAQid, string clubBQid) => $$"""
        SELECT ?player ?playerLabel ?alias WHERE {
          ?player wdt:P106 wd:Q937857.
          ?player p:P54 ?clubAStatement.
          ?clubAStatement ps:P54 wd:{{clubAQid}}.
          MINUS { ?clubAStatement wikibase:rank wikibase:DeprecatedRank. }
          ?player p:P54 ?clubBStatement.
          ?clubBStatement ps:P54 wd:{{clubBQid}}.
          MINUS { ?clubBStatement wikibase:rank wikibase:DeprecatedRank. }
          ?player wdt:P21 wd:{{MaleWikidataQid}}.
          ?player wdt:P569 ?dateOfBirth.
          FILTER(?dateOfBirth >= "{{DateOfBirthCutoff}}"^^xsd:dateTime)
          OPTIONAL {
            ?player skos:altLabel ?alias.
            FILTER(LANG(?alias) = "en")
          }
          SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
        }
        """;

    public async Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolPageAsync(
        int offset, int pageSize, CancellationToken cancellationToken = default)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "offset must not be negative.");
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "pageSize must be positive.");

        var query = BuildPlayerPoolPageQuery(offset, pageSize);
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

            return ParseNameIndexBindings(parsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Timeout on a single page — treated as an empty page, same
            // "never throws" contract as the intersection queries. The
            // importer's own doc comment covers why it can't fully
            // distinguish this from "no more pages" and what it does about it.
            _logger.LogWarning(
                "Wikidata player-pool page query timed out at offset {Offset}; treating as empty page.", offset);
            return [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex,
                "Wikidata player-pool page query failed at offset {Offset}; treating as empty page.", offset);
            return [];
        }
    }

    // S-032/ADR-0007: broad "association football player" pool query, no
    // country/club filter — unlike the two intersection queries above, this
    // DOES page (inner SELECT DISTINCT ?player ... LIMIT/OFFSET) because the
    // unfiltered result set is far larger than WDQS can safely return in one
    // request. The outer query re-joins ?player against birth date/
    // citizenship/image so a player with more than one P27 citizenship or
    // P18 image produces more than one result row for the same ?player —
    // ParseNameIndexBindings groups by qid the same way ParseBindings above
    // groups aliases, taking the first non-null value seen for each. Same
    // male-only/born-1939-or-later filter as the intersection queries
    // (ADR-0025/REQ-112) for player-pool consistency. Deliberately no P54
    // (club) — that's PlayerAttribute's job, not this index's (ADR-0007).
    private static string BuildPlayerPoolPageQuery(int offset, int pageSize) => $$"""
        SELECT ?player ?playerLabel ?birthYear ?countryLabel ?image WHERE {
          {
            SELECT DISTINCT ?player WHERE {
              ?player wdt:P106 wd:Q937857.
              ?player wdt:P21 wd:{{MaleWikidataQid}}.
              ?player wdt:P569 ?dateOfBirth.
              FILTER(?dateOfBirth >= "{{DateOfBirthCutoff}}"^^xsd:dateTime)
            }
            ORDER BY ?player
            LIMIT {{pageSize}}
            OFFSET {{offset}}
          }
          ?player wdt:P569 ?dateOfBirth.
          BIND(YEAR(?dateOfBirth) AS ?birthYear)
          OPTIONAL { ?player wdt:P27 ?country. }
          OPTIONAL { ?player wdt:P18 ?image. }
          SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
        }
        """;

    private static IReadOnlyList<WikidataNameIndexEntry> ParseNameIndexBindings(SparqlResponse? response)
    {
        if (response?.Results?.Bindings is null)
            return [];

        var byQid = new Dictionary<string, (string FullName, int? BirthYear, string? Nationality, string? PhotoUrl)>();

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
                entry = (label, birthYear, null, null);
            }

            // A player with more than one citizenship/image produces more
            // than one binding row — keep the first non-null value seen for
            // each, rather than overwriting with a later (possibly blank)
            // one.
            if (entry.Nationality is null && binding.TryGetValue("countryLabel", out var countryValue)
                && !string.IsNullOrWhiteSpace(countryValue.Value))
                entry.Nationality = countryValue.Value;

            if (entry.PhotoUrl is null && binding.TryGetValue("image", out var imageValue)
                && !string.IsNullOrWhiteSpace(imageValue.Value))
                entry.PhotoUrl = imageValue.Value;

            byQid[qid] = entry;
        }

        return byQid
            .Select(kv => new WikidataNameIndexEntry(kv.Key, kv.Value.FullName, kv.Value.BirthYear, kv.Value.Nationality, kv.Value.PhotoUrl))
            .ToList();
    }

    private static IReadOnlyList<WikidataPlayerMatch> ParseBindings(SparqlResponse? response)
    {
        if (response?.Results?.Bindings is null)
            return [];

        var byQid = new Dictionary<string, (string FullName, HashSet<string> Aliases)>();

        foreach (var binding in response.Results.Bindings)
        {
            if (!binding.TryGetValue("player", out var playerValue) || string.IsNullOrEmpty(playerValue.Value))
                continue;

            var qid = playerValue.Value.Split('/').Last();

            if (!byQid.TryGetValue(qid, out var entry))
            {
                var label = binding.TryGetValue("playerLabel", out var labelValue) ? labelValue.Value : qid;
                entry = (label, []);
                byQid[qid] = entry;
            }

            if (binding.TryGetValue("alias", out var aliasValue) && !string.IsNullOrWhiteSpace(aliasValue.Value))
                entry.Aliases.Add(aliasValue.Value);
        }

        return byQid.Select(kv => new WikidataPlayerMatch(kv.Key, kv.Value.FullName, kv.Value.Aliases.ToList())).ToList();
    }

    [GeneratedRegex(@"^Q\d+$")]
    private static partial Regex QidPattern();

    private sealed record SparqlResponse([property: JsonPropertyName("results")] SparqlResults? Results);

    private sealed record SparqlResults([property: JsonPropertyName("bindings")] List<Dictionary<string, SparqlValue>>? Bindings);

    private sealed record SparqlValue([property: JsonPropertyName("value")] string Value);
}

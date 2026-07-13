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
    private static string BuildCountryClubIntersectionQuery(string countryQid, string clubQid) => $$"""
        SELECT ?player ?playerLabel ?alias WHERE {
          ?player wdt:P106 wd:Q937857.
          ?player wdt:P27 wd:{{countryQid}}.
          ?player wdt:P54 wd:{{clubQid}}.
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
    // born-1939-or-later rules as above.
    private static string BuildClubClubIntersectionQuery(string clubAQid, string clubBQid) => $$"""
        SELECT ?player ?playerLabel ?alias WHERE {
          ?player wdt:P106 wd:Q937857.
          ?player wdt:P54 wd:{{clubAQid}}.
          ?player wdt:P54 wd:{{clubBQid}}.
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

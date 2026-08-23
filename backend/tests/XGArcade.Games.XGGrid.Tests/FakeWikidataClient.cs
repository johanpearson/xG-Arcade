using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid.Tests;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as FakeWikidataLookupService in this same
// test project). GridGameModule only ever calls QueryPlayerPhotoByNameAsync
// directly on IWikidataClient (REQ-216/ADR-0057's wrong-guess photo-by-name
// lookup) — every other intersection/batch query on this interface is routed
// through IWikidataLookupService instead (FakeWikidataLookupService), so
// every other method here is a trivial empty/null stub, never exercised by
// any GridGameModuleTests case.
internal sealed class FakeWikidataClient : IWikidataClient
{
    private readonly Dictionary<string, WikidataPlayerPhotoLookupResult> _resultsByName = new(StringComparer.OrdinalIgnoreCase);
    private int _remainingFailures;

    public List<string> QueriedNames { get; } = [];

    public void SetResult(string playerName, string fullName, string? photoUrl = null) =>
        _resultsByName[playerName] = new WikidataPlayerPhotoLookupResult(fullName, photoUrl);

    // REQ-216/ADR-0057: the next `calls` calls to QueryPlayerPhotoByNameAsync
    // throw WikidataQueryException instead of returning a result — simulates
    // a timeout/HTTP/parse failure, which GridGameModule.ResolveWrongGuessPlayerAsync
    // must catch and turn into a silent null (never fail-closed).
    public void FailNextCalls(int calls) => _remainingFailures = calls;

    public Task<WikidataPlayerPhotoLookupResult?> QueryPlayerPhotoByNameAsync(
        string playerName, CancellationToken cancellationToken = default)
    {
        QueriedNames.Add(playerName);

        if (_remainingFailures > 0)
        {
            _remainingFailures--;
            throw new WikidataQueryException($"simulated WDQS failure for wrong-guess photo lookup of '{playerName}'");
        }

        var result = _resultsByName.TryGetValue(playerName, out var configured) ? configured : null;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
        string countryWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryNationalTeamClubIntersectionAsync(
        string nationalTeamWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryClubClubIntersectionAsync(
        string clubAWikidataQid, string clubBWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyCountryIntersectionAsync(
        string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyClubIntersectionAsync(
        string trophyWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    // ADR-0061: GridGameModule routes every Trophy live lookup through
    // IWikidataLookupService (FakeWikidataLookupService), never directly
    // through IWikidataClient — same "trivial stub, never exercised" note as
    // every other intersection method in this file.
    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyCountryIntersectionAsync(
        string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyNationalTeamIntersectionAsync(
        string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyClubIntersectionAsync(
        string trophyWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyNationalTeamIntersectionAsync(
        string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolBirthYearAsync(
        int birthYear, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

    public Task<IReadOnlyDictionary<string, string>> QueryPlayerPhotosByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    public Task<IReadOnlyDictionary<string, PlayerPositionBirthYearEntry>> QueryPlayerPositionsAndBirthYearsByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, PlayerPositionBirthYearEntry>>(new Dictionary<string, PlayerPositionBirthYearEntry>());

    public Task<IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>>> QueryPlayerCareerStintsByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>>>(
            new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>());

    public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByNationalityAsync(
        string nationalityWikidataQid, bool useCountryForSportProperty, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

    // ADR-0069: GridGameModule never calls this (it's PlayerCareerPrefetchService's
    // own prefetch-time method, not part of grid generation or guess-scoring)
    // — a trivial stub, same as QueryPlayerPoolByNationalityAsync above.
    public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByClubAsync(
        string clubWikidataQid, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

    public Task<IReadOnlyDictionary<string, int>> QuerySitelinkCountsByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

    // REQ-509/510 (S-090): GridGameModule never calls this (it's
    // AdminSuggestionEndpoints' admin-lookup method, not part of grid
    // generation or guess-scoring) — a trivial stub, same as every other
    // method in this file besides QueryPlayerPhotoByNameAsync above.
    public Task<WikidataPlayerCareerLookupResult?> QueryPlayerCareerAndNationalityByNameAsync(
        string playerName, CancellationToken cancellationToken = default) =>
        Task.FromResult<WikidataPlayerCareerLookupResult?>(null);

    // REQ-513 (GitHub issue #239): GridGameModule never calls this (it's
    // AdminEndpoints' single-player refresh action, not part of grid
    // generation or guess-scoring) — a trivial stub, same as every other
    // method in this file besides QueryPlayerPhotoByNameAsync above.
    public Task<WikidataPlayerRefreshData> QueryPlayerRefreshDataByQidAsync(
        string wikidataQid, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WikidataPlayerRefreshData(null, null, null, null));
}

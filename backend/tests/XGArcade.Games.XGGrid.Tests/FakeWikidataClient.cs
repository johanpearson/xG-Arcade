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

    public Task<IReadOnlyDictionary<string, int>> QuerySitelinkCountsByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
}

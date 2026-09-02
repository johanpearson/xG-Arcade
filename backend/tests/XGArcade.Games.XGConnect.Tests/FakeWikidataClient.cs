using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGConnect.Tests;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as XGArcade.Games.XGGrid.Tests's own
// FakeWikidataClient and XGArcade.DataSync.Tests.Wikidata's own
// FakeWikidataClient). PlayerCareerOverlapService only ever calls
// QueryPlayerCareerStintsByQidsAsync directly on IWikidataClient — every
// other method on this interface is a trivial empty/null/zero stub, never
// exercised by any PlayerCareerOverlapServiceTests case.
internal sealed class FakeWikidataClient : IWikidataClient
{
    private readonly Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>> _careerStintsByQid = new();
    private int _remainingCareerStintBatchFailures;

    // Every batch queried, in call order, as the exact QID list passed in —
    // lets a test assert both the batch size and which QIDs were grouped
    // together (or NOT grouped together — e.g. "only the player needing
    // refresh, not the one already cached").
    public List<IReadOnlyList<string>> QueriedCareerStintBatches { get; } = [];

    public void SetCareerStints(string wikidataQid, params WikidataCareerStintEntry[] stints) =>
        _careerStintsByQid[wikidataQid] = stints;

    // The next `batches` calls to QueryPlayerCareerStintsByQidsAsync throw
    // WikidataQueryException instead of returning a result — simulates a
    // timeout/HTTP/parse failure, which PlayerCareerOverlapService must
    // translate into LiveLookupUnavailableException, never swallow.
    public void FailNextCareerStintBatches(int batches) => _remainingCareerStintBatchFailures = batches;

    public Task<IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>>> QueryPlayerCareerStintsByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default)
    {
        QueriedCareerStintBatches.Add(wikidataQids);

        if (_remainingCareerStintBatchFailures > 0)
        {
            _remainingCareerStintBatchFailures--;
            throw new WikidataQueryException("simulated WDQS failure for a player career-stint batch");
        }

        IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>> result = wikidataQids
            .Where(qid => _careerStintsByQid.ContainsKey(qid))
            .ToDictionary(qid => qid, qid => _careerStintsByQid[qid]);

        return Task.FromResult(result);
    }

    // ---- Every other IWikidataClient method: trivial stubs, never touched
    // ---- by PlayerCareerOverlapService/PlayerCareerOverlapServiceTests ----

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

    public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByNationalityAsync(
        string nationalityWikidataQid, bool useCountryForSportProperty, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

    public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByClubAsync(
        string clubWikidataQid, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

    public Task<RecentClubTransferLookupResult> QueryRecentClubTransfersAsync(
        string clubWikidataQid, string clubName, DateTime sinceUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RecentClubTransferLookupResult(
            new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>(), new Dictionary<string, string>()));

    public Task<IReadOnlyDictionary<string, int>> QuerySitelinkCountsByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

    public Task<WikidataPlayerPhotoLookupResult?> QueryPlayerPhotoByNameAsync(
        string playerName, CancellationToken cancellationToken = default) =>
        Task.FromResult<WikidataPlayerPhotoLookupResult?>(null);

    public Task<WikidataPlayerCareerLookupResult?> QueryPlayerCareerAndNationalityByNameAsync(
        string playerName, CancellationToken cancellationToken = default) =>
        Task.FromResult<WikidataPlayerCareerLookupResult?>(null);

    public Task<WikidataPlayerRefreshData> QueryPlayerRefreshDataByQidAsync(
        string wikidataQid, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WikidataPlayerRefreshData(null, null, null, null));
}

using XGArcade.DataSync.Wikidata;

namespace XGArcade.TestSupport;

// Hand-rolled IWikidataClient fake (docs/coding-guidelines.md: no mocking
// framework). Promoted here (quality-architect follow-up, S-245 review)
// once a near-verbatim copy of this exact class was about to land for the
// 5th time overall and the 3rd time within XGArcade.Api.Tests alone
// (AdminEndpointTests.cs, AdminSuggestionEndpointTests.cs, and the new
// SeedGuessableRoundWithMissingClubEndpointTests.cs all had their own
// private copy) — the same rule-of-three trigger FixedTimeProvider.cs's own
// doc comment describes, and docs/coding-guidelines.md's Code health budget
// section is explicit that a diff shouldn't wait for a fifth copy before
// extracting.
//
// Exposes BOTH configurable behaviors those three call sites actually used
// — AdminEndpointTests.cs only ever configures the refresh-by-QID path
// (SetRefreshData/FailNextRefreshLookups), AdminSuggestionEndpointTests.cs
// and SeedGuessableRoundWithMissingClubEndpointTests.cs only ever configure
// the career/nationality-by-name path (SetCareerLookup/FailNextCareerLookups)
// — so every existing call site keeps exactly the configuration surface it
// already used; neither behavior interferes with the other since they're
// independent dictionaries/counters. XGArcade.Games.XGGrid.Tests and
// XGArcade.DataSync.Tests keep their own separate FakeWikidataClient copies
// deliberately (different assemblies, no InternalsVisibleTo wired to this
// project, and their own configurable surfaces diverge further from this
// one — see each of those files' own header comments) — not folded in here.
//
// Every other IWikidataClient member below is a trivial stub purely to
// satisfy the interface — none of AdminEndpoints/AdminSuggestionEndpoints/
// the seed-guessable-round-with-missing-club endpoint ever call them.
public sealed class FakeWikidataClient : IWikidataClient
{
    private readonly Dictionary<string, WikidataPlayerCareerLookupResult> _careerLookupByName = new();
    private int _remainingCareerLookupFailures;

    private readonly Dictionary<string, WikidataPlayerRefreshData> _refreshDataByQid = new();
    private int _remainingRefreshFailures;

    public void SetCareerLookup(string playerName, WikidataPlayerCareerLookupResult result) =>
        _careerLookupByName[playerName] = result;

    public void FailNextCareerLookups(int calls) => _remainingCareerLookupFailures = calls;

    public void SetRefreshData(string wikidataQid, WikidataPlayerRefreshData data) => _refreshDataByQid[wikidataQid] = data;

    public void FailNextRefreshLookups(int calls) => _remainingRefreshFailures = calls;

    public Task<WikidataPlayerCareerLookupResult?> QueryPlayerCareerAndNationalityByNameAsync(
        string playerName, CancellationToken cancellationToken = default)
    {
        if (_remainingCareerLookupFailures > 0)
        {
            _remainingCareerLookupFailures--;
            throw new WikidataQueryException("simulated WDQS failure for admin career/nationality lookup");
        }

        var result = _careerLookupByName.TryGetValue(playerName, out var configured) ? configured : null;
        return Task.FromResult(result);
    }

    public Task<WikidataPlayerRefreshData> QueryPlayerRefreshDataByQidAsync(
        string wikidataQid, CancellationToken cancellationToken = default)
    {
        if (_remainingRefreshFailures > 0)
        {
            _remainingRefreshFailures--;
            throw new WikidataQueryException("simulated WDQS failure for admin player refresh");
        }

        var result = _refreshDataByQid.TryGetValue(wikidataQid, out var configured)
            ? configured
            : new WikidataPlayerRefreshData(null, null, null, null);
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
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>>>(new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>());

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

    public Task<IReadOnlyDictionary<string, WikidataInternationalStatsEntry>> QueryInternationalStatsByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, WikidataInternationalStatsEntry>>(new Dictionary<string, WikidataInternationalStatsEntry>());

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> QueryIndividualTrophyStatsByQidsAsync(
        IReadOnlyList<string> playerWikidataQids, IReadOnlyList<string> trophyWikidataQids, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(new Dictionary<string, IReadOnlyList<string>>());

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> QueryTeamTrophyStatsByQidsAsync(
        IReadOnlyList<string> playerWikidataQids, IReadOnlyList<string> trophyWikidataQids, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(new Dictionary<string, IReadOnlyList<string>>());
}

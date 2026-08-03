using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as FakeHttpMessageHandler/
// FakeWikidataLookupService elsewhere in this repo).
// QueryPlayerPoolBirthYearAsync and QueryPlayerPhotosByQidsAsync are
// configurable (PlayerNameIndexImporterTests and
// PlayerPhotoBackfillServiceTests respectively); the two intersection-query
// methods are never touched by either caller, so they stay stubbed to an
// empty result. An unconfigured year returns [] (a genuinely empty year, per
// the real method's contract); FailFor scripts WikidataQueryException throws
// before (or instead of) success, mirroring the real method's fail-loud
// contract. Same shape for photo batches: an unconfigured QID is simply
// absent from the result (a real "no P18 statement"), and
// FailNextPhotoBatches scripts a whole-call WikidataQueryException.
internal sealed class FakeWikidataClient : IWikidataClient
{
    private readonly Dictionary<int, IReadOnlyList<WikidataNameIndexEntry>> _entriesByYear = new();
    private readonly Dictionary<int, int> _remainingFailuresByYear = new();
    private readonly Dictionary<int, CancellationTokenSource> _cancelCallerTokenByYear = new();

    // REQ-214 backfill (S-045): QueryPlayerPhotosByQidsAsync support.
    // Configured per-QID (SetPhoto), plus one shared "fail the next N
    // calls" counter — PlayerPhotoBackfillServiceTests only needs "this
    // whole batch call fails," never a per-QID failure, since the real
    // method's error contract is call-level (an HTTP/timeout/parse failure
    // fails the whole batch, not individual QIDs within it).
    private readonly Dictionary<string, string> _photosByQid = new();
    private int _remainingBatchFailures;

    // Every batch queried, in call order, as the exact QID list passed in —
    // lets a test assert both the batch size and which QIDs were grouped
    // together.
    public List<IReadOnlyList<string>> QueriedPhotoBatches { get; } = [];

    public void SetPhoto(string wikidataQid, string photoUrl) => _photosByQid[wikidataQid] = photoUrl;

    // The next `batches` calls to QueryPlayerPhotosByQidsAsync throw
    // WikidataQueryException instead of returning a result.
    public void FailNextPhotoBatches(int batches) => _remainingBatchFailures = batches;

    public Task<IReadOnlyDictionary<string, string>> QueryPlayerPhotosByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default)
    {
        QueriedPhotoBatches.Add(wikidataQids);

        if (_remainingBatchFailures > 0)
        {
            _remainingBatchFailures--;
            throw new WikidataQueryException("simulated WDQS failure for a player-photo batch");
        }

        IReadOnlyDictionary<string, string> result = wikidataQids
            .Where(qid => _photosByQid.ContainsKey(qid))
            .ToDictionary(qid => qid, qid => _photosByQid[qid]);

        return Task.FromResult(result);
    }

    // REQ-1207 backfill (bug-bundle fix, 2026-08-02): QueryPlayerPositionsAndBirthYearsByQidsAsync
    // support — same "configured per-QID, plus one shared fail-next-N-calls
    // counter" shape as the photo-batch support above.
    private readonly Dictionary<string, PlayerPositionBirthYearEntry> _positionBirthYearByQid = new();
    private int _remainingPositionBirthYearBatchFailures;

    public List<IReadOnlyList<string>> QueriedPositionBirthYearBatches { get; } = [];

    public void SetPositionBirthYear(string wikidataQid, string? position, int? birthYear) =>
        _positionBirthYearByQid[wikidataQid] = new PlayerPositionBirthYearEntry(position, birthYear);

    public void FailNextPositionBirthYearBatches(int batches) => _remainingPositionBirthYearBatchFailures = batches;

    public Task<IReadOnlyDictionary<string, PlayerPositionBirthYearEntry>> QueryPlayerPositionsAndBirthYearsByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default)
    {
        QueriedPositionBirthYearBatches.Add(wikidataQids);

        if (_remainingPositionBirthYearBatchFailures > 0)
        {
            _remainingPositionBirthYearBatchFailures--;
            throw new WikidataQueryException("simulated WDQS failure for a player-position/birth-year batch");
        }

        IReadOnlyDictionary<string, PlayerPositionBirthYearEntry> result = wikidataQids
            .Where(qid => _positionBirthYearByQid.ContainsKey(qid))
            .ToDictionary(qid => qid, qid => _positionBirthYearByQid[qid]);

        return Task.FromResult(result);
    }

    // ADR-0054: QueryPlayerCareerStintsByQidsAsync support — same
    // "configured per-QID, plus one shared fail-next-N-calls counter" shape
    // as the photo/position-birth-year batch support above.
    private readonly Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>> _careerStintsByQid = new();
    private int _remainingCareerStintBatchFailures;

    public List<IReadOnlyList<string>> QueriedCareerStintBatches { get; } = [];

    public void SetCareerStints(string wikidataQid, params WikidataCareerStintEntry[] stints) =>
        _careerStintsByQid[wikidataQid] = stints;

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

    // ADR-0055: QueryPlayerPoolByNationalityAsync support — same
    // "configured per-QID, plus one shared fail-next-N-calls counter" shape
    // as every other batch-style method above.
    private readonly Dictionary<string, IReadOnlyList<WikidataNameIndexEntry>> _poolByNationalityQid = new();
    private int _remainingNationalityPoolFailures;

    public List<string> QueriedNationalityQids { get; } = [];
    public List<bool> QueriedUsesCountryForSportProperty { get; } = [];

    public void SetPoolForNationality(string nationalityQid, IReadOnlyList<WikidataNameIndexEntry> pool) =>
        _poolByNationalityQid[nationalityQid] = pool;

    public void FailNextNationalityPoolCalls(int calls) => _remainingNationalityPoolFailures = calls;

    public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByNationalityAsync(
        string nationalityWikidataQid, bool useCountryForSportProperty, CancellationToken cancellationToken = default)
    {
        QueriedNationalityQids.Add(nationalityWikidataQid);
        QueriedUsesCountryForSportProperty.Add(useCountryForSportProperty);

        if (_remainingNationalityPoolFailures > 0)
        {
            _remainingNationalityPoolFailures--;
            throw new WikidataQueryException($"simulated WDQS failure for nationality {nationalityWikidataQid}");
        }

        var pool = _poolByNationalityQid.TryGetValue(nationalityWikidataQid, out var configured) ? configured : [];
        return Task.FromResult(pool);
    }

    // ADR-0056: QuerySitelinkCountsByQidsAsync support — same "configured
    // per-QID, plus one shared fail-next-N-calls counter" shape as every
    // other batch-style method above. Never actually exercised by
    // PlayerNameIndexImporterTests/PlayerPhotoBackfillServiceTests (same
    // "never touched by either caller" reasoning as the intersection-query
    // stubs below) — added only so this fake still satisfies IWikidataClient's
    // signature. PlayerFamiliarityServiceTests gets its own dedicated fake,
    // same precedent as PlayerCareerStintRefreshServiceTests.
    private readonly Dictionary<string, int> _sitelinkCountsByQid = new();
    private int _remainingSitelinkBatchFailures;

    public List<IReadOnlyList<string>> QueriedSitelinkBatches { get; } = [];

    public void SetSitelinkCount(string wikidataQid, int count) => _sitelinkCountsByQid[wikidataQid] = count;

    public void FailNextSitelinkBatches(int batches) => _remainingSitelinkBatchFailures = batches;

    public Task<IReadOnlyDictionary<string, int>> QuerySitelinkCountsByQidsAsync(
        IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default)
    {
        QueriedSitelinkBatches.Add(wikidataQids);

        if (_remainingSitelinkBatchFailures > 0)
        {
            _remainingSitelinkBatchFailures--;
            throw new WikidataQueryException("simulated WDQS failure for a sitelink-count batch");
        }

        IReadOnlyDictionary<string, int> result = wikidataQids
            .Where(qid => _sitelinkCountsByQid.ContainsKey(qid))
            .ToDictionary(qid => qid, qid => _sitelinkCountsByQid[qid]);

        return Task.FromResult(result);
    }

    // Every year queried, in call order (a retried year appears once per attempt).
    public List<int> QueriedYears { get; } = [];

    public int CallCountFor(int year) => QueriedYears.Count(y => y == year);

    public void SetYear(int year, IReadOnlyList<WikidataNameIndexEntry> entries) => _entriesByYear[year] = entries;

    // The first `attempts` calls for this year throw WikidataQueryException;
    // pass int.MaxValue for a year that never succeeds.
    public void FailFor(int year, int attempts) => _remainingFailuresByYear[year] = attempts;

    // Simulates the caller's own token being cancelled (Ctrl+C, host
    // shutdown) while this year's query is in flight: cancels `source` and
    // throws an OCE carrying its token — the real client's contract for
    // caller cancellation, as opposed to FailFor's WikidataQueryException
    // (a query failure). The importer must treat these two very differently.
    public void CancelCallerTokenWhileQuerying(int year, CancellationTokenSource source) =>
        _cancelCallerTokenByYear[year] = source;

    // onTechnicalFailure/timeoutTier (REQ-110): never exercised by
    // PlayerNameIndexImporterTests/PlayerPhotoBackfillServiceTests — added
    // only so this fake still satisfies IWikidataClient's signature, same
    // "never touched by either caller" reasoning as throwOnTimeout below.
    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
        string countryWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    // REQ-114/ADR-0035: never touched by PlayerNameIndexImporterTests/
    // PlayerPhotoBackfillServiceTests, same "never touched by either
    // caller" reasoning as the other intersection methods below — stays
    // stubbed to an empty result.
    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryNationalTeamClubIntersectionAsync(
        string nationalTeamWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryClubClubIntersectionAsync(
        string clubAWikidataQid, string clubBWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    // S-031/REQ-108: neither Trophy intersection is touched by
    // PlayerNameIndexImporterTests/PlayerPhotoBackfillServiceTests, same
    // "never touched by either caller" reasoning as the two intersection
    // methods above — stays stubbed to an empty result.
    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyCountryIntersectionAsync(
        string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyClubIntersectionAsync(
        string trophyWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolBirthYearAsync(
        int birthYear, CancellationToken cancellationToken = default)
    {
        QueriedYears.Add(birthYear);

        if (_cancelCallerTokenByYear.TryGetValue(birthYear, out var source))
        {
            source.Cancel();
            throw new OperationCanceledException(source.Token);
        }

        if (_remainingFailuresByYear.TryGetValue(birthYear, out var remaining) && remaining > 0)
        {
            _remainingFailuresByYear[birthYear] = remaining - 1;
            throw new WikidataQueryException($"simulated WDQS failure for birth year {birthYear}");
        }

        var entries = _entriesByYear.TryGetValue(birthYear, out var configured) ? configured : [];
        return Task.FromResult(entries);
    }

    // REQ-216/ADR-0057: QueryPlayerPhotoByNameAsync support — never touched
    // by PlayerNameIndexImporterTests/PlayerPhotoBackfillServiceTests (same
    // "never touched by either caller" reasoning as the intersection-query
    // stubs above), added only so this fake still satisfies IWikidataClient's
    // signature. GridGameModuleTests gets its own dedicated fake, same
    // precedent as PlayerFamiliarityServiceTests/PlayerCareerStintRefreshServiceTests.
    public Task<WikidataPlayerPhotoLookupResult?> QueryPlayerPhotoByNameAsync(
        string playerName, CancellationToken cancellationToken = default) =>
        Task.FromResult<WikidataPlayerPhotoLookupResult?>(null);
}

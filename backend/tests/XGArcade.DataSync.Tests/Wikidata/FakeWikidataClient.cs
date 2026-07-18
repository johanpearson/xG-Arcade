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

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
        string countryWikidataQid, string clubWikidataQid, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryClubClubIntersectionAsync(
        string clubAWikidataQid, string clubBWikidataQid, CancellationToken cancellationToken = default) =>
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
}

using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as FakeHttpMessageHandler/
// FakeWikidataLookupService elsewhere in this repo). Only
// QueryPlayerPoolBirthYearAsync is configurable — PlayerNameIndexImporterTests
// is the only current caller, and it never touches the intersection-query
// methods. An unconfigured year returns [] (a genuinely empty year, per the
// real method's contract); FailFor scripts WikidataQueryException throws
// before (or instead of) success, mirroring the real method's fail-loud
// contract.
internal sealed class FakeWikidataClient : IWikidataClient
{
    private readonly Dictionary<int, IReadOnlyList<WikidataNameIndexEntry>> _entriesByYear = new();
    private readonly Dictionary<int, int> _remainingFailuresByYear = new();

    // Every year queried, in call order (a retried year appears once per attempt).
    public List<int> QueriedYears { get; } = [];

    public int CallCountFor(int year) => QueriedYears.Count(y => y == year);

    public void SetYear(int year, IReadOnlyList<WikidataNameIndexEntry> entries) => _entriesByYear[year] = entries;

    // The first `attempts` calls for this year throw WikidataQueryException;
    // pass int.MaxValue for a year that never succeeds.
    public void FailFor(int year, int attempts) => _remainingFailuresByYear[year] = attempts;

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

        if (_remainingFailuresByYear.TryGetValue(birthYear, out var remaining) && remaining > 0)
        {
            _remainingFailuresByYear[birthYear] = remaining - 1;
            throw new WikidataQueryException($"simulated WDQS failure for birth year {birthYear}");
        }

        var entries = _entriesByYear.TryGetValue(birthYear, out var configured) ? configured : [];
        return Task.FromResult(entries);
    }
}

using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as FakeHttpMessageHandler/
// FakeWikidataLookupService elsewhere in this repo). Only QueryPlayerPoolPageAsync
// is configurable — PlayerNameIndexImporterTests is the only current caller,
// and it never touches the intersection-query methods.
internal sealed class FakeWikidataClient : IWikidataClient
{
    private readonly Dictionary<int, IReadOnlyList<WikidataNameIndexEntry>> _pagesByOffset = new();

    public int CallCount { get; private set; }

    public void SetPage(int offset, IReadOnlyList<WikidataNameIndexEntry> entries) => _pagesByOffset[offset] = entries;

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
        string countryWikidataQid, string clubWikidataQid, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataPlayerMatch>> QueryClubClubIntersectionAsync(
        string clubAWikidataQid, string clubBWikidataQid, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

    public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolPageAsync(
        int offset, int pageSize, CancellationToken cancellationToken = default)
    {
        CallCount++;
        var page = _pagesByOffset.TryGetValue(offset, out var entries) ? entries : [];
        return Task.FromResult(page);
    }
}

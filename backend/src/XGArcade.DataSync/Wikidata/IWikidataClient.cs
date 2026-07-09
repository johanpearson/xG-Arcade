namespace XGArcade.DataSync.Wikidata;

// COMP-07 (DataSync.Clients), Tier 0 half: the Wikidata half of ADR-0011's
// live-lookup waterfall. Tier 0 grids are Country x Club only (MVP-SCOPE.md),
// so this is scoped to that specific intersection rather than a generic
// n-category query — Trophy support is Tier 1.
public interface IWikidataClient
{
    // Never LIMITs the underlying SPARQL query — see implementation-document.md
    // §6a: the result set IS the cell's complete answer key. Returns an empty
    // list (never throws) on timeout, HTTP error, or genuinely no match.
    Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
        string countryWikidataQid,
        string clubWikidataQid,
        CancellationToken cancellationToken = default);
}

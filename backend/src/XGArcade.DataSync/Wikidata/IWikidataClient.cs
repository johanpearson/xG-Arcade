namespace XGArcade.DataSync.Wikidata;

// COMP-07 (DataSync.Clients), Tier 0 half: the Wikidata half of ADR-0011's
// live-lookup waterfall. Tier 0 grids are Country x Club and, as of
// docs/backlog.md S-030, Club x Club (MVP-SCOPE.md) — so this is scoped to
// those two intersections rather than a generic n-category query — Trophy
// support is Tier 1.
public interface IWikidataClient
{
    // Never LIMITs the underlying SPARQL query — see implementation-document.md
    // §6a: the result set IS the cell's complete answer key. Returns an empty
    // list (never throws) on timeout, HTTP error, or genuinely no match.
    Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
        string countryWikidataQid,
        string clubWikidataQid,
        CancellationToken cancellationToken = default);

    // S-030: "ever played for both clubs" — same P54-based "any point in
    // their career" semantics as QueryCountryClubIntersectionAsync's P54
    // half, just checked twice against two different clubs instead of once
    // against a P27 citizenship. Same no-LIMIT/never-throws contract.
    Task<IReadOnlyList<WikidataPlayerMatch>> QueryClubClubIntersectionAsync(
        string clubAWikidataQid,
        string clubBWikidataQid,
        CancellationToken cancellationToken = default);
}

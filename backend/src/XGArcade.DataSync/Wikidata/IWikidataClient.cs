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

    // S-032/ADR-0007/REQ-207: PlayerNameIndexImporter's bulk-import query —
    // "association football player" (P106=Q937857) broadly, no country/club
    // filter, unlike the two intersection queries above. Unlike those (which
    // never LIMIT — the result set is the cell's complete answer key), this
    // query DOES page — this result set is far larger (many thousands of
    // rows) than WDQS can safely return in one request, so the importer
    // calls this repeatedly with increasing offset until a page comes back
    // empty. Same male-only/born-1939-or-later filter as the intersection
    // queries (ADR-0025/REQ-112) for player-pool consistency. Deliberately
    // does not fetch P54 (club) data — that's PlayerAttribute's job, not
    // this index's (ADR-0007). Same no-LIMIT-on-a-per-page-basis-means-
    // never-throws contract as the intersection queries: returns an empty
    // page on timeout/HTTP error, which the importer must not mistake for
    // "no more pages" vs. "transient failure" — see PlayerNameIndexImporter's
    // own doc comment for how it handles that ambiguity.
    Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolPageAsync(
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default);
}

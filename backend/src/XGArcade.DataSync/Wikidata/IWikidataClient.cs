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
    // filter, unlike the two intersection queries above. Sliced by BIRTH
    // YEAR (revised 2026-07-18): one call fetches every eligible player born
    // in the given year via a bounded one-year P569 window, with no
    // ORDER BY/LIMIT/OFFSET — the original LIMIT/OFFSET paging forced WDQS
    // to sort the entire unfiltered pool per page and hit its hard ~60s
    // server-side timeout on every single page (NOTES.md 2026-07-18). Same
    // male-only/born-1939-or-later filter as the intersection queries
    // (ADR-0025/REQ-112); deliberately does not fetch P54 (club) data —
    // that's PlayerAttribute's job, not this index's (ADR-0007).
    //
    // Error contract — deliberately the OPPOSITE of the intersection
    // queries above: throws WikidataQueryException on timeout/HTTP/parse
    // failure instead of returning []. An empty list means exactly "no
    // eligible players born this year" (real for sparse early years), never
    // a swallowed failure — the old swallow-to-[] contract made a timeout
    // indistinguishable from end-of-data, and the import job exited 0
    // having imported nothing. The importer retries a failed slice and
    // fails the whole run loudly if it keeps failing.
    Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolBirthYearAsync(
        int birthYear,
        CancellationToken cancellationToken = default);
}

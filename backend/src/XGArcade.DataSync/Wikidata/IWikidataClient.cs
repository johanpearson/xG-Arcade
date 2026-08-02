namespace XGArcade.DataSync.Wikidata;

// COMP-07 (DataSync.Clients), Tier 0 half: the Wikidata half of ADR-0011's
// live-lookup waterfall. Tier 0 grids are Country x Club, Club x Club (as of
// docs/backlog.md S-030), and a Trophy-involving pairing (as of S-031,
// REQ-108, individual awards only) (MVP-SCOPE.md) — so this is scoped to
// those intersections rather than a generic n-category query. Team-
// competition trophies (World Cup, Champions League) need a structurally
// different query (squad membership + tournament result) and remain
// deferred to a follow-up story.
public interface IWikidataClient
{
    // Never LIMITs the underlying SPARQL query — see implementation-document.md
    // §6a: the result set IS the cell's complete answer key. Returns an empty
    // list (never throws) on timeout, HTTP error, or genuinely no match —
    // UNLESS throwOnTimeout is true, in which case a timeout specifically
    // throws WikidataQueryException instead (see that parameter's own doc
    // comment below). HTTP/parse errors still always swallow to [],
    // regardless of throwOnTimeout.
    //
    // throwOnTimeout (REQ-211, 2026-07-27 fix): defaults to false, which
    // preserves this method's original swallow-to-[] contract completely
    // unchanged for grid generation (REQ-103's "never block grid generation
    // on a Wikidata failure," ADR-0011) — every existing caller is
    // unaffected. Set to true ONLY by REQ-211's guess-time fallback
    // (WikidataLookupOrigin.GuessTimeFallback), which needs to distinguish
    // "Wikidata timed out, this cell's correctness is genuinely unknown"
    // from "Wikidata answered and found no match" — the two were
    // indistinguishable before this fix, so a slow-but-correct guess (e.g.
    // "Clarence Seedorf" for Ajax x AC Milan) could be wrongly scored
    // incorrect and consume a real REQ-210 attempt. This follows the same
    // "swallowing would be semantically wrong here" precedent
    // QueryPlayerPoolBirthYearAsync/QueryPlayerPhotosByQidsAsync already
    // established below, just made conditional per-call instead of
    // per-method, since this method (unlike those two) has a legitimate
    // caller on each side of the distinction.
    // onTechnicalFailure (REQ-110, 2026-07-28): an optional, purely-additive
    // observation hook — invoked (with no arguments) exactly when this call
    // ends in a technical failure that the throwOnTimeout=false path still
    // swallows to [] (a WDQS timeout, an HTTP error, or a JSON parse error;
    // see RunIntersectionQueryAsync's own catch blocks in WikidataClient).
    // Never invoked for a genuine "queried successfully, zero real matches"
    // outcome — that distinction is exactly the point. Deliberately a
    // trailing optional parameter (default null) rather than a return-type
    // change (e.g. wrapping the match list): every existing caller of these
    // five methods — REQ-103 grid generation and REQ-211's guess-time
    // fallback via WikidataLookupService, plus every assertion in
    // WikidataClientTests.cs — reads the bare match list today, and this
    // keeps all of that completely untouched. Only PlayerCacheWarmingService
    // (via WikidataLookupService.LookupAndPersistAsync/
    // LookupAndPersistClubClubAsync) supplies a non-null hook, to build its
    // run summary's technical-failure count/list — see that class's own doc
    // comment. Not invoked on the throwOnTimeout=true timeout path (that
    // path throws WikidataQueryException instead, which is itself an
    // observable failure signal — see throwOnTimeout's own doc comment
    // below).
    // timeoutTier (REQ-110, 2026-07-28 "cache-warming-specific timeout"
    // extension): a second, independent trailing optional parameter, same
    // additive/default-preserves-behavior shape as onTechnicalFailure above.
    // Selects which of WikidataClient's three timeout budgets applies —
    // see WikidataQueryTimeoutTier's own doc comment for the full "why a
    // second selector alongside throwOnTimeout" reasoning. Defaults to
    // WikidataQueryTimeoutTier.Default, which resolves exactly as this
    // method always has (throwOnTimeout picks between the 15s/28s budgets);
    // only PlayerCacheWarmingService (via WikidataLookupService) passes
    // WikidataQueryTimeoutTier.CacheWarming, always alongside
    // throwOnTimeout: false — cache warming's fail-open/swallow contract is
    // unaffected by this parameter, it only changes how long the client
    // waits before swallowing. This is the intersection method
    // PlayerCacheWarmingService's Country x Club loop actually passes
    // WikidataQueryTimeoutTier.CacheWarming to (via
    // WikidataLookupService.LookupAndPersistAsync) — or, for a national-team
    // country row, QueryNationalTeamClubIntersectionAsync below instead.
    Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
        string countryWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default);

    // REQ-114/ADR-0035: the P1532 ("country for sport") counterpart of
    // QueryCountryClubIntersectionAsync's P27 ("country of citizenship")
    // query — for England/Scotland/Wales/Northern Ireland, none of which
    // are sovereign states and so can't be queried via P27 the way every
    // other seeded country can (their players' P27 is uniformly United
    // Kingdom). A second query path, not a replacement: callers pick
    // between this and QueryCountryClubIntersectionAsync per
    // `CountryDefinition.UsesCountryForSportProperty`
    // (WikidataLookupService), never both for the same row. Same P54
    // full-statement-path club-membership half and no-LIMIT/never-throws
    // contract as every other intersection query in this interface.
    // onTechnicalFailure: see QueryCountryClubIntersectionAsync's own doc
    // comment — same purely-additive, default-null observation hook.
    // timeoutTier: see QueryCountryClubIntersectionAsync's own doc comment
    // — same purely-additive, default-preserves-behavior selector.
    Task<IReadOnlyList<WikidataPlayerMatch>> QueryNationalTeamClubIntersectionAsync(
        string nationalTeamWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default);

    // S-030: "ever played for both clubs" — same P54-based "any point in
    // their career" semantics as QueryCountryClubIntersectionAsync's P54
    // half, just checked twice against two different clubs instead of once
    // against a P27 citizenship. Same no-LIMIT/never-throws-unless-
    // throwOnTimeout contract as QueryCountryClubIntersectionAsync — see
    // that method's own doc comment for throwOnTimeout.
    // onTechnicalFailure: see QueryCountryClubIntersectionAsync's own doc
    // comment — same purely-additive, default-null observation hook.
    // timeoutTier: see QueryCountryClubIntersectionAsync's own doc comment
    // — same purely-additive, default-preserves-behavior selector. This is
    // the intersection method PlayerCacheWarmingService's Club x Club loop
    // actually passes WikidataQueryTimeoutTier.CacheWarming to (via
    // WikidataLookupService.LookupAndPersistClubClubAsync).
    Task<IReadOnlyList<WikidataPlayerMatch>> QueryClubClubIntersectionAsync(
        string clubAWikidataQid,
        string clubBWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default);

    // S-031/REQ-108: "received this individual award AND holds this
    // citizenship" — P166 ("award received") + P27, the Trophy counterpart
    // of QueryCountryClubIntersectionAsync's P27+P54 shape. Uses the truthy
    // wdt:P166 shortcut (unlike P54) — see BuildIntersectionQuery's Trophy
    // comment in WikidataClient for why that's safe here. Same
    // no-LIMIT/never-throws-unless-throwOnTimeout contract as
    // QueryCountryClubIntersectionAsync above.
    // onTechnicalFailure: see QueryCountryClubIntersectionAsync's own doc
    // comment — same purely-additive, default-null observation hook. Not
    // currently wired up by any caller (REQ-110's cache-warming path doesn't
    // cover Trophy pairings), added for interface symmetry with the other
    // four intersection methods rather than special-casing this one.
    // timeoutTier: same interface-symmetry reasoning — see
    // QueryCountryClubIntersectionAsync's own doc comment. Not currently
    // passed anything but Default by any caller, same as onTechnicalFailure.
    Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyCountryIntersectionAsync(
        string trophyWikidataQid,
        string countryWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default);

    // S-031/REQ-108: "received this individual award AND ever played for
    // this club" — P166 (truthy) + P54 (full statement path, same
    // non-truthy reasoning as QueryCountryClubIntersectionAsync/
    // QueryClubClubIntersectionAsync's P54 halves). Same
    // no-LIMIT/never-throws-unless-throwOnTimeout contract.
    // onTechnicalFailure: see QueryCountryClubIntersectionAsync's own doc
    // comment — same purely-additive, default-null observation hook, added
    // for interface symmetry (same rationale as
    // QueryTrophyCountryIntersectionAsync's own comment).
    // timeoutTier: same interface-symmetry reasoning as
    // QueryTrophyCountryIntersectionAsync's own comment.
    Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyClubIntersectionAsync(
        string trophyWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default);

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

    // REQ-214 backfill (S-045): PlayerPhotoBackfillService's batched,
    // direct-by-QID lookup for `Player` rows that predate the P18 addition
    // to the two intersection queries above
    // (PlayerStoreRepository.GetOrCreatePlayersByWikidataQidAsync only ever
    // sets PhotoUrl at row-creation time, never on a later lookup — see
    // Player.cs's own comment — so every Player created before REQ-214
    // shipped has PhotoUrl permanently NULL with no code-path that will
    // ever revisit it). A fundamentally different query shape from the
    // intersection queries above: a SPARQL
    // `VALUES` clause over a bounded batch of QIDs
    // (`SELECT ?player ?photo WHERE { VALUES ?player { wd:Q1 wd:Q2 ... }
    // OPTIONAL { ?player wdt:P18 ?photo. } }`), not a candidate-matching
    // intersection — callers are expected to keep each batch within the
    // "few-thousand-row, no ORDER BY/LIMIT/OFFSET" bounded-query class
    // implementation-document.md §6a already establishes as safe on WDQS
    // (PlayerPhotoBackfillService.BatchSize = 200). Returns a dictionary
    // keyed by QID, present only for QIDs that resolved to a photo — a QID
    // with no P18 statement, or one WDQS didn't return a row for at all, is
    // simply absent from the result, never an error.
    //
    // Error contract — same as QueryPlayerPoolBirthYearAsync above, NOT the
    // intersection queries' swallow-to-[] contract: this is a batch job
    // whose success metric is a row/backfill count
    // (docs/coding-guidelines.md's 2026-07-18 error-handling guideline,
    // promoted from the S-032 incident), so a swallowed failure here would
    // be indistinguishable from "none of these players have a photo" and
    // silently under-backfill forever. Throws WikidataQueryException on
    // timeout/HTTP/parse failure instead.
    Task<IReadOnlyDictionary<string, string>> QueryPlayerPhotosByQidsAsync(
        IReadOnlyList<string> wikidataQids,
        CancellationToken cancellationToken = default);

    // REQ-1207 backfill (bug-bundle fix, 2026-08-02): PlayerPositionBirthYearBackfillService's
    // batched, direct-by-QID lookup — the exact mirror of
    // QueryPlayerPhotosByQidsAsync above, just for P413 ("position played on
    // team / speciality")/P569 ("date of birth") instead of P18. Same reason
    // this exists at all: PlayerStoreRepository
    // .GetOrCreatePlayersByWikidataQidAsync only ever sets Position/BirthYear
    // at row-creation time (REQ-1207's own "set once, never overwritten"
    // contract, Player.cs's own comment), so every Player row created before
    // the P413/P569 bindings were added to the intersection queries
    // (migration 20260727140000_AddPlayerPositionAndBirthYear) has both
    // permanently null with no other code path that will ever revisit them.
    // Same VALUES-clause-over-a-bounded-QID-batch shape as
    // QueryPlayerPhotosByQidsAsync (`SELECT ?player ?position ?dateOfBirth
    // WHERE { VALUES ?player { wd:Q1 wd:Q2 ... }
    // OPTIONAL { ?player wdt:P413 ?position. }
    // OPTIONAL { ?player wdt:P569 ?dateOfBirth. } }`), not a candidate-
    // matching intersection — callers are expected to keep each batch within
    // the same "few-thousand-row, no ORDER BY/LIMIT/OFFSET" bounded-query
    // class as every other query in this interface
    // (PlayerPositionBirthYearBackfillService.BatchSize, mirroring
    // PlayerPhotoBackfillService.BatchSize = 200). Returns a dictionary
    // keyed by QID, present only for QIDs where at least one of
    // Position/BirthYear resolved — a QID with neither is simply absent from
    // the result, same "absent, never an error" contract as
    // QueryPlayerPhotosByQidsAsync.
    //
    // Error contract — same as QueryPlayerPhotosByQidsAsync above (throw
    // WikidataQueryException on timeout/HTTP/parse failure, not the
    // intersection queries' swallow-to-[] contract): this is a batch job
    // whose success metric is a backfilled-row count, so a swallowed failure
    // would be indistinguishable from "none of these QIDs have this data."
    Task<IReadOnlyDictionary<string, PlayerPositionBirthYearEntry>> QueryPlayerPositionsAndBirthYearsByQidsAsync(
        IReadOnlyList<string> wikidataQids,
        CancellationToken cancellationToken = default);
}

namespace XGArcade.DataSync.Wikidata;

// COMP-07 (DataSync.Clients), Tier 0 half: the Wikidata half of ADR-0011's
// live-lookup waterfall. Tier 0 grids are Country x Club, Club x Club (as of
// docs/backlog.md S-030), and a Trophy-involving pairing (as of S-031,
// REQ-108, individual awards; extended to team competitions by ADR-0061)
// (MVP-SCOPE.md) — so this is scoped to those intersections rather than a
// generic n-category query.
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

    // ADR-0061: team-competition trophies (FIFA World Cup, UEFA Champions
    // League) have no P166-equivalent "trophies won" statement on a player
    // item — winning a team competition is a fact about a squad and a
    // specific tournament edition, not the player item itself (see that
    // ADR's "What Wikidata actually models" section). This is a three-hop
    // join instead of a single joining property: the player's own P1344
    // ("participant of") edition, the edition's P3450 ("sports season of
    // league or competition") membership in the trophy's series, and the
    // edition's P1346 ("winner") matching the target country (via the
    // winner's own P1532, "country for sport" — a P1346 value for the World
    // Cup is a national-team item, e.g. "Brazil national football team",
    // never the country item itself). Player-side P27 ("country of
    // citizenship") — the team-trophy counterpart of
    // QueryTrophyCountryIntersectionAsync above, for every ordinary
    // sovereign-state country. Same
    // no-LIMIT/never-throws-unless-throwOnTimeout contract as every other
    // intersection query in this interface.
    // onTechnicalFailure/timeoutTier: see QueryCountryClubIntersectionAsync's
    // own doc comment — same purely-additive, default-preserving shape.
    Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyCountryIntersectionAsync(
        string trophyWikidataQid,
        string countryWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default);

    // ADR-0061: the P1532 ("country for sport") player-side counterpart of
    // QueryTeamTrophyCountryIntersectionAsync above, for England/Scotland/
    // Wales/Northern Ireland — mirrors how QueryNationalTeamClubIntersectionAsync
    // (P1532) is the ADR-0035 counterpart of QueryCountryClubIntersectionAsync
    // (P27). The winner-side join stays P1532 either way (see
    // QueryTeamTrophyCountryIntersectionAsync's own comment) — this method
    // only changes which property identifies the PLAYER's side of the
    // match; do not collapse the two into one branch, they answer genuinely
    // different questions ("holds this citizenship" vs. "represented this
    // country for sport"), same as every other P27-vs-P1532 split in this
    // file. Same no-LIMIT/never-throws-unless-throwOnTimeout contract.
    // onTechnicalFailure/timeoutTier: see QueryCountryClubIntersectionAsync's
    // own doc comment.
    Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyNationalTeamIntersectionAsync(
        string trophyWikidataQid,
        string countryWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default);

    // ADR-0061: the Club counterpart of QueryTeamTrophyCountryIntersectionAsync
    // above — no player-side branch needed, a club's identity is unambiguous,
    // unlike a country's citizenship-vs-represented split. Keeps the P54
    // club-membership clause (full statement path, same non-negotiable "ever
    // played for," not "currently plays for," reasoning as every other P54
    // use in this file) ALONGSIDE the P1344/P3450/P1346 edition-winner join,
    // not instead of it — P1344 alone ("participated in this edition") is
    // true for every player on every club that reached that edition, not
    // just the winning squad; requiring club membership too narrows this
    // back down to "played for the specific club that won it." A
    // best-effort narrowing, not a guarantee — see ADR-0061's Consequences
    // section for the known residual gap this doesn't solve (season/date
    // qualifier matching between P54 and the edition's own year). The
    // trophy's edition winner is matched directly against
    // ClubDefinition.WikidataQid — a club competition's winner item IS the
    // club item, no P1532-style indirection needed here (unlike the country
    // variants above). Same no-LIMIT/never-throws-unless-throwOnTimeout
    // contract.
    // onTechnicalFailure/timeoutTier: see QueryCountryClubIntersectionAsync's
    // own doc comment.
    Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyClubIntersectionAsync(
        string trophyWikidataQid,
        string clubWikidataQid,
        bool throwOnTimeout = false,
        CancellationToken cancellationToken = default,
        Action? onTechnicalFailure = null,
        WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default);

    // Judgment call, not part of ADR-0061's own three-method list (see
    // NOTES.md's 2026-08-09 entry for the full reasoning): ADR-0035's
    // follow-up note flagged that LookupAndPersistTrophyCountryAsync didn't
    // honor CountryDefinition.UsesCountryForSportProperty "in general," not
    // only for the team-trophy branch ADR-0061 adds. Closing that gap for
    // the EXISTING individual-award P166 path (S-031) needs its own P1532
    // player-side counterpart too, since QueryTrophyCountryIntersectionAsync
    // has no such method to dispatch to — same P166 (truthy) + P1532
    // (truthy) shape as QueryTrophyCountryIntersectionAsync's P166+P27,
    // mirroring how QueryNationalTeamClubIntersectionAsync mirrors
    // QueryCountryClubIntersectionAsync. Do not confuse this with
    // QueryTeamTrophyNationalTeamIntersectionAsync above — that one is the
    // team-competition (P1344/P3450/P1346) shape for a flagged country; this
    // one is the individual-award (P166) shape for a flagged country. Same
    // no-LIMIT/never-throws-unless-throwOnTimeout contract.
    // onTechnicalFailure/timeoutTier: see QueryCountryClubIntersectionAsync's
    // own doc comment.
    Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyNationalTeamIntersectionAsync(
        string trophyWikidataQid,
        string countryWikidataQid,
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

    // ADR-0054: xG Path's own direct career fetch — the FULL, unrestricted
    // P54 club-membership history for a batch of QIDs, unlike every other
    // query in this interface. The five intersection queries above only ever
    // learn a player's career-stint qualifiers for the ONE club they happen
    // to be scoped to (country/nationality x club, club x club, trophy x
    // club) as a side effect of xG Grid's own lookups (ADR-0042) — a player's
    // PlayerCareerStint set was, until this method existed, never more
    // complete than "whatever clubs xG Grid happened to query about so far."
    // This method exists specifically to let xG Path refresh a puzzle's
    // target player with their real, complete career, independent of xG
    // Grid's lookup history.
    //
    // Same VALUES-clause-over-a-bounded-QID-batch shape as
    // QueryPlayerPhotosByQidsAsync/QueryPlayerPositionsAndBirthYearsByQidsAsync
    // — not a candidate-matching intersection, no male/date-of-birth/
    // occupation filter (same reasoning as those two: every QID in the batch
    // is already a real Player row this codebase created via a query that DID
    // apply those filters at the time). Unlike those two, though, this one
    // DOES need the full P54 statement-path treatment (p:P54/ps:P54, MINUS
    // deprecated rank) the intersection queries' BuildCountryClubIntersectionQuery
    // comment explains at length — the truthy wdt:P54 shortcut only returns a
    // player's best-rank (often "current club") statement, which would make a
    // "full career" fetch just as incomplete as the byproduct it's meant to
    // replace. Do not simplify this to wdt:P54.
    //
    // Returns a dictionary keyed by QID, present only for QIDs with at least
    // one non-deprecated P54 statement whose P580 ("start time") qualifier
    // resolved — a QID with no career data at all (or only qualifier-less
    // statements) is simply absent from the result, never an error, same
    // "absent means none" contract as QueryPlayerPhotosByQidsAsync.
    //
    // Error contract — same as QueryPlayerPhotosByQidsAsync/
    // QueryPlayerPositionsAndBirthYearsByQidsAsync (throw
    // WikidataQueryException on timeout/HTTP/parse failure, not the
    // intersection queries' swallow-to-[] contract): the caller
    // (PlayerCareerStintRefreshService) is responsible for deciding that a
    // failed refresh must never block xG Path round generation — see that
    // class's own doc comment — but this client method itself must not
    // silently conflate "Wikidata has no career data for this QID" with "the
    // query failed," the same reasoning QueryPlayerPhotosByQidsAsync's own
    // doc comment gives.
    Task<IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>>> QueryPlayerCareerStintsByQidsAsync(
        IReadOnlyList<string> wikidataQids,
        CancellationToken cancellationToken = default);

    // ADR-0055: PlayerCareerPrefetchService's per-country pool query — the
    // nationality-scoped counterpart of QueryPlayerPoolBirthYearAsync's
    // birth-year-scoped one (S-032/ADR-0007). Same bounded-query shape (no
    // ORDER BY/LIMIT/OFFSET) and same reuse of WikidataNameIndexEntry/
    // ParseNameIndexBindings — this is still "a broad player-pool scan,"
    // just sliced by a different, also-bounded axis. useCountryForSportProperty
    // selects P1532 vs. P27, mirroring QueryNationalTeamClubIntersectionAsync/
    // QueryCountryClubIntersectionAsync's own split (ADR-0035) — callers
    // pass `CountryDefinition.UsesCountryForSportProperty`, never both for
    // the same country.
    //
    // Error contract — same throw-on-failure shape as
    // QueryPlayerPoolBirthYearAsync/QueryPlayerCareerStintsByQidsAsync: this
    // is a batch job whose success metric is a fetched-pool count, so a
    // swallowed failure would be indistinguishable from "this country
    // genuinely has zero eligible players" (never actually true for a
    // seeded country, but the client has no way to know that).
    //
    // Unverified from this sandbox whether a single large country's
    // unsliced pool (e.g. Brazil, England — potentially thousands of players
    // across the full 1939-present eligible range) stays safely inside
    // WDQS's ~60s server-side cap the way a single birth-year slice does —
    // flagged in ADR-0055 as an open risk, not assumed safe. The caller
    // (PlayerCareerPrefetchService) treats a single country's failure as
    // recoverable (log, continue with the remaining countries, fail the run
    // loudly at the end) for exactly this reason — see that class's own doc
    // comment.
    Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByNationalityAsync(
        string nationalityWikidataQid,
        bool useCountryForSportProperty,
        CancellationToken cancellationToken = default);

    // ADR-0069: PlayerCareerPrefetchService's per-club pool query — the
    // club-scoped sibling of QueryPlayerPoolByNationalityAsync above, added
    // to widen the prefetch candidate pool beyond seeded countries (ADR-0055
    // deliberately scoped move (3) to nationality only; this extends it, per
    // a fresh product decision — see ADR-0055's own "For AI agents" section
    // for why that widening needed one). Same return type and
    // ParseNameIndexBindings parser as QueryPlayerPoolByNationalityAsync —
    // this is still "a broad player-pool scan," just sliced by club
    // membership (P54) instead of nationality (P27/P1532).
    //
    // Correctness-critical: P54 MUST use the full statement path
    // (p:P54/ps:P54, excluding deprecated rank), never the truthy wdt:P54
    // shortcut — see IntersectionQuerySpecs.BuildCountryClubIntersectionQuery's
    // own comment for the full "current club marked preferred rank hides
    // historical clubs" reasoning. This query's whole point is "everyone who
    // EVER played for this club," so getting this wrong would silently
    // narrow the pool to the club's current squad — see ADR-0069 for the
    // full "why this matters here too" writeup.
    //
    // Error contract — same throw-on-failure shape as
    // QueryPlayerPoolByNationalityAsync: this is a batch job whose success
    // metric is a fetched-pool count, so a swallowed failure would be
    // indistinguishable from "this club genuinely has zero eligible
    // players" (never actually true for a seeded club, but the client has no
    // way to know that). The caller (PlayerCareerPrefetchService) treats a
    // single club's failure as recoverable (log, continue with the
    // remaining clubs, fail the run loudly at the end), same as its existing
    // per-country handling.
    Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByClubAsync(
        string clubWikidataQid,
        CancellationToken cancellationToken = default);

    // ADR-0056: xG Path's familiarity signal — Wikipedia sitelink count per
    // QID, the same VALUES-clause-over-a-bounded-QID-batch shape as
    // QueryPlayerPhotosByQidsAsync/QueryPlayerPositionsAndBirthYearsByQidsAsync.
    // `wikibase:sitelinks` is WDQS's own computed predicate, not a P-number
    // statement — see WikidataClient.QuerySitelinkCountsByQidsAsync's own doc
    // comment for the full reasoning, including why this exists at all
    // (REQ-1201's eligibility check had no fame/recognizability signal
    // whatsoever before this — any player with 3+ documented stints and one
    // seeded-club stint could be picked, regardless of how obscure).
    //
    // Returns a dictionary keyed by QID, present only for QIDs whose
    // sitelink count actually resolved — a QID absent from the result is
    // "unknown," never "confirmed 0," same "absent means no data, never an
    // error" contract as QueryPlayerPhotosByQidsAsync.
    //
    // Error contract — same as QueryPlayerPhotosByQidsAsync/
    // QueryPlayerPositionsAndBirthYearsByQidsAsync/
    // QueryPlayerCareerStintsByQidsAsync (throw WikidataQueryException on
    // timeout/HTTP/parse failure): the caller
    // (PathEligibilityService.GetEligiblePlayerIdsAsync) is responsible for
    // deciding that a failed familiarity check must never block round
    // generation (REQ-103's established reasoning) — this client method must
    // not silently conflate "the query failed" with "this player has 0
    // sitelinks."
    Task<IReadOnlyDictionary<string, int>> QuerySitelinkCountsByQidsAsync(
        IReadOnlyList<string> wikidataQids,
        CancellationToken cancellationToken = default);

    // REQ-216/ADR-0057: a NAME-based counterpart to
    // QueryPlayerPhotosByQidsAsync, for a wrong-but-real guess that has no
    // WikidataQid to look up by yet (no existing Player row) — the only
    // signal available is the raw guess string itself, already confirmed
    // by the caller (GridGameModule) to match a real PlayerNameIndex
    // candidate (ADR-0007). This is its own distinct, lower-priority
    // trigger, separate from REQ-211's correctness-critical live lookup —
    // never called for anything but this cosmetic, already-known-wrong-guess
    // display case, and never routed through any API-Football client or
    // ExternalApiUsage threshold (ADR-0057's whole point).
    //
    // Matches a footballer (P106=Q937857) by exact case-insensitive label OR
    // alias — case-insensitive because the caller only reaches this method
    // after a PlayerNameIndex match on PlayerNameNormalizer's normalized
    // form, not necessarily identical casing to Wikidata's own label.
    // Deliberately LIMIT 1, unlike every other query in this file
    // (implementation-document.md §6a's "never LIMIT, the result set IS the
    // answer key" rule applies to grid generation/scoring only): this is a
    // single cosmetic display lookup for one already-known-wrong guess, not
    // an answer key — more than one same-named footballer existing on
    // Wikidata is a real but rare case this doesn't need to disambiguate,
    // since any one of them is equally "a real footballer with this name"
    // for REQ-216's purposes.
    //
    // Returns null when the query found no matching player at all (a
    // PlayerNameIndex hit that Wikidata's own live data no longer confirms —
    // rare, but possible) or when the matched player has no P18 photo
    // statement — both are ordinary, error-free outcomes.
    //
    // Error contract — throws WikidataQueryException on timeout/HTTP/parse
    // failure, the same as QueryPlayerPhotosByQidsAsync, rather than
    // swallowing to null: this keeps the client's own honest signal
    // ("the query failed" vs. "the query succeeded and found nothing")
    // available to the caller, exactly as ADR-0057 requires — but the
    // caller (GridGameModule.ResolveWrongGuessPlayerAsync) is the one
    // responsible for catching this and turning it into a silent null,
    // never a fail-closed/incorrect outcome (there is no correctness verdict
    // left to compute for a guess already known to be wrong).
    Task<WikidataPlayerPhotoLookupResult?> QueryPlayerPhotoByNameAsync(
        string playerName,
        CancellationToken cancellationToken = default);

    // REQ-509/REQ-510 (S-090): the admin-review live lookup — "occupation
    // P106, citizenship P27, club membership P54," per those REQs' own
    // acceptance criteria, run by player name (an admin reviewing a
    // suggestion, or searching directly, has a name to start from — never a
    // WikidataQid, so this can't reuse QueryPlayerCareerStintsByQidsAsync's
    // by-QID batch shape). Candidate-player selection is deliberately LIMIT-1
    // on the CANDIDATE PLAYER, not on result rows overall — see
    // BuildPlayerCareerAndNationalityByNameQuery's own comment for how that's
    // combined with P27/P54 below without truncating a real multi-club career
    // to one row.
    //
    // ADR-0062 (2026-08-09): candidate selection uses a federated
    // `SERVICE wikibase:mwapi` `EntitySearch` call (Wikidata's own indexed
    // search, the same engine behind its search box) re-filtered to
    // footballers (P106=Q937857), NOT QueryPlayerPhotoByNameAsync's
    // rdfs:label/skos:altLabel scan — that raw label/alias shape was an
    // unindexed, population-wide scan expensive enough to trigger a
    // production HTTP 502 from WDQS's own gateway (see that ADR's Context
    // section). QueryPlayerPhotoByNameAsync itself is unchanged and still
    // uses the label/alias shape (a possible future follow-up per ADR-0062,
    // not part of this change) — do not assume the two by-name lookups
    // share an identical matching mechanism going forward.
    //
    // Combined with P27's citizenship label and P54's FULL statement-path
    // club-membership history (p:P54/ps:P54, MINUS deprecated rank — the
    // same non-negotiable "ever played for," not "currently plays for" shape
    // QueryPlayerCareerStintsByQidsAsync's own doc comment explains at
    // length; do not simplify to the truthy wdt:P54 shortcut).
    //
    // Returns null when no footballer (P106=Q937857) matches playerName via
    // the EntitySearch candidate lookup at all — a genuine "Wikidata has no
    // record of this name" (or nothing search-relevant enough to surface
    // among the top-ranked candidates re-filtered to footballers), never a
    // swallowed failure (that distinction is exactly what throwing below is
    // for). A matched player with no P27/P54 data at all still returns a
    // non-null result with Nationality null and Clubs empty — both are
    // independently optional, same "absent means none, never an error"
    // contract as every other OPTIONAL-bound field in this file.
    //
    // Error contract — always throws WikidataQueryException on timeout/HTTP/
    // parse failure, the SAME as every other name/QID-based lookup in this
    // interface EXCEPT the five swallow-to-[] intersection-query methods
    // above (QueryCountryClubIntersectionAsync et al. — whose swallow
    // contract exists only because REQ-103/ADR-0011 must never block grid
    // generation on a Wikidata failure). This method has no such "never
    // block" caller: it is a brand-new, single-purpose, admin-triggered
    // action (REQ-509/510), not REQ-211's per-guess fallback, so there is no
    // throwOnTimeout parameter here — swallowing this method's failure to
    // "no data" would violate REQ-509's own explicit acceptance criterion
    // ("a query that fails to complete is reported to the admin as 'lookup
    // unavailable, try again' — it is never silently treated as 'no data
    // found'", ADR-0046's timeout-vs-no-match distinction applied here
    // without exception). The caller (AdminSuggestionEndpoints) catches this
    // and returns HTTP 503, the same shape GuessEndpoints.cs already uses for
    // GuessSubmissionOutcome.LiveLookupUnavailable.
    Task<WikidataPlayerCareerLookupResult?> QueryPlayerCareerAndNationalityByNameAsync(
        string playerName,
        CancellationToken cancellationToken = default);
}

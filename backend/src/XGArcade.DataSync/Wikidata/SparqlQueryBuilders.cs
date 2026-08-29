using System.Globalization;

namespace XGArcade.DataSync.Wikidata;

// S-155 (docs/backlog.md, Epic 17): every SPARQL query-building method
// WikidataClient's public IWikidataClient methods call into — moved out of
// WikidataClient.cs unchanged (only the containing class changed; see
// WikidataClientTests.cs's byte-for-byte SPARQL assertions for the
// regression proof this didn't change a single character of any generated
// query). CODE_HEALTH_ASSESSMENT.md's 2026-08-11 revision scored
// WikidataClient.cs 2.5/10 for breadth (S-118/S-124, Epic 9, already fixed
// the duplication half by consolidating every HTTP-handling block behind
// RunIntersectionQueryAsync/RunThrowingQueryAsync); this file is the
// query-building half of the recommended split, SparqlResponseParsers.cs
// the parsing half — both stateless and dependency-free, as the original
// recommendation intended. internal, not private: WikidataClient.cs's thin
// wrapper methods call these from a different class in the same
// namespace/assembly; nothing outside XGArcade.DataSync needs them, and no
// test reaches them directly (WikidataClientTests.cs only ever constructs
// WikidataClient and calls its public IWikidataClient methods, asserting on
// the SPARQL sent to a FakeHttpMessageHandler — see that file). Does NOT
// include the 9 intersection-query candidate-clause builders
// (BuildCountryClubIntersectionQuery etc.) — those already live in
// IntersectionQuerySpecs.cs as of S-100/S-101 and are untouched by this
// story.
internal static class SparqlQueryBuilders
{
    // ADR-0025: Tier 0's player pool is restricted to male footballers born
    // in 1939 or later — Q6581097 is Wikidata's "male" item for P21 (sex or
    // gender). A fixed date, not a rolling window relative to "now" — a
    // deliberate, one-time product decision, not a moving "last N years"
    // rule, so there is no clock/TimeProvider dependency involved.
    internal const string MaleWikidataQid = "Q6581097";
    internal const string DateOfBirthCutoff = "1939-01-01T00:00:00Z";

    // Bug fix (2026-08-02, bug-bundle, REQ-1203): Wikidata models national
    // team caps under the same P54 ("member of sports team") property as
    // club membership — a national team like "Switzerland men's national
    // football team" is itself a Wikidata item, instance-of (transitively,
    // via P279 subclass chains such as "men's national association football
    // team") this Q6979593 "national association football team" class.
    // BuildPlayerCareerStintsByQidsQuery excludes any ?club matching this
    // class so xG Path's career-stint fetch never surfaces a national team
    // as a "club" — REQ-1203's own acceptance criteria are explicit that
    // "national team caps/appearances are never revealed as a clue for this
    // game." Confirmed via Wikidata's own item page, not training-knowledge
    // recall — see this file's PR description for the verification.
    internal const string NationalTeamClassWikidataQid = "Q6979593";

    // Shared shape between the two intersection query builders below —
    // extracted after REQ-214's P18 addition had to be hand-duplicated into
    // both (flagged in quality-gate review as exactly the kind of place
    // that's easy to silently diverge on next time). `candidateClauses` is
    // the one thing that actually differs per builder: which
    // country/club(s) a player must match. Everything else — the shared
    // predicates and the OPTIONAL/SERVICE footer — lives here so a future
    // addition (another OPTIONAL property, say) can only land once.
    //
    // No LIMIT — non-negotiable, see implementation-document.md §6a: the
    // result set IS the cell's complete answer key. Fetches skos:altLabel
    // in the same query so aliases cost nothing extra (REQ-208's alias
    // value, free). P106 = occupation (association football player),
    // P21 = sex or gender, P569 = date of birth (ADR-0025's male-only/
    // born-1939-or-later player pool restriction — REQ-112), P18 = image
    // (REQ-214's photo reveal — OPTIONAL, same as alias, so a player with
    // no photo still matches the rest of the query instead of being
    // dropped). P106/P21/P569 stay truthy (wdt:) on purpose: for those,
    // best-rank semantics match product intent (current citizenship, the
    // best-supported date of birth) — see each caller's own comment for why
    // P54 (club membership) can't use the same truthy shortcut.
    //
    // ADR-0042/S-079: ?startTime/?endTime/?numberOfMatches are P580/P582/
    // P1350 — qualifiers on the ?clubStatement variable, OPTIONAL so a
    // player still matches the rest of the query when they're absent (same
    // reasoning as alias/photo above). This is the one shared footer for
    // every candidateClauses builder below, so it only "just works" for the
    // three builders whose candidateClauses bind a club membership
    // statement to that exact variable name (BuildCountryClubIntersectionQuery,
    // BuildNationalTeamClubIntersectionQuery, BuildTrophyClubIntersectionQuery)
    // — BuildClubClubIntersectionQuery uses two distinctly-named statement
    // variables (?clubAStatement/?clubBStatement) and
    // BuildTrophyCountryIntersectionQuery has no P54 clause at all, so
    // ?clubStatement simply never binds for either and these three
    // qualifiers are silently absent from their results — not a bug, just
    // an empty binding (see WikidataLookupService's own scope note for why
    // only the country/nationality x club path persists this data as of
    // S-079).
    //
    // REQ-1207/S-082: ?position is P413 ("position played on team /
    // speciality") — OPTIONAL, same reasoning as ?photo above, so a player
    // with no P413 statement still matches the rest of the query. ?dateOfBirth
    // is NOT a new binding — the WHERE clause already required it (ADR-0025's
    // pool filter, the FILTER line below); it just wasn't previously listed
    // in the SELECT projection, so ParseBindings never saw it. Adding it to
    // SELECT is the only change needed to make Player.BirthYear derivable —
    // no new triple pattern, no new round-trip, per REQ-1207's "no new
    // binding added to the query for this field at all."
    //
    // Bug fix (2026-08-02, bug-bundle): ?positionLabel, not ?position, is
    // what actually gets projected into Player.Position (see ParseBindings
    // below) — ?position alone is the raw P413 object, a bare entity URI
    // (e.g. "http://www.wikidata.org/entity/Q336286"), never a human-readable
    // string. The SERVICE wikibase:label block below was already resolving
    // ?playerLabel/?clubLabel this whole time; ?positionLabel only needed to
    // be added to the SELECT list to make the same auto-label join happen for
    // ?position too — no new triple pattern. Real xG Path play surfaced this:
    // the position clue rendered the literal QID URI instead of a position
    // name.
    internal static string BuildIntersectionQuery(string candidateClauses) => $$"""
        SELECT ?player ?playerLabel ?alias ?photo ?positionLabel ?dateOfBirth ?startTime ?endTime ?numberOfMatches WHERE {
          ?player wdt:P106 wd:Q937857.
        {{candidateClauses}}
          ?player wdt:P21 wd:{{MaleWikidataQid}}.
          ?player wdt:P569 ?dateOfBirth.
          FILTER(?dateOfBirth >= "{{DateOfBirthCutoff}}"^^xsd:dateTime)
          OPTIONAL {
            ?player skos:altLabel ?alias.
            FILTER(LANG(?alias) = "en")
          }
          OPTIONAL { ?player wdt:P18 ?photo. }
          OPTIONAL { ?player wdt:P413 ?position. }
          OPTIONAL { ?clubStatement pq:P580 ?startTime. }
          OPTIONAL { ?clubStatement pq:P582 ?endTime. }
          OPTIONAL { ?clubStatement pq:P1350 ?numberOfMatches. }
          SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
        }
        """;

    // S-100/S-101 (docs/backlog.md): every Build*Query candidate-clause
    // method used to live here as a private static method — all 9 are now
    // moved, unchanged, to IntersectionQuerySpecs.cs as the
    // BuildCandidateClauses delegates for IntersectionQuerySpecs' 9 spec
    // entries (CountryClub/NationalTeamClub/ClubClub from S-100;
    // TrophyCountry/TrophyClub/TeamTrophyCountry/TeamTrophyNationalTeam/
    // TeamTrophyClub/TrophyNationalTeam from S-101). Their own rationale
    // comments (P54's full-statement-path vs. wdt:P54 shortcut, P1532 vs.
    // P27, the ADR-0052 FILTER EXISTS fix, P166's truthy shortcut, the
    // P1344/P3450/P1346 edition-winner join) moved with them — see that
    // file, not here, for all 9.

    // S-032/ADR-0007's broad "association football player" pool query, no
    // country/club filter — revised 2026-07-18: sliced by BIRTH YEAR, not
    // paged by LIMIT/OFFSET. The original shape (inner
    // `SELECT DISTINCT ?player ... ORDER BY ?player LIMIT n OFFSET m`)
    // forced WDQS to materialize and sort the ENTIRE unfiltered pool
    // (hundreds of thousands of items) on every page request, which blew
    // WDQS's hard ~60s server-side timeout on every single page — no
    // client-side timeout can raise that cap, so every run imported zero
    // rows (NOTES.md 2026-07-18). A one-year P569 window instead bounds each
    // query to a few thousand rows with NO ORDER BY, OFFSET, LIMIT, or inner
    // subquery — the same size/shape class as the intersection queries that
    // already work in production. Same male-only filter and 1939 floor as
    // the intersection queries (ADR-0025/REQ-112). A player with two P569
    // statements in different years appears in two slices; the importer's
    // deterministic-PlayerId upsert dedups that (see PlayerNameIndexImporter).
    // A player with more than one P27 citizenship produces more than one
    // result row for the same ?player — ParseNameIndexBindings groups by
    // qid, taking the first non-null value seen. Deliberately no P54 (club)
    // — that's PlayerAttribute's job, not this index's (ADR-0007) — and,
    // since 2026-07-18, no P18 (photo) either: the autocomplete contract
    // never exposes photos (design-document.md's SCREEN-02 note), so
    // fetching P18 was pure join/row cost for a column nothing read.
    internal static string BuildPlayerPoolBirthYearQuery(int birthYear) => $$"""
        SELECT ?player ?playerLabel ?birthYear ?countryLabel WHERE {
          ?player wdt:P106 wd:Q937857.
          ?player wdt:P21 wd:{{MaleWikidataQid}}.
          ?player wdt:P569 ?dateOfBirth.
          FILTER(?dateOfBirth >= "{{birthYear}}-01-01T00:00:00Z"^^xsd:dateTime && ?dateOfBirth < "{{birthYear + 1}}-01-01T00:00:00Z"^^xsd:dateTime)
          BIND(YEAR(?dateOfBirth) AS ?birthYear)
          OPTIONAL { ?player wdt:P27 ?country. }
          SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
        }
        """;

    // ADR-0055: the nationality-scoped sibling of BuildPlayerPoolBirthYearQuery
    // — same bounded shape (no ORDER BY/LIMIT/OFFSET), same male/
    // born-1939-or-later filter (ADR-0025/REQ-112, via the shared
    // DateOfBirthCutoff constant this time, rather than a per-year window),
    // filtered by P27 or P1532 instead of sliced by birth year.
    // useCountryForSportProperty selects between them — same flag,
    // same meaning as QueryNationalTeamClubIntersectionAsync/
    // QueryCountryClubIntersectionAsync's own split (ADR-0035). Truthy
    // (wdt:) for both P27 and P1532 — same reasoning
    // BuildNationalTeamClubIntersectionQuery's own comment gives for why
    // P1532 doesn't have P54's "current club" rank-hiding problem, and P27
    // ("citizenship") has no equivalent problem either (a player either
    // holds a citizenship or doesn't; Wikidata has no "current citizenship
    // supersedes past ones" editorial convention the way P54's "current
    // club" does).
    internal static string BuildPlayerPoolByNationalityQuery(string nationalityQid, bool useCountryForSportProperty)
    {
        var nationalityProperty = useCountryForSportProperty ? "P1532" : "P27";
        return $$"""
            SELECT ?player ?playerLabel ?birthYear WHERE {
              ?player wdt:P106 wd:Q937857.
              ?player wdt:P21 wd:{{MaleWikidataQid}}.
              ?player wdt:P569 ?dateOfBirth.
              FILTER(?dateOfBirth >= "{{DateOfBirthCutoff}}"^^xsd:dateTime)
              BIND(YEAR(?dateOfBirth) AS ?birthYear)
              ?player wdt:{{nationalityProperty}} wd:{{nationalityQid}}.
              SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
            }
            """;
    }

    // ADR-0069: the club-scoped sibling of BuildPlayerPoolByNationalityQuery
    // above — same bounded shape (no ORDER BY/LIMIT/OFFSET), same male/
    // born-1939-or-later filter (ADR-0025/REQ-112, via the shared
    // DateOfBirthCutoff constant), filtered by club membership instead of
    // nationality. P54 ("member of sports team") MUST use the full statement
    // path (p:P54/ps:P54, excluding deprecated rank) — NEVER the truthy
    // wdt:P54 shortcut — same non-negotiable "ever played for," not
    // "currently plays for," reasoning as every other P54 use in this
    // codebase; see IntersectionQuerySpecs.BuildCountryClubIntersectionQuery's
    // own comment for the full incident (Sandro Tonali x AC Milan) that
    // established this rule. Do not "simplify" this to wdt:P54.
    internal static string BuildPlayerPoolByClubQuery(string clubQid) => $$"""
        SELECT ?player ?playerLabel ?birthYear WHERE {
          ?player wdt:P106 wd:Q937857.
          ?player wdt:P21 wd:{{MaleWikidataQid}}.
          ?player wdt:P569 ?dateOfBirth.
          FILTER(?dateOfBirth >= "{{DateOfBirthCutoff}}"^^xsd:dateTime)
          BIND(YEAR(?dateOfBirth) AS ?birthYear)
          ?player p:P54 ?clubStatement.
          ?clubStatement ps:P54 wd:{{clubQid}}.
          MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
          SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
        }
        """;

    // S-188 (docs/backlog.md, Epic 26 — Supabase free-tier egress
    // remediation; a THIRD freshness mechanism alongside ADR-0088's
    // skip-forever default and ADR-0090's rotating bounded resweep — both on
    // PlayerCareerPrefetchService's full player-pool sweep. See both ADRs'
    // own "Alternatives considered" for why neither alone gives a real-time
    // answer to "a transfer just happened, reflect it now"): a cheap,
    // targeted, DATE-FILTERED query for players who joined a seeded club's
    // ?clubStatement since sinceUtc — RecentTransferSweepService's own
    // per-club fetch, meant to be run manually (workflow_dispatch) around a
    // transfer-window deadline day rather than waiting out ADR-0090's
    // multi-month rotation period for that specific club to come back
    // around.
    //
    // Cheap and safe to run often specifically BECAUSE WDQS filters by date
    // SERVER-SIDE (the FILTER below) — unlike BuildPlayerPoolByClubQuery's
    // own full, unbounded pool scan (every player who has EVER played for
    // this club), this query's result set is naturally bounded by how much
    // real transfer activity actually happened at this one club since
    // sinceUtc, not by squad size or career-history depth. See
    // BuildRecentClubDeparturesQuery just below for the pq:P582 mirror that
    // catches the opposite direction (an existing ongoing stint's end date
    // getting filled in).
    //
    // MUST use the full statement path (p:P54/ps:P54), never the truthy
    // wdt:P54 shortcut BuildPlayerPoolByClubQuery's own comment warns
    // against simplifying to — doubly so here, since this query's whole
    // point is reading the pq:P580 qualifier ON that statement (the
    // transfer date itself), a triple that does not even exist under the
    // truthy wdt: shortcut (that shortcut only ever exposes the statement's
    // best-rank VALUE, never its qualifiers). wdt:P54 therefore cannot
    // answer "when did this happen" at all — this isn't only the usual
    // "ever played for, not currently plays for" correctness preference
    // every other P54 use in this file gives, it's a mechanical requirement
    // of the query shape itself.
    //
    // ?endTime/?numberOfMatches are OPTIONAL, same reasoning as every other
    // qualifier fetch in this file (a stint that both started and ended
    // inside the same lookback window — a very short loan — still matches
    // the rest of the query rather than being dropped). No MINUS
    // national-team exclusion (unlike BuildPlayerCareerStintsByQidsQuery) —
    // this query is already scoped to one caller-supplied clubQid (always a
    // seeded ClubDefinition, never a national team), not a free ?club
    // binding across a player's whole career, so there is nothing to
    // exclude.
    //
    // No ?club/?clubLabel projection at all, unlike
    // BuildPlayerCareerStintsByQidsQuery — the caller already knows exactly
    // which club this is (the ClubDefinition it's iterating), so there is no
    // ambiguous label to canonicalize the way that method's own QID-based
    // canonicalization exists to solve (see WikidataCareerStintEntry's own
    // doc comment). RecentTransferSweepService supplies
    // ClubDefinition.Name directly to
    // SparqlResponseParsers.ParseRecentClubTransferBindings — the same
    // "this exact row IS the club, unambiguous" reasoning
    // PlayerCareerPrefetchService.SweepClubsAsync's own comment already
    // gives for its own club sweep (club.Name, never clubNameByClubQid).
    internal static string BuildRecentClubArrivalsQuery(string clubQid, DateTime sinceUtc) => $$"""
        SELECT ?player ?playerLabel ?startTime ?endTime ?numberOfMatches WHERE {
          ?player wdt:P106 wd:Q937857.
          ?player p:P54 ?clubStatement.
          ?clubStatement ps:P54 wd:{{clubQid}}.
          ?clubStatement pq:P580 ?startTime.
          FILTER(?startTime >= "{{FormatSparqlDateTime(sinceUtc)}}"^^xsd:dateTime)
          OPTIONAL { ?clubStatement pq:P582 ?endTime. }
          OPTIONAL { ?clubStatement pq:P1350 ?numberOfMatches. }
          MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
          SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
        }
        """;

    // S-188: the pq:P582 ("end time") mirror of BuildRecentClubArrivalsQuery
    // above — catches a departure (an existing stint's end date getting
    // filled in for the first time) the same server-side-filtered way.
    // ?startTime is OPTIONAL here, unlike the arrivals query's MANDATORY
    // bind above — this query's own FILTER is on ?endTime, not ?startTime,
    // so a real stint whose start date was never recorded on Wikidata at
    // all still matches the rest of this query. RecentTransferSweepService's
    // own reconciliation (PlayerCareerStintRefreshService.BuildNewStintsByPlayerId
    // -> CareerStintReconciler.Reconcile, keyed on (ClubName, StartYear))
    // simply has no key to match such a row against an existing stored row,
    // and SparqlResponseParsers.ParseRecentClubTransferBindings drops it
    // (same "startTime is non-nullable, skip if absent" discipline
    // ParseCareerStintBindings already uses) — a known, accepted
    // limitation (the same "omit rather than mislead" posture this codebase
    // already applies to an unknown AppearanceCount), not a bug.
    internal static string BuildRecentClubDeparturesQuery(string clubQid, DateTime sinceUtc) => $$"""
        SELECT ?player ?playerLabel ?startTime ?endTime ?numberOfMatches WHERE {
          ?player wdt:P106 wd:Q937857.
          ?player p:P54 ?clubStatement.
          ?clubStatement ps:P54 wd:{{clubQid}}.
          ?clubStatement pq:P582 ?endTime.
          FILTER(?endTime >= "{{FormatSparqlDateTime(sinceUtc)}}"^^xsd:dateTime)
          OPTIONAL { ?clubStatement pq:P580 ?startTime. }
          OPTIONAL { ?clubStatement pq:P1350 ?numberOfMatches. }
          MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
          SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
        }
        """;

    // ISO 8601 UTC, matching DateOfBirthCutoff's own literal format
    // ("1939-01-01T00:00:00Z") — xsd:dateTime requires this exact shape.
    // sinceUtc is assumed already UTC (RecentTransferSweepService computes
    // it via DateTime.UtcNow.AddDays(-lookbackDays)); ToUniversalTime() is
    // still applied defensively so a caller that accidentally passes a
    // local-kind DateTime doesn't silently produce a wrong-timezone filter.
    private static string FormatSparqlDateTime(DateTime sinceUtc) =>
        sinceUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // A VALUES clause over the batch, not a candidate-matching pattern —
    // deliberately no male/date-of-birth/occupation filter here, unlike
    // every other query in this file: every QID in the batch is already a
    // real Player row this codebase itself created via the intersection
    // queries (which DID apply those filters at the time), so re-filtering
    // here would only risk a false negative if Wikidata's own P21/P569 data
    // changed since. No LIMIT/ORDER BY/OFFSET, same bounded-query
    // discipline as every other query here — the caller
    // (PlayerPhotoBackfillService) is responsible for keeping each batch
    // small (BatchSize = 200), not this method.
    internal static string BuildPlayerPhotosByQidsQuery(IReadOnlyList<string> qids)
    {
        var valuesClause = string.Join(" ", qids.Select(qid => $"wd:{qid}"));
        return $$"""
            SELECT ?player ?photo WHERE {
              VALUES ?player { {{valuesClause}} }
              OPTIONAL { ?player wdt:P18 ?photo. }
            }
            """;
    }

    // Same "VALUES clause over the batch, no candidate-matching filter" shape
    // as BuildPlayerPhotosByQidsQuery — every QID in the batch is already a
    // real Player row this codebase itself created via the intersection
    // queries (which DID apply the male/date-of-birth/occupation filters at
    // the time), so re-filtering here would only risk a false negative if
    // Wikidata's own data changed since. No LIMIT/ORDER BY/OFFSET, same
    // bounded-query discipline as every other query here — the caller
    // (PlayerPositionBirthYearBackfillService) is responsible for keeping
    // each batch small, not this method.
    // Bug fix (2026-08-02, bug-bundle): this query had no SERVICE
    // wikibase:label block at all — ?position was projected straight into
    // Player.Position as the raw P413 entity URI, never resolved to a
    // human-readable name (the same class of bug BuildIntersectionQuery's
    // own comment describes, but this backfill query never even had the
    // label service every other query in this file already uses for
    // ?playerLabel/?clubLabel). Adding it here, plus ?positionLabel in the
    // SELECT, is what actually fixes it for every Player row this backfill
    // touches — see SparqlResponseParsers.ParsePositionBirthYearBindings
    // (moved there by S-155, docs/backlog.md) for the matching read-side
    // change.
    internal static string BuildPlayerPositionsAndBirthYearsByQidsQuery(IReadOnlyList<string> qids)
    {
        var valuesClause = string.Join(" ", qids.Select(qid => $"wd:{qid}"));
        return $$"""
            SELECT ?player ?positionLabel ?dateOfBirth WHERE {
              VALUES ?player { {{valuesClause}} }
              OPTIONAL { ?player wdt:P413 ?position. }
              OPTIONAL { ?player wdt:P569 ?dateOfBirth. }
              SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
            }
            """;
    }

    // Full P54 statement path (p:P54/ps:P54, MINUS deprecated rank), same
    // non-negotiable "ever played for," not "currently plays for" reasoning
    // as every other P54 use in this file — see
    // BuildCountryClubIntersectionQuery's own comment. ?club (not a
    // caller-supplied QID this time) is itself part of the SELECT, with
    // ?clubLabel resolved by the same label service every other builder in
    // this file already uses — this is what makes the query "every club,"
    // not "this one club."
    // Bug fix (2026-08-02, bug-bundle, REQ-1203): excludes national teams
    // from ?club — see NationalTeamClassWikidataQid's own comment for why
    // this is necessary at all (P54 covers both club and international
    // caps) and why the exclusion needs the transitive P279* subclass path,
    // not just a direct P31 check, since a specific national team's P31 is
    // typically a narrower subclass (e.g. "men's national association
    // football team") rather than Q6979593 itself.
    // ?club added to the SELECT (bug fix, 2026-08-04, xG Path duplicate-node
    // bug, REQ-1203 follow-up): the 2026-08-03 SparqlResponseParsers.NormalizeClubName
    // fix (moved there by S-155, docs/backlog.md) only strips a small,
    // hand-picked set of legal-suffix tokens
    // ("FC"/"AFC"/etc.) and does nothing for a genuine alternate-name
    // variant (e.g. "Lyon" vs. "Olympique Lyonnais") — the underlying ?club
    // QID is the only reliable way to recognize "this is the same real
    // club" across such variants, since ClubDefinition.WikidataQid already
    // exists specifically to canonicalize against (see
    // ParseCareerStintBindings' own comment for where this QID is threaded
    // to). ?club was already bound in the query body (?clubStatement ps:P54
    // ?club) — it just wasn't projected.
    internal static string BuildPlayerCareerStintsByQidsQuery(IReadOnlyList<string> qids)
    {
        var valuesClause = string.Join(" ", qids.Select(qid => $"wd:{qid}"));
        return $$"""
            SELECT ?player ?club ?clubLabel ?startTime ?endTime ?numberOfMatches WHERE {
              VALUES ?player { {{valuesClause}} }
              ?player p:P54 ?clubStatement.
              ?clubStatement ps:P54 ?club.
              MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
              MINUS { ?club wdt:P31/wdt:P279* wd:{{NationalTeamClassWikidataQid}}. }
              OPTIONAL { ?clubStatement pq:P580 ?startTime. }
              OPTIONAL { ?clubStatement pq:P582 ?endTime. }
              OPTIONAL { ?clubStatement pq:P1350 ?numberOfMatches. }
              SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
            }
            """;
    }

    // Same "VALUES clause over the batch, no candidate-matching filter"
    // shape as BuildPlayerPhotosByQidsQuery/
    // BuildPlayerPositionsAndBirthYearsByQidsQuery — the caller
    // (XGPathGameModule) is responsible for keeping each batch within the
    // same bounded-query class every other batch method in this file uses.
    internal static string BuildSitelinkCountsByQidsQuery(IReadOnlyList<string> qids)
    {
        var valuesClause = string.Join(" ", qids.Select(qid => $"wd:{qid}"));
        return $$"""
            SELECT ?player ?sitelinks WHERE {
              VALUES ?player { {{valuesClause}} }
              OPTIONAL { ?player wikibase:sitelinks ?sitelinks. }
            }
            """;
    }

    // REQ-513 (GitHub issue #239): the admin single-player refresh query —
    // a VALUES clause over exactly ONE QID (the batch-of-one degenerate
    // case of BuildPlayerPhotosByQidsQuery/
    // BuildPlayerPositionsAndBirthYearsByQidsQuery's own shape), combining
    // all three of those methods' OPTIONAL bindings (P413/P569/P18) PLUS
    // ?playerLabel — no existing single-QID query in this file already
    // fetches the label, so this is a new, narrow addition rather than a
    // duplicate of any of the three above. Same "no candidate-matching
    // filter" reasoning as those three: the QID passed in is always an
    // already-real Player row's own already-trusted WikidataQid, so there is
    // nothing to re-filter by male/date-of-birth/occupation the way a fresh
    // discovery query would.
    internal static string BuildPlayerRefreshDataByQidQuery(string qid) => $$"""
        SELECT ?player ?playerLabel ?positionLabel ?dateOfBirth ?photo WHERE {
          VALUES ?player { wd:{{qid}} }
          OPTIONAL { ?player wdt:P413 ?position. }
          OPTIONAL { ?player wdt:P569 ?dateOfBirth. }
          OPTIONAL { ?player wdt:P18 ?photo. }
          SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
        }
        """;

    // Case-insensitive label-OR-alias match, deliberately LIMIT 1 — see
    // IWikidataClient.QueryPlayerPhotoByNameAsync's own doc comment for why
    // this is the one query in this file that both filters by a free-text
    // string and caps its result set. rdfs:label is Wikidata's primary
    // label triple (distinct from the SERVICE wikibase:label block below,
    // which resolves ?playerLabel for the SELECTed player once a match is
    // already found via either branch of the UNION) — skos:altLabel is the
    // same alias predicate BuildIntersectionQuery already uses elsewhere in
    // this file.
    internal static string BuildPlayerPhotoByNameQuery(string playerName)
    {
        var escapedName = EscapeSparqlStringLiteral(playerName);
        return $$"""
            SELECT ?player ?playerLabel ?photo WHERE {
              ?player wdt:P106 wd:Q937857.
              {
                ?player rdfs:label ?matchedLabel.
                FILTER(LANG(?matchedLabel) = "en")
              }
              UNION
              {
                ?player skos:altLabel ?matchedLabel.
                FILTER(LANG(?matchedLabel) = "en")
              }
              FILTER(LCASE(STR(?matchedLabel)) = LCASE("{{escapedName}}"))
              OPTIONAL { ?player wdt:P18 ?photo. }
              SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
            }
            LIMIT 1
            """;
    }

    // This file's first query to interpolate free, player-supplied text
    // (every other query only ever interpolates a QID this codebase itself
    // resolved, or a fixed constant) — escapes the two characters that would
    // otherwise break out of the SPARQL string literal (backslash, double
    // quote) and strips newlines (a submitted guess should never contain
    // one, but a SPARQL string literal can't safely span lines either way).
    private static string EscapeSparqlStringLiteral(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");

    // ADR-0062 (2026-08-09): the candidate-selection subquery below used to
    // scan every Wikidata footballer's rdfs:label/skos:altLabel with a
    // case-insensitive FILTER — an unindexed, population-wide graph scan
    // that a production log confirmed WDQS's own gateway will 502 on once it
    // runs long enough (38.8s, not a client-side timeout — see
    // WikidataClient's own _adminLookupQueryTimeout doc comment (S-155 moved
    // this builder out of that file, docs/backlog.md), and ADR-0062's
    // Context section for the full incident). It's replaced with a federated
    // `SERVICE wikibase:mwapi { ... }` block that delegates candidate
    // selection to Wikidata's own indexed `EntitySearch` API (the same
    // engine behind Wikidata's search box) instead of a raw literal scan —
    // same single-endpoint SPARQL client, no new external host. The
    // subquery still selects exactly one candidate ?player (LIMIT 1, same
    // as before): EntitySearch returns up to `mwapi:limit` ranked
    // candidates (10 here — deliberately more than 1, since EntitySearch
    // ranks by general relevance and can surface a same/similar-named
    // non-footballer above the actual match), each re-filtered by
    // `wdt:P106 wd:Q937857` (footballers) before the first survivor is
    // taken. This mechanism change does NOT alter the outer query's shape
    // or bindings — see the OPTIONAL P27/P54 comment just below, which is
    // unchanged. Unverified against the real query.wikidata.org endpoint
    // from this sandbox (no live network access) — see ADR-0062's
    // Consequences section for what a human must confirm before this is
    // trusted in production.
    //
    // Combines that mwapi-based candidate subquery (LIMIT 1 — scoped to the
    // SUBQUERY's own ?player projection, so it bounds "how many CANDIDATE
    // PLAYERS this matches" to one, without truncating that one player's own
    // OPTIONAL P27/P54 rows the way a top-level LIMIT 1 would) with
    // QueryPlayerCareerStintsByQidsAsync's full P54 statement-path club
    // history (same MINUS-deprecated-rank/MINUS-national-team-class shape —
    // see that query builder's own comment for why both MINUS clauses are
    // needed) and a P27 citizenship label. ?nationality/?club are both
    // OPTIONAL and independent of each other — a player matched by name with
    // neither, either, or both bound is every one of those a normal, valid
    // outcome (parsed by
    // SparqlResponseParsers.ParsePlayerCareerAndNationalityByNameBindings,
    // moved there by S-155, docs/backlog.md).
    internal static string BuildPlayerCareerAndNationalityByNameQuery(string playerName)
    {
        var escapedName = EscapeSparqlStringLiteral(playerName);
        return $$"""
            SELECT ?player ?playerLabel ?nationalityLabel ?club ?clubLabel ?startTime ?endTime ?numberOfMatches WHERE {
              {
                SELECT ?player WHERE {
                  SERVICE wikibase:mwapi {
                    bd:serviceParam wikibase:api "EntitySearch".
                    bd:serviceParam wikibase:endpoint "www.wikidata.org".
                    bd:serviceParam mwapi:search "{{escapedName}}".
                    bd:serviceParam mwapi:language "en".
                    bd:serviceParam mwapi:limit "10".
                    ?player wikibase:apiOutputItem mwapi:item.
                  }
                  ?player wdt:P106 wd:Q937857.
                }
                LIMIT 1
              }
              OPTIONAL { ?player wdt:P27 ?nationality. }
              OPTIONAL {
                ?player p:P54 ?clubStatement.
                ?clubStatement ps:P54 ?club.
                MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
                MINUS { ?club wdt:P31/wdt:P279* wd:{{NationalTeamClassWikidataQid}}. }
                OPTIONAL { ?clubStatement pq:P580 ?startTime. }
                OPTIONAL { ?clubStatement pq:P582 ?endTime. }
                OPTIONAL { ?clubStatement pq:P1350 ?numberOfMatches. }
              }
              SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
            }
            """;
    }
}

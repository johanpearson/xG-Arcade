namespace XGArcade.DataSync.Wikidata;

// S-100 (docs/backlog.md): the spec-table registry WikidataClient.QueryIntersectionAsync
// looks up by (CategoryType, CategoryType) before building/running a query.
// Only the 3 non-trophy pairs are migrated here so far (country-club,
// national-team-club, club-club) -- the other 6 Query*IntersectionAsync
// methods on WikidataClient still call their own Build*Query methods and
// RunIntersectionQueryAsync directly, unchanged (S-101 migrates those onto
// this same table). Each BuildCandidateClauses method below is moved,
// character-for-character (only the wrapping BuildIntersectionQuery(...)
// call is gone -- WikidataClient.QueryIntersectionAsync makes that call
// itself now), from WikidataClient's former Build*IntersectionQuery methods
// of the same name -- see WikidataClientTests.cs's byte-for-byte SPARQL
// assertions for the regression proof this didn't change a single character
// of generated SPARQL for these three pairs.
internal static class IntersectionQuerySpecs
{
    internal static readonly IntersectionQuerySpec CountryClub = new(
        CategoryType.Country, CategoryType.Club, "country-club", BuildCountryClubIntersectionQuery);

    internal static readonly IntersectionQuerySpec NationalTeamClub = new(
        CategoryType.NationalTeam, CategoryType.Club, "national-team-club", BuildNationalTeamClubIntersectionQuery);

    internal static readonly IntersectionQuerySpec ClubClub = new(
        CategoryType.Club, CategoryType.Club, "club-club", BuildClubClubIntersectionQuery);

    // Declared after the three specs above (not before) -- a static field
    // initializer that referenced CountryClub/NationalTeamClub/ClubClub
    // before their own initializers had run would capture null, since C#
    // runs static field initializers in textual declaration order.
    internal static readonly IReadOnlyDictionary<(CategoryType TypeA, CategoryType TypeB), IntersectionQuerySpec> ByCategoryPair =
        new Dictionary<(CategoryType, CategoryType), IntersectionQuerySpec>
        {
            [(CategoryType.Country, CategoryType.Club)] = CountryClub,
            [(CategoryType.NationalTeam, CategoryType.Club)] = NationalTeamClub,
            [(CategoryType.Club, CategoryType.Club)] = ClubClub,
        };

    // P54 deliberately uses the full statement path (p:P54/ps:P54,
    // excluding only deprecated rank), NOT the truthy wdt:P54 shortcut
    // BuildIntersectionQuery's shared predicates use — do not "simplify" it
    // back. Wikidata's truthy wdt: graph contains only best-rank
    // statements: the moment any P54 statement on a player is marked
    // preferred rank (editors routinely mark the *current* club
    // preferred), every normal-rank historical club silently vanishes from
    // wdt:P54. That turned "ever played for this club" into "currently
    // plays for this club" for exactly those players (e.g. Sandro Tonali x
    // AC Milan), leaving the persisted answer key incomplete and correct
    // guesses scored incorrect (REQ-113's ever-played-for semantics,
    // REQ-101/REQ-203's correctness contract). Both grid generation and
    // REQ-211's guess-time live lookup route through both builders below,
    // so the statement path covers both.
    private static string BuildCountryClubIntersectionQuery(string countryQid, string clubQid) => $$"""
              ?player wdt:P27 wd:{{countryQid}}.
              ?player p:P54 ?clubStatement.
              ?clubStatement ps:P54 wd:{{clubQid}}.
              MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
            """;

    // REQ-114/ADR-0035: England/Scotland/Wales/Northern Ireland aren't
    // sovereign states, so P27 ("country of citizenship") can't distinguish
    // them — every English/Scottish/Welsh/Northern Irish player's P27 is
    // uniformly United Kingdom (Q145). P1532 ("country for sport") is
    // Wikidata's own property for "country represented in competition,"
    // which is exactly what a football trivia game means by "England."
    // Deliberately uses the truthy wdt:P1532 shortcut, unlike P54's full
    // statement path above — P1532 doesn't have P54's "current club" rank-
    // hiding problem: there's no Wikidata editorial convention of marking
    // one P1532 statement "preferred rank" to mean "the country they
    // currently represent" the way editors routinely do for a player's
    // *current* club on P54 (see BuildCountryClubIntersectionQuery's own
    // comment for that incident). A player either represented a given
    // national team or they didn't — best-rank semantics and "represented
    // this country at all" coincide here, the same reasoning
    // BuildTrophyCountryIntersectionQuery's comment gives for P166's truthy
    // shortcut. Same P54 full-statement-path club-membership half as every
    // other club-involving query in this file — do not "simplify" that half
    // to wdt:P54.
    private static string BuildNationalTeamClubIntersectionQuery(string nationalTeamQid, string clubQid) => $$"""
              ?player wdt:P1532 wd:{{nationalTeamQid}}.
              ?player p:P54 ?clubStatement.
              ?clubStatement ps:P54 wd:{{clubQid}}.
              MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
            """;

    // S-030: "ever played for both clubs" — P54 checked twice instead of
    // once against P27, same full-statement-path-not-truthy P54 rule as
    // BuildCountryClubIntersectionQuery above (see its comment for why
    // wdt:P54 is wrong here). Two distinct statement variables, one per
    // club — a single shared variable could never bind (one statement
    // can't point at two clubs).
    //
    // 2026-08-01 fix (ADR-0052): each club's match is wrapped in its own
    // FILTER EXISTS block instead of a plain join. A plain join binds
    // ?clubAStatement/?clubBStatement in the outer pattern, so a player
    // with multiple non-deprecated P54 statements at club A (loan spells, a
    // return transfer) times multiple at club B produces one result ROW PER
    // (clubAStatement, clubBStatement) COMBINATION per player — on top of
    // the per-alias multiplication BuildIntersectionQuery's OPTIONAL
    // alt-label fetch already applies. For two clubs with a large,
    // well-documented, historically-overlapping squad this combination
    // produced a real 250,000+ row WDQS response that neither WDQS nor this
    // client's JSON parser could finish inside any reasonable timeout, and
    // the same doomed pair got re-attempted on every future
    // warm-player-cache run since nothing persisted its failure (see
    // PairLookupFailure, ADR-0052, for that half of the fix). FILTER EXISTS
    // checks "does at least one qualifying statement exist" without binding
    // ?clubAStatement/?clubBStatement in the outer pattern, so neither
    // club's statement count can multiply rows — the result is exactly one
    // row per matching player before the still-intentional per-alias
    // multiplication. This is safe specifically because club-club never
    // reads the shared footer's per-statement qualifiers (?clubStatement,
    // singular — a different variable, never bound by this builder either
    // way, see BuildIntersectionQuery's own qualifier comment); a builder
    // that DOES need those qualifiers (country-club, national-team-club,
    // trophy-club) cannot use this same trick without losing them. Never
    // simplify this back to a plain join.
    private static string BuildClubClubIntersectionQuery(string clubAQid, string clubBQid) => $$"""
              FILTER EXISTS {
                ?player p:P54 ?clubAStatement.
                ?clubAStatement ps:P54 wd:{{clubAQid}}.
                MINUS { ?clubAStatement wikibase:rank wikibase:DeprecatedRank. }
              }
              FILTER EXISTS {
                ?player p:P54 ?clubBStatement.
                ?clubBStatement ps:P54 wd:{{clubBQid}}.
                MINUS { ?clubBStatement wikibase:rank wikibase:DeprecatedRank. }
              }
            """;
}

namespace XGArcade.DataSync.Wikidata;

// S-100/S-101 (docs/backlog.md): the spec-table registry
// WikidataClient.QueryIntersectionAsync looks up by (CategoryType,
// CategoryType) before building/running a query. All 9 pairs are migrated
// here -- S-100 moved the 3 non-trophy pairs (country-club,
// national-team-club, club-club); S-101 moved the remaining 6
// trophy-involving pairs. Each BuildCandidateClauses method below is moved,
// character-for-character (only the wrapping BuildIntersectionQuery(...)
// call is gone -- WikidataClient.QueryIntersectionAsync makes that call
// itself now), from WikidataClient's former Build*IntersectionQuery methods
// of the same name -- see WikidataClientTests.cs's byte-for-byte SPARQL
// assertions for the regression proof this didn't change a single character
// of generated SPARQL for any of the 9 pairs.
internal static class IntersectionQuerySpecs
{
    internal static readonly IntersectionQuerySpec CountryClub = new(
        CategoryType.Country, CategoryType.Club, "country-club", BuildCountryClubIntersectionQuery);

    internal static readonly IntersectionQuerySpec NationalTeamClub = new(
        CategoryType.NationalTeam, CategoryType.Club, "national-team-club", BuildNationalTeamClubIntersectionQuery);

    internal static readonly IntersectionQuerySpec ClubClub = new(
        CategoryType.Club, CategoryType.Club, "club-club", BuildClubClubIntersectionQuery);

    internal static readonly IntersectionQuerySpec TrophyCountry = new(
        CategoryType.Trophy, CategoryType.Country, "trophy-country", BuildTrophyCountryIntersectionQuery);

    internal static readonly IntersectionQuerySpec TrophyClub = new(
        CategoryType.Trophy, CategoryType.Club, "trophy-club", BuildTrophyClubIntersectionQuery);

    internal static readonly IntersectionQuerySpec TeamTrophyCountry = new(
        CategoryType.TeamTrophy, CategoryType.Country, "team-trophy-country", BuildTeamTrophyCountryIntersectionQuery);

    internal static readonly IntersectionQuerySpec TeamTrophyNationalTeam = new(
        CategoryType.TeamTrophy, CategoryType.NationalTeam, "team-trophy-national-team", BuildTeamTrophyNationalTeamIntersectionQuery);

    internal static readonly IntersectionQuerySpec TeamTrophyClub = new(
        CategoryType.TeamTrophy, CategoryType.Club, "team-trophy-club", BuildTeamTrophyClubIntersectionQuery);

    internal static readonly IntersectionQuerySpec TrophyNationalTeam = new(
        CategoryType.Trophy, CategoryType.NationalTeam, "trophy-national-team", BuildTrophyNationalTeamIntersectionQuery);

    // Declared after the specs above (not before) -- a static field
    // initializer that referenced them before their own initializers had run
    // would capture null, since C# runs static field initializers in
    // textual declaration order.
    internal static readonly IReadOnlyDictionary<(CategoryType TypeA, CategoryType TypeB), IntersectionQuerySpec> ByCategoryPair =
        new Dictionary<(CategoryType, CategoryType), IntersectionQuerySpec>
        {
            [(CategoryType.Country, CategoryType.Club)] = CountryClub,
            [(CategoryType.NationalTeam, CategoryType.Club)] = NationalTeamClub,
            [(CategoryType.Club, CategoryType.Club)] = ClubClub,
            [(CategoryType.Trophy, CategoryType.Country)] = TrophyCountry,
            [(CategoryType.Trophy, CategoryType.Club)] = TrophyClub,
            [(CategoryType.TeamTrophy, CategoryType.Country)] = TeamTrophyCountry,
            [(CategoryType.TeamTrophy, CategoryType.NationalTeam)] = TeamTrophyNationalTeam,
            [(CategoryType.TeamTrophy, CategoryType.Club)] = TeamTrophyClub,
            [(CategoryType.Trophy, CategoryType.NationalTeam)] = TrophyNationalTeam,
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

    // S-101 (docs/backlog.md): BuildTrophyCountryIntersectionQuery,
    // BuildTrophyClubIntersectionQuery, BuildTeamTrophyCountryIntersectionQuery,
    // BuildTeamTrophyNationalTeamIntersectionQuery, BuildTeamTrophyClubIntersectionQuery,
    // and BuildTrophyNationalTeamIntersectionQuery used to live in
    // WikidataClient.cs as private static methods -- moved, unchanged (only
    // the wrapping BuildIntersectionQuery(...) call is gone, same as the
    // three builders above), to become the BuildCandidateClauses delegates
    // for IntersectionQuerySpecs.TrophyCountry/TrophyClub/TeamTrophyCountry/
    // TeamTrophyNationalTeam/TeamTrophyClub/TrophyNationalTeam. Their own
    // rationale comments (P166's truthy shortcut, the P1344/P3450/P1346
    // edition-winner join, the P1532 indirection on the winner side) moved
    // with them.

    // S-031/REQ-108: P166 ("award received") — deliberately uses the truthy
    // wdt:P166 shortcut, unlike P54 above. This is a real judgment call, not
    // a reflexive "truthy is simpler": P54's truthy shortcut is unsafe
    // specifically because Wikidata editors routinely mark a player's
    // *current* club statement preferred rank, which silently drops every
    // normal-rank historical club from the best-rank-only wdt: graph (see
    // BuildCountryClubIntersectionQuery's own comment for the Sandro Tonali
    // incident this pins down). A repeatable individual award like Ballon
    // d'Or has no equivalent editorial convention — there's no "this win
    // supersedes that win" preferred-rank practice on P166 statements the
    // way there is for "this is my current club" on P54 — so best-rank
    // semantics and "received this award at all" coincide here, and truthy
    // is safe. If a future trophy turns out to have its own rank quirk,
    // this reasoning (and the truthy shortcut) needs re-checking per-trophy,
    // not assumed to hold universally just because it holds for Ballon d'Or.
    private static string BuildTrophyCountryIntersectionQuery(string trophyQid, string countryQid) => $$"""
              ?player wdt:P166 wd:{{trophyQid}}.
              ?player wdt:P27 wd:{{countryQid}}.
            """;

    // S-031/REQ-108: P166 (truthy, see BuildTrophyCountryIntersectionQuery's
    // comment) + P54 (full statement path, excluding only deprecated rank —
    // the same non-negotiable "ever played for," not "currently plays for,"
    // reasoning as every other P54 use in this file). Do not "simplify" the
    // P54 half to wdt:P54.
    private static string BuildTrophyClubIntersectionQuery(string trophyQid, string clubQid) => $$"""
              ?player wdt:P166 wd:{{trophyQid}}.
              ?player p:P54 ?clubStatement.
              ?clubStatement ps:P54 wd:{{clubQid}}.
              MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
            """;

    // ADR-0061: team-competition trophies have no P166-equivalent player
    // statement — see IWikidataClient.QueryTeamTrophyCountryIntersectionAsync's
    // own doc comment for the full "why a three-hop join" reasoning. P1344
    // ("participant of"), P3450 ("sports season of league or competition"),
    // and P1346 ("winner") all stay truthy (wdt:) — none of the three has a
    // documented "current vs. historical" rank-hiding convention the way P54
    // does (BuildCountryClubIntersectionQuery's own comment), and a specific
    // tournament edition is a one-time historical fact, not something with a
    // "current" value at all, so best-rank and "participated in/won this
    // edition at all" coincide, the same reasoning
    // BuildTrophyCountryIntersectionQuery's own comment gives for P166.
    //
    // The winner-side join is ALWAYS P1532 ("country for sport"), regardless
    // of which property identifies the PLAYER's side of the match (P27 here,
    // P1532 in BuildTeamTrophyNationalTeamIntersectionQuery below) — a
    // P1346 winner value for the World Cup is a national-team item (e.g.
    // "Brazil national football team"), never the country item itself, so a
    // direct QID match against the country would silently return zero
    // results rather than erroring. See ADR-0061's "Alternatives considered"
    // table for why this isn't simplified to a direct country match.
    private static string BuildTeamTrophyCountryIntersectionQuery(string trophyQid, string countryQid) => $$"""
              ?player wdt:P27 wd:{{countryQid}}.
              ?player wdt:P1344 ?edition.
              ?edition wdt:P3450 wd:{{trophyQid}}.
              ?edition wdt:P1346 ?winner.
              ?winner wdt:P1532 wd:{{countryQid}}.
            """;

    // ADR-0061/ADR-0035: the P1532 player-side counterpart of
    // BuildTeamTrophyCountryIntersectionQuery above, for England/Scotland/
    // Wales/Northern Ireland — same reasoning
    // BuildNationalTeamClubIntersectionQuery's own comment gives for why
    // truthy wdt:P1532 is safe on the player side. The winner-side join
    // stays P1532 either way (see BuildTeamTrophyCountryIntersectionQuery's
    // own comment) — this builder only changes which property identifies
    // the PLAYER's side of the match, never the winner side.
    private static string BuildTeamTrophyNationalTeamIntersectionQuery(string trophyQid, string countryQid) => $$"""
              ?player wdt:P1532 wd:{{countryQid}}.
              ?player wdt:P1344 ?edition.
              ?edition wdt:P3450 wd:{{trophyQid}}.
              ?edition wdt:P1346 ?winner.
              ?winner wdt:P1532 wd:{{countryQid}}.
            """;

    // ADR-0061: team-competition trophy x club. Deliberately keeps the P54
    // club-membership clause (full statement path, same non-negotiable
    // "ever played for," not "currently plays for," reasoning as every
    // other P54 use in this file) ALONGSIDE the P1344/P3450/P1346
    // edition-winner join, not instead of it — P1344 alone ("participated
    // in this edition") is true for every player on every club that reached
    // that edition, not just the winning squad; requiring club membership
    // too narrows this back down to "played for the specific club that won
    // it." A best-effort narrowing, not a guarantee — see ADR-0061's
    // Consequences section for the known residual gap (no season/date
    // qualifier matching between P54 and the edition's own year). The
    // trophy's edition winner is matched directly against the club QID — a
    // club competition's winner item IS the club item, no P1532-style
    // indirection needed here (unlike the two country variants above).
    private static string BuildTeamTrophyClubIntersectionQuery(string trophyQid, string clubQid) => $$"""
              ?player p:P54 ?clubStatement.
              ?clubStatement ps:P54 wd:{{clubQid}}.
              MINUS { ?clubStatement wikibase:rank wikibase:DeprecatedRank. }
              ?player wdt:P1344 ?edition.
              ?edition wdt:P3450 wd:{{trophyQid}}.
              ?edition wdt:P1346 wd:{{clubQid}}.
            """;

    // Judgment call, not part of ADR-0061's own three-builder list — the
    // individual-award P166 counterpart of
    // BuildTeamTrophyNationalTeamIntersectionQuery, needed to fully close
    // ADR-0035's follow-up note for the EXISTING S-031 P166 path (see
    // IWikidataClient.QueryTrophyNationalTeamIntersectionAsync's own doc
    // comment). P166 stays truthy for the same reason
    // BuildTrophyCountryIntersectionQuery's own comment gives; P1532 stays
    // truthy for the same reason BuildNationalTeamClubIntersectionQuery's
    // own comment gives. No P54/edition-join clauses at all — this is the
    // individual-award shape, not the team-competition one.
    private static string BuildTrophyNationalTeamIntersectionQuery(string trophyQid, string countryQid) => $$"""
              ?player wdt:P166 wd:{{trophyQid}}.
              ?player wdt:P1532 wd:{{countryQid}}.
            """;
}

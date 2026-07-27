namespace XGArcade.DataSync.Wikidata;

// One row of the intersection query's result set (implementation-document.md
// §6a) — a player satisfying both the country and club category values,
// with whatever skos:altLabel aliases Wikidata returned in the same query.
//
// PhotoUrl (REQ-214): Wikidata's P18 (image), fetched OPTIONAL in the same
// query as everything else here — null whenever the player has no P18
// statement, never an error. wdt:P18 is a commonsMedia-typed property, so
// Wikidata's own SPARQL endpoint resolves it directly to a fully-qualified
// Special:FilePath URL (not an entity QID) — unlike WikidataQid above,
// there is no "/entity/Qnnn" suffix to split off; the binding's raw value
// IS the usable photo URL. This shape could not be verified against a live
// query from this environment (no wikidata.org access) — flagged for
// manual verification, same as every other newly-introduced Wikidata
// property in this codebase's recent history (S-036/S-037).
// CareerStints (ADR-0042/S-079): the P580/P582/P1350 qualifiers on the same
// P54 club-membership statement this query already fetches — one tuple per
// distinct (StartYear, EndYear, AppearanceCount) combination, deduped the
// same way Aliases is (a HashSet in ParseBindings), and only ever bound for
// the query shapes whose candidateClauses share the ?clubStatement variable
// name (country-club, national-team-club, trophy-club) — club-club (two
// distinctly-named statement variables) and trophy-country (no P54 clause
// at all) simply never populate it, which is not a bug. Only
// WikidataLookupService.LookupAndPersistAsync (the country/nationality x
// club path) actually persists these as of S-079 — see that method's own
// comment for why the other three Lookup*Async callers deliberately leave
// this unconsumed for now.
public record WikidataPlayerMatch(
    string WikidataQid,
    string FullName,
    IReadOnlyList<string> Aliases,
    string? PhotoUrl = null,
    IReadOnlyList<CareerStintQualifiers>? CareerStints = null)
{
    // Shadows the primary constructor's CareerStints parameter to default to
    // an empty list rather than null — every caller (including tests
    // constructing this record directly) can enumerate it without a null
    // check, the same convenience Aliases already gets from ParseBindings
    // always supplying a (possibly empty) list.
    public IReadOnlyList<CareerStintQualifiers> CareerStints { get; init; } = CareerStints ?? [];
}

// ADR-0042/S-079: one distinct (start, end, appearance-count) combination
// carried by a player's P54 statement qualifiers. StartYear is
// non-nullable — a tuple is only ever constructed when Wikidata's P580
// ("start time") qualifier was actually bound; a row with none of the three
// qualifiers bound carries zero information and never produces one of
// these (see WikidataClient.ParseBindings). Two statements that happen to
// share identical (start, end, count) collapse into one tuple — there is
// no way to distinguish them from this query shape, and it is not worth
// tracking raw statement URIs just to do so.
public record CareerStintQualifiers(int StartYear, int? EndYear, int? AppearanceCount);

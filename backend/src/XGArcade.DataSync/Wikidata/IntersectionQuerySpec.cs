namespace XGArcade.DataSync.Wikidata;

// S-100 (docs/backlog.md): the (CategoryType, CategoryType)-keyed shape
// IntersectionQuerySpecs.ByCategoryPair holds one of per migrated
// Query*IntersectionAsync pair -- see WikidataClient.QueryIntersectionAsync
// for the shared driver that looks a spec up and runs it, and
// WikidataClient.BuildIntersectionQuery for the shared SELECT/WHERE shape
// BuildCandidateClauses' output gets embedded into. QueryKind is the same
// short hyphenated tag (e.g. "country-club") RunIntersectionQueryAsync has
// always used in its log lines and WikidataQueryException messages -- kept
// as an explicit spec field, not derived from TypeA/TypeB, so an existing
// log/exception string can't silently change wording as a side effect of
// this refactor.
internal delegate string IntersectionCandidateClauseBuilder(string qidA, string qidB);

internal sealed record IntersectionQuerySpec(
    CategoryType TypeA,
    CategoryType TypeB,
    string QueryKind,
    IntersectionCandidateClauseBuilder BuildCandidateClauses);

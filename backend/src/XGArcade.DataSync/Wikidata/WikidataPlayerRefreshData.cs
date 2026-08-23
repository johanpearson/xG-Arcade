namespace XGArcade.DataSync.Wikidata;

// REQ-513 (GitHub issue #239): WikidataClient.QueryPlayerRefreshDataByQidAsync's
// return shape — the single-QID admin-refresh counterpart of
// WikidataPlayerMatch's FullName/Position/BirthYear/PhotoUrl bindings
// (REQ-214/REQ-1207/S-082), re-fetched later for one already-known Player
// row rather than discovered fresh via an intersection query.
//
// Every field is independently nullable, including FullName — unlike
// WikidataPlayerCareerLookupResult (REQ-509/510), which treats a missing
// label as "no match at all" and returns null for the whole result, this
// record's caller (AdminEndpoints' refresh-from-wikidata endpoint) needs a
// non-null result even when Wikidata's response has NO binding for a given
// property at all, since REQ-513's own per-field diff rule is "a null/
// missing Wikidata binding for a field never overwrites the existing
// Player value" — the caller decides that per field, not this record. A
// null field here means exactly "this property has no current binding on
// Wikidata for this QID," never an error (see
// SparqlResponseParsers.ParsePlayerRefreshDataBinding's own doc comment for
// the parsing side of that contract).
public record WikidataPlayerRefreshData(string? FullName, string? Position, int? BirthYear, string? PhotoUrl);

namespace XGArcade.DataSync.Wikidata;

// One row of QueryPlayerPoolBirthYearAsync's bulk-import result set (S-032,
// ADR-0007) — deliberately narrower than WikidataPlayerMatch: no club/P54
// data (that's PlayerAttribute's job, not PlayerNameIndex's), and no photo
// (dropped 2026-07-18 — the autocomplete contract never exposes one, so
// fetching P18 was pure query cost; see WikidataClient's query-builder
// comment).
public record WikidataNameIndexEntry(
    string WikidataQid,
    string FullName,
    int? BirthYear,
    string? Nationality);

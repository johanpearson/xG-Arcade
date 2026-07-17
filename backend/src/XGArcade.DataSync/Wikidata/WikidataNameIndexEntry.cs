namespace XGArcade.DataSync.Wikidata;

// One row of QueryPlayerPoolPageAsync's bulk-import result set (S-032,
// ADR-0007) — deliberately narrower than WikidataPlayerMatch: no club/P54
// data (that's PlayerAttribute's job, not PlayerNameIndex's).
public record WikidataNameIndexEntry(
    string WikidataQid,
    string FullName,
    int? BirthYear,
    string? Nationality,
    string? PhotoUrl);

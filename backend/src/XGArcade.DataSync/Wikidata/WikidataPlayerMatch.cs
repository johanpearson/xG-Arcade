namespace XGArcade.DataSync.Wikidata;

// One row of the intersection query's result set (implementation-document.md
// §6a) — a player satisfying both the country and club category values,
// with whatever skos:altLabel aliases Wikidata returned in the same query.
public record WikidataPlayerMatch(string WikidataQid, string FullName, IReadOnlyList<string> Aliases);

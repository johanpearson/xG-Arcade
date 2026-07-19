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
public record WikidataPlayerMatch(string WikidataQid, string FullName, IReadOnlyList<string> Aliases, string? PhotoUrl = null);

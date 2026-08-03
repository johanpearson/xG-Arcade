namespace XGArcade.DataSync.Wikidata;

// REQ-216/ADR-0057: WikidataClient.QueryPlayerPhotoByNameAsync's return
// shape — a name-based counterpart to QueryPlayerPhotosByQidsAsync's
// by-QID batch lookup, for the one case that method can't serve: a wrong
// guess string that never resolved to a Player row (and so has no
// WikidataQid to look up by), but per PlayerNameIndex (ADR-0007) genuinely
// names a real footballer.
//
// FullName is Wikidata's own canonical label (?playerLabel), never the raw
// as-typed guess text — REQ-216 is explicit that it never revives showing
// the as-typed string, since it isn't necessarily a real player's canonical
// name. PhotoUrl is independently nullable — a resolved player with no P18
// statement is a normal, valid outcome, never an error.
public record WikidataPlayerPhotoLookupResult(string FullName, string? PhotoUrl);

namespace XGArcade.DataSync.Wikidata;

// REQ-509/REQ-510 (S-090): WikidataClient.QueryPlayerCareerAndNationalityByNameAsync's
// return shape — the admin-review counterpart of WikidataPlayerPhotoLookupResult
// (REQ-216/ADR-0057), which only ever needed a name+photo. This one needs
// enough to both DISPLAY the fetch for an admin to compare against a
// suggestion's claim (FullName/Nationality/Clubs) and to WRITE it (WikidataQid
// — the key IPlayerStoreRepository.GetOrCreatePlayersByWikidataQidAsync needs
// to resolve/create the local Player row a commit writes PlayerAttribute/
// PlayerOverride rows against).
//
// FullName is Wikidata's own canonical label (?playerLabel), never the
// as-typed suggestion/search text — same "never revive the as-typed string"
// discipline as WikidataPlayerPhotoLookupResult.FullName.
//
// Nationality is a single label string (P27 "country of citizenship"),
// nullable — a player with no P27 statement is a normal, valid outcome, never
// an error. Deliberately a plain string, not a WikidataQid: REQ-509/510's own
// acceptance criteria only need it displayed and written as a PlayerOverride
// Value (a free-text field, same as every other PlayerOverride.Value in this
// codebase), never re-resolved as a QID by anything downstream.
//
// Clubs is the player's FULL club-membership history (REQ-113's "ever played
// for, at any career point" definition) — every non-deprecated P54 statement,
// not just the current/best-rank one, same full-statement-path fetch
// (p:P54/ps:P54) ADR-0054's QueryPlayerCareerStintsByQidsAsync already uses.
//
// A plain list of distinct club-name strings, NOT WikidataCareerStintEntry
// (bug fix, 2026-08-08, REQ-509/510): this method's only caller
// (AdminSuggestionEndpoints) only ever reads a club name off each entry —
// CommitPlayerDataRequest.Clubs itself is IReadOnlyList<string>, and a
// commit only ever writes PlayerAttribute rows keyed on club NAME. The
// original version reused WikidataCareerStintEntry (whose StartYear is
// non-nullable, by design, for ADR-0054's xG Path stint log — see that
// record's own doc comment), which forced ParsePlayerCareerAndNationalityByNameBindings
// to gate club detection on the SPARQL row's ?startTime qualifier also
// being bound. Not every real P54 statement carries a P580 start-time
// qualifier — plenty of lesser-known clubs (exactly the data-completeness
// gap this admin feature exists to help fill, per MVP-SCOPE.md) have the
// membership fact recorded with no start/end date at all — so that gate
// silently dropped those clubs from every lookup. Using a plain string
// list here removes the need for a sentinel StartYear value (and the
// false invariant it would otherwise imply) without touching
// WikidataCareerStintEntry's own non-nullable contract, which
// QueryPlayerCareerStintsByQidsAsync's callers (PlayerCareerStintRefreshService/
// PlayerCareerPrefetchService) genuinely rely on for chronological
// ordering.
public record WikidataPlayerCareerLookupResult(
    string WikidataQid,
    string FullName,
    string? Nationality,
    IReadOnlyList<string> Clubs);

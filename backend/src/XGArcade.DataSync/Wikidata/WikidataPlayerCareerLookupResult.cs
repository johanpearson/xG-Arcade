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
// Reuses WikidataCareerStintEntry rather than introducing a new record —
// same (ClubName, StartYear, EndYear, AppearanceCount, ClubQid) shape is
// exactly what's needed here too, and this method's caller (AdminSuggestionEndpoints)
// only ever reads ClubName off it.
public record WikidataPlayerCareerLookupResult(
    string WikidataQid,
    string FullName,
    string? Nationality,
    IReadOnlyList<WikidataCareerStintEntry> Clubs);

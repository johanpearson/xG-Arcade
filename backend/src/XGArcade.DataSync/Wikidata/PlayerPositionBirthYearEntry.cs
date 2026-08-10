namespace XGArcade.DataSync.Wikidata;

// One QID's worth of QueryPlayerPositionsAndBirthYearsByQidsAsync's result
// (REQ-1207 backfill, bug-bundle fix 2026-08-02) — the by-QID batch
// counterpart of WikidataPlayerMatch.Position/BirthYear (REQ-1207/S-082),
// used by PlayerPositionBirthYearBackfillService the same way
// QueryPlayerPhotosByQidsAsync's plain string result is used by
// PlayerPhotoBackfillService. Both fields independently nullable — a QID
// can resolve a position but not a birth year, or vice versa (or, in
// practice, almost always both, since every Player row already satisfied
// ADR-0025's mandatory-P569 pool filter at creation time; OPTIONAL is still
// used for both bindings for the same defensive reasons every other OPTIONAL
// binding in this file is, not because either is expected to be commonly
// absent).
public record PlayerPositionBirthYearEntry(string? Position, int? BirthYear);

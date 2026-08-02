namespace XGArcade.DataSync.Wikidata;

// One P54 ("member of sports team") statement's worth of
// QueryPlayerCareerStintsByQidsAsync's result (ADR-0054) — the full-career
// counterpart of CareerStintQualifiers (WikidataPlayerMatch.cs), which only
// ever carries qualifiers for the ONE club a country-club/national-team-club/
// trophy-club intersection query was scoped to. This record additionally
// carries ClubName, since a full-career fetch has no caller-supplied club to
// fall back on — every club in the player's history is discovered from the
// response itself, not known in advance.
//
// StartYear is non-nullable for the same reason CareerStintQualifiers'
// is: a row is only ever constructed when Wikidata's P580 ("start time")
// qualifier was actually bound (see WikidataClient.ParseCareerStintBindings).
public record WikidataCareerStintEntry(string ClubName, int StartYear, int? EndYear, int? AppearanceCount);

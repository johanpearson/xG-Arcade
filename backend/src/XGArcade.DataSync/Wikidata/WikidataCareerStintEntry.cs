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
//
// ClubQid (bug fix, 2026-08-04, xG Path duplicate-node bug, REQ-1203
// follow-up): the underlying Wikidata QID for ClubName's ?club binding,
// added alongside the previously-existing four fields as a trailing
// optional parameter (default null) — same "purely additive, every
// existing caller/test untouched" shape this codebase already uses
// elsewhere (see IWikidataClient's onTechnicalFailure/timeoutTier params
// for the same pattern). ClubName alone is only ever a best-effort,
// Wikidata-raw-label-derived string (run through XGArcade.Data
// .ClubNameNormalizer.StripLegalSuffix, nothing more) — it is NOT guaranteed
// to match the hand-seeded ClubDefinition.Name for the same real club,
// since Wikidata's own preferred label for a QID can differ from this
// codebase's seed data by more than a legal-suffix token (e.g. "Lyon" vs.
// "Olympique Lyonnais", both valid labels for the same QID). ClubQid is
// what lets a caller with access to ClubDefinition (PlayerCareerStintRefreshService/
// PlayerCareerPrefetchService — this record's own assembly deliberately
// does NOT have that access, see WikidataClient's COMP-07 boundary)
// canonicalize ClubName to the seeded name when the QID matches a seeded
// club, falling back to this best-effort label when it doesn't (an
// unseeded club — still worth keeping for xG Path's own display and for
// ClubGapAuditService's gap detection). Null only when the query response
// itself didn't carry a resolvable ?club binding for this row (should not
// happen in production — ?club is a mandatory, non-OPTIONAL match — but
// tolerated defensively; see ParseCareerStintBindings' own comment).
public record WikidataCareerStintEntry(string ClubName, int StartYear, int? EndYear, int? AppearanceCount, string? ClubQid = null);

namespace XGArcade.Data.Repositories;

// ADR-0069: one existing PlayerCareerStint row that must be updated in
// place (EndYear/AppearanceCount overwritten) because a freshly-fetched
// Wikidata entry reported the stint has concluded since it was last
// observed (an EndYear: null -> non-null transition under the
// (ClubName, StartYear) match key). Identified by StintId (the row's own
// Id), not by (ClubName, StartYear) alone — the reconciliation plan this
// is built from (PlayerCareerStintRefreshService.BuildNewStintsByPlayerId)
// reads existing rows via GetCareerStintsByPlayerIdsAsync's AsNoTracking
// query, but AddCareerStintsBatchAsync must apply the update against its
// own, separately-loaded TRACKED query — Id is what lets that tracked
// query re-locate the exact same entity to mutate. AppearanceCount may
// legitimately be null here (Wikidata's P1350 qualifier is OPTIONAL) —
// that's a real closing value, not a signal the closure should be skipped.
public sealed record CareerStintClosure(Guid StintId, int EndYear, int? AppearanceCount);

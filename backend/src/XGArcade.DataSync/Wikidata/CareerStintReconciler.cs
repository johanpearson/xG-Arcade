using XGArcade.Data.Entities;

namespace XGArcade.DataSync.Wikidata;

// S-187 follow-up (REQ-1203, 2026-08-29, architecture-reviewer finding —
// "close the third duplicate-stint door"): the single narrowed-key-and-
// complete-in-place decision PlayerCareerStintRefreshService
// .BuildNewStintsByPlayerId introduced, extracted so WikidataLookupService
// .PersistCareerStintsAsync can share the exact same rule instead of
// carrying a third, near-identical copy — the two callers have genuinely
// different input shapes (WikidataCareerStintEntry with its own per-entry
// ClubName/ClubQid vs. CareerStintQualifiers scoped to one caller-supplied
// clubName, batched differently, keyed differently), so this primitive is
// deliberately narrower than either caller's own loop: it knows nothing
// about PlayerId, batching, or which Wikidata record type produced the
// candidate — just one player's already-narrowed existing-stints-by-key
// lookup plus one candidate stint's four scalar fields in, one of three
// outcomes out. See BuildNewStintsByPlayerId's own doc comment for the full
// "why" behind the (ClubName, StartYear) key choice and its deliberate
// limits (never revisits a stored StartYear or ClubName itself — only
// completes EndYear/AppearanceCount on an already-correct row).
internal static class CareerStintReconciler
{
    internal enum Outcome
    {
        // Identical to what's already stored for this (ClubName, StartYear)
        // — nothing to write.
        NoOp,
        // No existing row matches (ClubName, StartYear) — a genuinely new
        // stint, insert it.
        Insert,
        // An existing row matches (ClubName, StartYear) but its EndYear/
        // AppearanceCount differ from the fetched values — complete that row
        // in place (ExistingStintId identifies which one), never insert a
        // second row for it.
        Complete,
    }

    internal readonly record struct Decision(Outcome Outcome, Guid ExistingStintId = default);

    // existingByKey: ONE player's existing stints, keyed by (ClubName,
    // StartYear) with first-wins on a same-key collision — same "arbitrary
    // but harmless" tolerance BuildNewStintsByPlayerId's own comment
    // documents (a player cannot really start two real spells at the same
    // club in the same year). Building this dictionary from a player's full
    // existing-stint list is each caller's own responsibility (they already
    // have that list in hand for their own reasons); this method only ever
    // reconciles one candidate stint against it.
    internal static Decision Reconcile(
        IReadOnlyDictionary<(string ClubName, int StartYear), PlayerCareerStint> existingByKey,
        string clubName, int startYear, int? endYear, int? appearanceCount)
    {
        if (!existingByKey.TryGetValue((clubName, startYear), out var existingStint))
            return new Decision(Outcome.Insert);

        return existingStint.EndYear == endYear && existingStint.AppearanceCount == appearanceCount
            ? new Decision(Outcome.NoOp)
            : new Decision(Outcome.Complete, existingStint.Id);
    }
}

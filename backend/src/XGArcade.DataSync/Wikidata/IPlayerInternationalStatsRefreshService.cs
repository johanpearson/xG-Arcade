namespace XGArcade.DataSync.Wikidata;

// REQ-1501/REQ-1506 (xG Higher/Lower, S-231, ADR-0112): a batch
// international-caps/international-goals refresh for a GIVEN list of
// player IDs — mirrors IPlayerCareerStintRefreshService's exact shape and
// contract (ADR-0112's Decision, point 3), including the same
// throwOnFailure escape hatch. Interface exists for the same reason as
// IPlayerCareerStintRefreshService: so a future caller's own tests can
// substitute a hand-rolled fake.
//
// Deliberately NOT called per-round by HigherLowerGenerationService the way
// XGPathGameModule calls IPlayerCareerStintRefreshService just-in-time for
// a small, already-known target list — see ADR-0112's Context and this
// story's own task description for why: xG Higher/Lower's candidate pool
// must already be populated BEFORE generation runs, the same precondition
// "club"/"trophy" PlayerAttribute rows already have. The only production
// caller of this method is PlayerInternationalStatsBackfillService, which
// drives it across the FULL existing player pool via the
// `dotnet run -- backfill-player-international-stats` CLI verb (ADR-0024) —
// see that class's own doc comment for the full "why a driving loop on top
// of this batch-by-IDs unit" reasoning.
public interface IPlayerInternationalStatsRefreshService
{
    // Never throws unless throwOnFailure is true, in which case a Wikidata
    // technical failure is rethrown as WikidataQueryException instead of
    // being logged and swallowed — see IPlayerCareerStintRefreshService's
    // own doc comment on throwOnFailure for the identical shape this
    // mirrors. With the default false, a Wikidata failure for some or all
    // of playerIds simply leaves those players with whatever
    // international-caps/international-goals PlayerAttribute rows they
    // already had (none, for a first run) — never worse than before this
    // call. PlayerInternationalStatsBackfillService passes true, so it can
    // distinguish "this batch's Wikidata call itself failed" from "these
    // players genuinely have no qualifying national-team P54 statement" for
    // its own batchesFailed reporting, the same distinction
    // PlayerPositionBirthYearBackfillService's own per-batch try/catch
    // already makes around its own Wikidata call.
    //
    // Never overwrites a player who already has an "international-caps"
    // PlayerAttribute row (same "set once, never revisited" posture as
    // REQ-1207's Position/BirthYear backfill) — this method assumes its
    // caller has already filtered playerIds to ones actually missing the
    // data (PlayerInternationalStatsBackfillService's own
    // GetPlayersMissingInternationalStatsAsync cursor), but re-checks
    // defensively rather than trusting that unconditionally, since a
    // future caller passing an already-processed player's ID must not
    // silently clobber an admin PlayerOverride's effective value or
    // duplicate a PlayerAttribute row. See PlayerInternationalStatsRefreshService's
    // own doc comment for why this is a deliberate limitation (real-world
    // caps/goals DO change over time, unlike Position/BirthYear) rather
    // than an oversight — flagged as a Follow-up, not fixed here.
    Task RefreshInternationalStatsAsync(
        IReadOnlyList<Guid> playerIds, bool throwOnFailure = false, CancellationToken cancellationToken = default);
}

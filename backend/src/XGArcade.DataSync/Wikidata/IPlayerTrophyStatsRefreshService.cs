namespace XGArcade.DataSync.Wikidata;

// REQ-1501 (xG Higher/Lower, S-232, ADR-0113): a batch trophy-holder refresh
// for a GIVEN list of player IDs — mirrors IPlayerInternationalStatsRefreshService's
// exact shape and contract (ADR-0113's Decision), including the same
// throwOnFailure escape hatch. Interface exists for the same reason as
// IPlayerInternationalStatsRefreshService: so a future caller's own tests can
// substitute a hand-rolled fake.
//
// Deliberately NOT called per-round by HigherLowerGenerationService the way
// XGPathGameModule calls IPlayerCareerStintRefreshService just-in-time for a
// small, already-known target list — same reasoning ADR-0112's own Context
// gives for international-caps/goals: xG Higher/Lower's candidate pool must
// already be populated BEFORE generation runs. The only production caller of
// this method is PlayerTrophyStatsBackfillService, which drives it across
// the FULL existing player pool via the `dotnet run --
// backfill-player-trophy-stats` CLI verb (ADR-0024) — see that class's own
// doc comment for the full "why a driving loop on top of this batch-by-IDs
// unit" reasoning.
public interface IPlayerTrophyStatsRefreshService
{
    // Never throws unless throwOnFailure is true, in which case a Wikidata
    // technical failure is rethrown as WikidataQueryException instead of
    // being logged and swallowed — see IPlayerInternationalStatsRefreshService's
    // own doc comment on throwOnFailure for the identical shape this
    // mirrors. With the default false, a Wikidata failure for some or all of
    // playerIds simply leaves those players with whatever "trophy"
    // PlayerAttribute rows they already had (none, or only the byproduct
    // path's, for a first run) — never worse than before this call.
    // PlayerTrophyStatsBackfillService passes true, so it can distinguish
    // "this batch's Wikidata call itself failed" from "these players
    // genuinely won none of the seeded trophies" for its own batchesFailed
    // reporting.
    //
    // Never overwrites a (player, trophy) pair that already has a "trophy"
    // PlayerAttribute row — this includes rows written by
    // WikidataLookupService's existing byproduct path (xG Grid's own
    // candidate-search queries), which predates this service and has no
    // corresponding PlayerData.TrophyStatsCheckedField marker; this method
    // treats that as "already have this specific trophy," not "never
    // checked," and never writes a duplicate row for the same pair. Writes
    // PlayerData.TrophyStatsCheckedField for EVERY player actually queried
    // in a successfully-queried batch, regardless of whether they won
    // anything — see PlayerData.TrophyStatsCheckedField's own doc comment
    // for why this is not optional (ADR-0113's whole reason for existing).
    Task RefreshTrophyStatsAsync(
        IReadOnlyList<Guid> playerIds, bool throwOnFailure = false, CancellationToken cancellationToken = default);
}

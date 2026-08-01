namespace XGArcade.Data.Entities;

// REQ-110 (2026-08-01 "persistent technical-failure tracking" extension,
// ADR-0052): the mirror-image gap ConfirmedLowMatchPair (ADR-0050) left
// open. A pair whose live Wikidata query ends in a technical failure (WDQS
// timeout, HTTP error, JSON parse error) has no persisted trace at all, so
// PlayerCacheWarmingService.WarmAsync re-attempts it, at full cost, on
// every single future run — forever, if the failure is structural rather
// than transient. That's exactly what happened 2026-07-28 through
// 2026-08-01: a handful of club-club pairs whose query shape produced a
// combinatorial row explosion (see WikidataClient.BuildClubClubIntersectionQuery's
// own comment) failed on every run, and the job's 90-minute budget was
// eaten by re-fighting the same doomed pairs instead of making progress.
//
// Unlike ConfirmedLowMatchPair (a confirmed FACT about Wikidata's data —
// "this pair genuinely has fewer than MinValidAnswers matches"), this table
// is a confirmed fact about this codebase's own query reliability against a
// pair — a query that might well succeed tomorrow, against a different
// WDQS load or after a query-shape fix. That distinction is why this is a
// separate table with its own (run-scoped, resettable) counter rather than
// folded into ConfirmedLowMatchPair's binary presence/absence signal — see
// ADR-0052's alternatives table.
//
// One row per pair that has failed at least once. ConsecutiveFailureCount
// increments on every additional run-level technical failure and the row
// is deleted entirely the moment a run gets a real answer (a match, or a
// genuine confirmed-low) for that pair — see
// IPlayerStoreRepository.ClearTechnicalFailureAsync. "Consecutive" is
// run-scoped, not attempt-scoped: PlayerCacheWarmingService no longer
// retries within a single run (the same-run retry itself was the direct
// cause of the 2026-07-28 regression this extension fixes — see
// PlayerCacheWarmingService's own comment on MaxAttemptsPerPair's removal).
// PlayerCacheWarmingService only skips a pair once ConsecutiveFailureCount
// reaches PersistentFailureThreshold, so a single transient blip (a one-off
// WDQS 502) never permanently starves a pair that would otherwise resolve
// fine on the very next run.
//
// Same composite-key shape and invalidation surface as ConfirmedLowMatchPair
// (StaleClubAttributeCleaner, purge-player-pool) — see that entity's own
// doc comment for the shared reasoning; a stale technical-failure record
// left over from before a QID correction or query-shape fix must not
// silently suppress the real re-check either.
public class PairLookupFailure
{
    public required string FirstAttributeType { get; set; }
    public required string FirstAttributeValue { get; set; }
    public required string SecondAttributeType { get; set; }
    public required string SecondAttributeValue { get; set; }

    public int ConsecutiveFailureCount { get; set; }

    public DateTime LastFailedAt { get; set; }
}

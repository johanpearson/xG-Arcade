namespace XGArcade.Games.XGGrid;

// REQ-110. See PlayerCacheWarmingService's own doc comment for the full
// rationale (why this exists, why it's a CLI verb and not an endpoint).
public interface IPlayerCacheWarmingService
{
    Task<CacheWarmingResult> WarmAsync(CancellationToken cancellationToken = default);
}

// REQ-110 (2026-07-28 extension): PairsWithTechnicalFailure/FailingPairs
// distinguish a live-queried pair whose Wikidata lookup ended in a technical
// failure (WDQS timeout, HTTP error, or JSON parse error — swallowed to an
// empty match list by WikidataClient's throwOnTimeout=false contract) from
// one that queried successfully and was simply found to be genuinely below
// MinValidAnswers. Both kinds of pair are still counted in PairsQueriedLive
// (that count's meaning — "a live lookup was attempted" — is unchanged);
// PairsWithTechnicalFailure is a subset of it, not a replacement metric.
// FailingPairs names each one ("{A} x {B}", matching this codebase's
// existing per-pair log convention) so an operator reading the run summary
// can tell which pairs are worth investigating. See docs/requirements-document.md's
// REQ-110 "Extended (2026-07-28)" entry for the full incident this
// responds to. As of the 2026-08-01 extension there is no same-run retry —
// a pair counted here failed on its one attempt this run (see
// PlayerCacheWarmingService's own comment on why the same-run retry was
// removed); repeated failures across separate runs are what
// PairsSkippedPersistentFailure below tracks instead.
//
// PairsSkippedConfirmedLow (REQ-110, 2026-07-28 "persisted confirmed-low
// signal" extension): pairs skipped without any live query because a prior
// run already confirmed them genuinely below MinValidAnswers (see
// ConfirmedLowMatchPair's own doc comment). A separate counter from
// PairsAlreadyValid — that one means "already has enough real matches
// cached to answer a grid," this one means "cached-but-confirmed-low, not
// worth re-checking yet."
//
// PairsSkippedPersistentFailure (REQ-110, 2026-08-01 "persistent
// technical-failure tracking" extension, ADR-0052): pairs skipped without
// any live query because at least PersistentFailureThreshold consecutive
// runs already ended in a technical failure for this pair (see
// PairLookupFailure's own doc comment). A separate counter from both
// PairsSkippedConfirmedLow (that one means "Wikidata answered, genuinely
// below threshold"; this one means "the query itself keeps failing,
// treated as structural, needs an operator or a query-shape fix") and from
// PairsWithTechnicalFailure (that one is a live query THIS run that failed;
// this one is a pair skipped WITHOUT a live query this run, because past
// runs already failed enough times).
//
// PairsConfirmedLowFromSweep (REQ-110/ADR-0078/S-160, 2026-08-18): pairs
// confirmed below MinValidAnswers and written to ConfirmedLowMatchPair
// directly from the local cached count, without any live Wikidata query,
// because BOTH sides of the pair (CountryDefinition/ClubDefinition) have a
// non-null PlayerPoolSweptAt — see PlayerCareerPrefetchService's own doc
// comment for what sets that column, and PlayerCacheWarmingService.WarmAsync
// for the read side. Distinct from PairsSkippedConfirmedLow (that one is a
// pair a PRIOR run already confirmed low, skipped this run without
// recomputing anything) — this one is a pair confirmed low FOR THE FIRST
// TIME (or re-confirmed) THIS run, using this run's own freshly-computed
// cachedCount, purely because the pool sweep already makes that count
// final. Checked before IsConfirmedLowAsync in WarmAsync's own branch
// order, so a pair that qualifies for both counts as
// PairsConfirmedLowFromSweep, not PairsSkippedConfirmedLow.
//
// TotalPairs still equals PairsQueriedLive + PairsAlreadyValid +
// PairsSkippedConfirmedLow + PairsSkippedPersistentFailure +
// PairsConfirmedLowFromSweep, same exhaustive-partition shape the result
// already had before this field existed.
public record CacheWarmingResult(
    int TotalPairs,
    int PairsQueriedLive,
    int PairsAlreadyValid,
    int PairsWithTechnicalFailure,
    IReadOnlyList<string> FailingPairs,
    int PairsSkippedConfirmedLow = 0,
    int PairsSkippedPersistentFailure = 0,
    int PairsConfirmedLowFromSweep = 0);

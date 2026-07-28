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
// can tell which pairs are worth re-running versus which are confirmed
// low. See docs/requirements-document.md's REQ-110 "Extended (2026-07-28)"
// entry for the full incident this responds to.
public record CacheWarmingResult(
    int TotalPairs,
    int PairsQueriedLive,
    int PairsAlreadyValid,
    int PairsWithTechnicalFailure,
    IReadOnlyList<string> FailingPairs);

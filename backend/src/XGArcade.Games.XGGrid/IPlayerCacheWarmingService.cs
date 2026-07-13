namespace XGArcade.Games.XGGrid;

// REQ-110. See PlayerCacheWarmingService's own doc comment for the full
// rationale (why this exists, why it's a CLI verb and not an endpoint).
public interface IPlayerCacheWarmingService
{
    Task<CacheWarmingResult> WarmAsync(CancellationToken cancellationToken = default);
}

public record CacheWarmingResult(int TotalPairs, int PairsQueriedLive, int PairsAlreadyValid);

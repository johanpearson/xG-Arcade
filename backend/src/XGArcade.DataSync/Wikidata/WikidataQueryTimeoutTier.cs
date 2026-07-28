namespace XGArcade.DataSync.Wikidata;

// REQ-110 (2026-07-28 "cache-warming-specific timeout" extension): a third
// way to select WikidataClient.RunIntersectionQueryAsync's per-call timeout,
// alongside (not replacing) the existing throwOnTimeout switch. Before this,
// throwOnTimeout doubled as the timeout-tier selector (see
// RunIntersectionQueryAsync's own comment on that history) — that worked
// while there were only two tiers (round generation's 15s, ADR-0046's 28s
// guess-time fallback) because throwOnTimeout also happened to be exactly
// "is this the guess-time fallback." Cache warming breaks that coincidence:
// it needs the longer budget WITHOUT throwing (its swallow-to-[] contract is
// unchanged — see PlayerCacheWarmingService), so a two-way bool can no longer
// carry both decisions.
//
// A trailing optional parameter (default Default) on every intersection
// Query*Async method, additive/backward-compatible the same way
// onTechnicalFailure was added in the prior 2026-07-28 extension — every
// existing caller (grid generation's Sync-origin lookups, REQ-211's
// guess-time fallback, every test in WikidataClientTests.cs) keeps compiling
// and behaving identically without passing this parameter at all.
public enum WikidataQueryTimeoutTier
{
    // Unchanged behavior: RunIntersectionQueryAsync resolves the timeout
    // exactly as it always has, keyed off throwOnTimeout (false ->
    // _queryTimeout/15s, REQ-103; true -> _guessTimeFallbackQueryTimeout/28s,
    // ADR-0046). Neither of those two timeouts changes value or selection
    // logic because of this enum's addition.
    Default = 0,

    // REQ-110: PlayerCacheWarmingService's own, longer, third tier — see
    // WikidataClient's _cacheWarmingQueryTimeout field for the chosen value
    // and its justification. Always paired with throwOnTimeout: false (cache
    // warming never throws on a timeout — same fail-open contract as
    // Default/false); this tier only widens which timeout window applies,
    // it has no effect on the throw-vs-swallow decision, which
    // throwOnTimeout alone still controls.
    CacheWarming,
}

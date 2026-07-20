namespace XGArcade.DataSync.Wikidata;

// REQ-502/503: distinguishes why a Wikidata lookup was run. ADR-0029
// originally made PlayerData.Confidence depend on this value (Sync started
// "verified", GuessTimeFallback started "unverified"); ADR-0032 reversed
// that split — both origins now start Confidence = "verified"
// (WikidataLookupService.ConfidenceFor). This enum and its two call sites
// are kept regardless, for logging/debugging/future re-differentiation —
// see ADR-0032's "For AI agents" note before reintroducing a per-origin
// Confidence split.
public enum WikidataLookupOrigin
{
    // A routine grid-generation cache-miss (GridGameModule.GetMatchCountAsync)
    // or an explicit cache-warming pass (PlayerCacheWarmingService) — the
    // same vetted per-category SPARQL intersection query either way.
    Sync,

    // REQ-211/ADR-0018's guess-time fallback (GridGameModule
    // .RefreshCellFromLiveLookupAsync) — re-checks a single already-generated
    // cell against a specific player's guess, not the original vetted
    // per-category intersection.
    GuessTimeFallback,
}

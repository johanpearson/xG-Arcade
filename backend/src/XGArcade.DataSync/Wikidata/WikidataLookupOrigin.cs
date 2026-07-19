namespace XGArcade.DataSync.Wikidata;

// REQ-502/503 revision (see ADR-0029): what PlayerData.Confidence a
// persisted Wikidata lookup starts at depends on why it was run, not just
// that it came from Wikidata.
public enum WikidataLookupOrigin
{
    // A routine grid-generation cache-miss (GridGameModule.GetMatchCountAsync)
    // or an explicit cache-warming pass (PlayerCacheWarmingService) — the
    // same vetted per-category SPARQL intersection query either way.
    // Tier 0's "Wikidata-first" design already treats this as ground truth
    // (MVP-SCOPE.md), so it starts Confidence = "verified".
    Sync,

    // REQ-211/ADR-0018's guess-time fallback (GridGameModule
    // .RefreshCellFromLiveLookupAsync) — re-checks a single already-generated
    // cell against a specific player's guess, not the original vetted
    // per-category intersection. Kept Confidence = "unverified" so an admin
    // can still spot-check this narrower, guess-triggered case.
    GuessTimeFallback,
}

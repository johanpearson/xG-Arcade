namespace XGArcade.Data.Entities;

// Category value reference table (ADR-0012, REQ-109) — grid generation
// picks candidate values from this table directly, never derives them ad
// hoc from PlayerAttribute. Tier 0 scope only: Name + WikidataQid, hand
// seeded (~15 rows, MVP-SCOPE.md); ApiFootballTeamId and the incremental
// admin-add resolution flow are Tier 1 (ADR-0012) — not added until then.
public class ClubDefinition
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? WikidataQid { get; set; }   // nullable until resolved

    // REQ-110/ADR-0078/S-160: mirrors CountryDefinition.PlayerPoolSweptAt's
    // own doc comment — set by PlayerCareerPrefetchService's clubsProcessed++
    // success path, checked by PlayerCacheWarmingService alongside the
    // paired CountryDefinition/ClubDefinition's own PlayerPoolSweptAt before
    // skipping a live Wikidata lookup. Unlike CountryDefinition's column,
    // this one has TWO invalidation sites, not one: StaleClubAttributeCleaner
    // (REQ-111, CleanAsync/CleanAllSeededClubsAsync) nulls it alongside the
    // PlayerAttribute/PlayerData/ConfirmedLowMatchPair/PairLookupFailure
    // rows it already clears for a corrected club, and purge-player-pool
    // (REQ-112/S-038) nulls it at full-reset scope. See ADR-0078's "For AI
    // agents" section before adding a third invalidation site.
    public DateTime? PlayerPoolSweptAt { get; set; }
}

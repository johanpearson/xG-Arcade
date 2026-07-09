using XGArcade.Data.Entities;

namespace XGArcade.DataSync.Wikidata;

// Orchestrates the "DataSync.Clients: live lookup -> Data.PlayerStore:
// persist as unverified" arrow in architecture-document.md §6.1 — the part
// of REQ-103 grid generation (S-007) will call on a cache miss.
public interface IWikidataLookupService
{
    // Returns the players persisted (new or already-known) for this
    // country/club combination — empty if either value has no resolved
    // WikidataQid yet (REQ-109), or if the query timed out/errored/found
    // no match (REQ-103). Never throws for those cases.
    Task<IReadOnlyList<Player>> LookupAndPersistAsync(
        CountryDefinition country,
        ClubDefinition club,
        CancellationToken cancellationToken = default);
}

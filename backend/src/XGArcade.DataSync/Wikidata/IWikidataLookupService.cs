using XGArcade.Data.Entities;

namespace XGArcade.DataSync.Wikidata;

// Orchestrates the "DataSync.Clients: live lookup -> Data.PlayerStore:
// persist as unverified" arrow in architecture-document.md §6.1/6.2 — called
// by REQ-103 grid generation (S-007) on a cache miss, and by REQ-211's
// guess-time fallback (S-011 follow-up, ADR-0018) when cached data doesn't
// already answer a submitted guess.
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

    // S-030: the Club x Club counterpart, same empty-on-unresolved-QID/
    // never-throws contract. Both clubs persist their matched players under
    // AttributeType "club" — with two distinct AttributeValues (clubA.Name,
    // clubB.Name), never the same value twice.
    Task<IReadOnlyList<Player>> LookupAndPersistClubClubAsync(
        ClubDefinition clubA,
        ClubDefinition clubB,
        CancellationToken cancellationToken = default);
}

using XGArcade.Data.Entities;

namespace XGArcade.DataSync.Wikidata;

// Orchestrates the "DataSync.Clients: live lookup -> Data.PlayerStore:
// persist" arrow in architecture-document.md §6.1/6.2 — called by REQ-103
// grid generation (S-007) on a cache miss, and by REQ-211's guess-time
// fallback (S-011 follow-up, ADR-0018) when cached data doesn't already
// answer a submitted guess. The caller-supplied WikidataLookupOrigin is
// still threaded through to the persisted PlayerData (for
// logging/debugging/future re-differentiation), but as of ADR-0032 it no
// longer changes what Confidence the data starts at — see that enum and
// ADR-0032.
public interface IWikidataLookupService
{
    // Returns the players persisted (new or already-known) for this
    // country/club combination — empty if either value has no resolved
    // WikidataQid yet (REQ-109), or if the query timed out/errored/found
    // no match (REQ-103). Never throws for those cases.
    //
    // REQ-114/ADR-0035: internally branches on
    // `country.UsesCountryForSportProperty` to query Wikidata's P1532
    // ("country for sport") instead of the default P27 ("country of
    // citizenship") — England/Scotland/Wales/Northern Ireland aren't
    // sovereign states, so P27 can't distinguish them. Callers (GridGameModule)
    // don't need to know which path was taken; both persist under the same
    // "nationality"/country.Name attribute.
    Task<IReadOnlyList<Player>> LookupAndPersistAsync(
        CountryDefinition country,
        ClubDefinition club,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken = default);

    // S-030: the Club x Club counterpart, same empty-on-unresolved-QID/
    // never-throws contract. Both clubs persist their matched players under
    // AttributeType "club" — with two distinct AttributeValues (clubA.Name,
    // clubB.Name), never the same value twice.
    Task<IReadOnlyList<Player>> LookupAndPersistClubClubAsync(
        ClubDefinition clubA,
        ClubDefinition clubB,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken = default);

    // S-031/REQ-108: the Trophy x Country counterpart, same
    // empty-on-unresolved-QID/never-throws contract. Persists matched
    // players under AttributeType "trophy"/trophy.Name and
    // "nationality"/country.Name.
    Task<IReadOnlyList<Player>> LookupAndPersistTrophyCountryAsync(
        TrophyDefinition trophy,
        CountryDefinition country,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken = default);

    // S-031/REQ-108: the Trophy x Club counterpart, same
    // empty-on-unresolved-QID/never-throws contract. Persists matched
    // players under AttributeType "trophy"/trophy.Name and "club"/club.Name.
    Task<IReadOnlyList<Player>> LookupAndPersistTrophyClubAsync(
        TrophyDefinition trophy,
        ClubDefinition club,
        WikidataLookupOrigin origin,
        CancellationToken cancellationToken = default);
}

using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore), split from IPlayerStoreRepository (S-106, pure
// refactor — see that story's own doc comment/CHANGELOG entry for why): the
// PlayerAttribute concern, including the two attribute-driven queries that
// return Player rows (GetPlayersWithEitherAttributeAsync — it queries
// PlayerAttribute first and is fundamentally an attribute-driven query, not
// a Player-driven one). See IPlayerRepository's own doc comment for the
// shared "no facade" boundary note that applies identically here.
public interface IPlayerAttributeRepository
{
    Task<IReadOnlyList<PlayerAttribute>> GetPlayerAttributesAsync(
        string attributeType, string attributeValue, CancellationToken cancellationToken = default);
    Task AddPlayerAttributeAsync(PlayerAttribute attribute, CancellationToken cancellationToken = default);

    // Bug-bundle fix (2026-07-27): batched counterpart to
    // AddPlayerAttributeAsync — one SaveChangesAsync call for the whole
    // list. Callers are responsible for dedup (same "caller already knows
    // which player/attribute pairs are new" discipline
    // WikidataLookupService's PersistMatchesAsync/QueueAttribute already
    // follows) — this never checks the composite
    // (PlayerId, AttributeType, AttributeValue) key itself.
    Task AddPlayerAttributesBatchAsync(IReadOnlyList<PlayerAttribute> attributes, CancellationToken cancellationToken = default);

    // REQ-209: disambiguation-prompt candidate building
    // (GridGameModule.BuildDisambiguationCandidatesAsync) — every cached
    // PlayerAttribute row for a batch of candidate players in one query,
    // rather than one GetPlayerAttributesAsync-shaped call per candidate.
    // Same bulk-by-player-ids shape as GetPlayerAliasesByPlayerIdsAsync
    // (IPlayerAliasRepository); a playerId with no attribute rows at all is
    // simply absent from the result (not present with an empty list).
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAttribute>>> GetPlayerAttributesByPlayerIdsAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default);

    // Grid generation's candidate-matching query (REQ-101): how many
    // players satisfy both category values at once. A single indexed join
    // rather than fetching both attribute lists and intersecting in memory.
    Task<int> CountPlayersWithBothAttributesAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default);

    // REQ-208: the bounded candidate pool for fuzzy/edit-distance matching
    // (GridGameModule.FindFuzzyCandidatesAsync) — every player already known
    // (via a cached PlayerAttribute row) to satisfy at least one of the
    // cell's two categories. A player satisfying neither category can never
    // be a correct answer for this cell regardless of how close their name
    // is, so this never excludes a genuine match while keeping the fuzzy
    // pass's cost bounded by this cell's own category population rather
    // than a full-table scan.
    Task<IReadOnlyList<Player>> GetPlayersWithEitherAttributeAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default);
}

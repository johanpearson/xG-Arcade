using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore): the only path to PlayerData/PlayerOverride/
// PlayerAttribute/PlayerAlias. Games.XGGrid (COMP-05) and any future game
// module must reach player data only through this interface — see
// architecture-document.md boundary rule 1.
public interface IPlayerStoreRepository
{
    Task<Player?> GetPlayerByWikidataQidAsync(string wikidataQid, CancellationToken cancellationToken = default);
    Task<Player?> GetPlayerByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Player> AddPlayerAsync(Player player, CancellationToken cancellationToken = default);

    // REQ-208 (Tier 0's simple half, MVP-SCOPE.md): guess-time name matching
    // queries Player.NormalizedFullName directly — no PlayerNameIndex/COMP-10
    // (deferred, Tier 1) and no PlayerAlias (also deferred for matching
    // purposes — "defer the alias table" in MVP-SCOPE.md's Tier 0 scoping).
    Task<IReadOnlyList<Player>> GetPlayersByNormalizedFullNameAsync(
        string normalizedFullName, CancellationToken cancellationToken = default);

    Task AddPlayerDataAsync(PlayerData data, CancellationToken cancellationToken = default);

    // REQ-503 (S-012): the admin review view's candidate list — every
    // PlayerData row still awaiting an admin's approve/correct/remove
    // decision.
    Task<IReadOnlyList<PlayerData>> GetUnverifiedPlayerDataAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlayerAttribute>> GetPlayerAttributesAsync(
        string attributeType, string attributeValue, CancellationToken cancellationToken = default);
    Task AddPlayerAttributeAsync(PlayerAttribute attribute, CancellationToken cancellationToken = default);

    // Grid generation's candidate-matching query (REQ-101): how many
    // players satisfy both category values at once. A single indexed join
    // rather than fetching both attribute lists and intersecting in memory.
    Task<int> CountPlayersWithBothAttributesAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlayerAlias>> GetPlayerAliasesAsync(Guid playerId, CancellationToken cancellationToken = default);
    Task AddPlayerAliasAsync(PlayerAlias alias, CancellationToken cancellationToken = default);

    Task<PlayerOverride?> GetOverrideAsync(Guid playerId, string field, CancellationToken cancellationToken = default);
    Task<PlayerOverride?> GetOverrideByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default);
    Task UpdateOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default);
    Task<bool> DeleteOverrideAsync(Guid id, CancellationToken cancellationToken = default);

    // REQ-203: "an override always takes precedence over synced/unverified
    // data" — the single effective-data check every correctness path
    // (grid-generation's cache read is count-only and doesn't need this;
    // guess-checking, S-009, does) must use, so override precedence is
    // enforced in exactly one place (architecture-document.md's Data
    // integrity row).
    Task<bool> HasEffectiveAttributeAsync(
        Guid playerId, string attributeType, string attributeValue, CancellationToken cancellationToken = default);
}

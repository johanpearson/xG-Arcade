using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore): the only path to PlayerData/PlayerOverride/
// PlayerAttribute/PlayerAlias. Games.XGGrid (COMP-05) and any future game
// module must reach player data only through this interface — see
// architecture-document.md boundary rule 1.
public interface IPlayerStoreRepository
{
    Task<Player?> GetPlayerByWikidataQidAsync(string wikidataQid, CancellationToken cancellationToken = default);
    Task<Player> AddPlayerAsync(Player player, CancellationToken cancellationToken = default);

    Task AddPlayerDataAsync(PlayerData data, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlayerAttribute>> GetPlayerAttributesAsync(
        string attributeType, string attributeValue, CancellationToken cancellationToken = default);
    Task AddPlayerAttributeAsync(PlayerAttribute attribute, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlayerAlias>> GetPlayerAliasesAsync(Guid playerId, CancellationToken cancellationToken = default);
    Task AddPlayerAliasAsync(PlayerAlias alias, CancellationToken cancellationToken = default);

    Task<PlayerOverride?> GetOverrideAsync(Guid playerId, string field, CancellationToken cancellationToken = default);
    Task AddOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default);
}

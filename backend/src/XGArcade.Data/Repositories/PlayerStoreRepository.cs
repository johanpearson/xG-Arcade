using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerStoreRepository(XGArcadeDbContext dbContext) : IPlayerStoreRepository
{
    public async Task<Player?> GetPlayerByWikidataQidAsync(string wikidataQid, CancellationToken cancellationToken = default) =>
        await dbContext.Players.AsNoTracking().FirstOrDefaultAsync(p => p.WikidataQid == wikidataQid, cancellationToken);

    public async Task<Player> AddPlayerAsync(Player player, CancellationToken cancellationToken = default)
    {
        dbContext.Players.Add(player);
        await dbContext.SaveChangesAsync(cancellationToken);
        return player;
    }

    public async Task AddPlayerDataAsync(PlayerData data, CancellationToken cancellationToken = default)
    {
        dbContext.PlayerData.Add(data);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlayerAttribute>> GetPlayerAttributesAsync(
        string attributeType, string attributeValue, CancellationToken cancellationToken = default) =>
        await dbContext.PlayerAttributes
            .AsNoTracking()
            .Where(pa => pa.AttributeType == attributeType && pa.AttributeValue == attributeValue)
            .ToListAsync(cancellationToken);

    public async Task AddPlayerAttributeAsync(PlayerAttribute attribute, CancellationToken cancellationToken = default)
    {
        dbContext.PlayerAttributes.Add(attribute);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<PlayerOverride?> GetOverrideAsync(Guid playerId, string field, CancellationToken cancellationToken = default) =>
        await dbContext.PlayerOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.PlayerId == playerId && o.Field == field, cancellationToken);

    public async Task AddOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default)
    {
        dbContext.PlayerOverrides.Add(playerOverride);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

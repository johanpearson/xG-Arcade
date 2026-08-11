using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerAttributeRepository(XGArcadeDbContext dbContext) : IPlayerAttributeRepository
{
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

    public async Task AddPlayerAttributesBatchAsync(IReadOnlyList<PlayerAttribute> attributes, CancellationToken cancellationToken = default)
    {
        if (attributes.Count == 0)
            return;

        dbContext.PlayerAttributes.AddRange(attributes);

        // One SaveChangesAsync call for the whole batch — load-then-
        // SaveChangesAsync (docs/coding-guidelines.md).
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAttribute>>> GetPlayerAttributesByPlayerIdsAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        if (playerIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<PlayerAttribute>>();

        var idList = playerIds.ToList();
        return await GroupByPlayerIdAsync(
            dbContext.PlayerAttributes.Where(pa => idList.Contains(pa.PlayerId)),
            attribute => attribute.PlayerId,
            cancellationToken);
    }

    public async Task<int> CountPlayersWithBothAttributesAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default)
    {
        var firstPlayerIds = dbContext.PlayerAttributes
            .AsNoTracking()
            .Where(pa => pa.AttributeType == firstAttributeType && pa.AttributeValue == firstAttributeValue)
            .Select(pa => pa.PlayerId);

        return await dbContext.PlayerAttributes
            .AsNoTracking()
            .Where(pa => pa.AttributeType == secondAttributeType && pa.AttributeValue == secondAttributeValue)
            .Where(pa => firstPlayerIds.Contains(pa.PlayerId))
            .Select(pa => pa.PlayerId)
            .Distinct()
            .CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Player>> GetPlayersWithEitherAttributeAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default)
    {
        var playerIds = await dbContext.PlayerAttributes
            .AsNoTracking()
            .Where(pa =>
                (pa.AttributeType == firstAttributeType && pa.AttributeValue == firstAttributeValue) ||
                (pa.AttributeType == secondAttributeType && pa.AttributeValue == secondAttributeValue))
            .Select(pa => pa.PlayerId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (playerIds.Count == 0)
            return [];

        return await dbContext.Players
            .AsNoTracking()
            .Where(p => playerIds.Contains(p.Id))
            .ToListAsync(cancellationToken);
    }

    // Duplicated from PlayerAliasRepository/PlayerCareerStintRepository
    // (S-106/S-107, per those stories' own explicit instruction) rather than
    // shared across repository classes — repositories shouldn't depend on
    // each other. See PlayerAliasRepository's own copy for the original
    // "why this exists" comment (quality-architect review, 2026-07-21).
    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TEntity>>> GroupByPlayerIdAsync<TEntity>(
        IQueryable<TEntity> filteredQuery,
        Func<TEntity, Guid> playerIdSelector,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var rows = await filteredQuery.AsNoTracking().ToListAsync(cancellationToken);

        return rows
            .GroupBy(playerIdSelector)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TEntity>)g.ToList());
    }
}

using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerAliasRepository(XGArcadeDbContext dbContext) : IPlayerAliasRepository
{
    public async Task<IReadOnlyList<PlayerAlias>> GetPlayerAliasesAsync(Guid playerId, CancellationToken cancellationToken = default) =>
        await dbContext.PlayerAliases
            .AsNoTracking()
            .Where(pa => pa.PlayerId == playerId)
            .ToListAsync(cancellationToken);

    public async Task AddPlayerAliasAsync(PlayerAlias alias, CancellationToken cancellationToken = default)
    {
        dbContext.PlayerAliases.Add(alias);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddPlayerAliasesBatchAsync(IReadOnlyList<PlayerAlias> aliases, CancellationToken cancellationToken = default)
    {
        if (aliases.Count == 0)
            return;

        dbContext.PlayerAliases.AddRange(aliases);

        // One SaveChangesAsync call for the whole batch — load-then-
        // SaveChangesAsync (docs/coding-guidelines.md).
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Player>> GetPlayersByNormalizedAliasAsync(
        string normalizedAlias, CancellationToken cancellationToken = default)
    {
        var playerIds = await dbContext.PlayerAliases
            .AsNoTracking()
            .Where(pa => pa.NormalizedAlias == normalizedAlias)
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

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAlias>>> GetPlayerAliasesByPlayerIdsAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        if (playerIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<PlayerAlias>>();

        var idList = playerIds.ToList();
        return await GroupByPlayerIdAsync(
            dbContext.PlayerAliases.Where(pa => idList.Contains(pa.PlayerId)),
            alias => alias.PlayerId,
            cancellationToken);
    }

    // Duplicated from PlayerStoreRepository/PlayerAttributeRepository
    // (S-106, per that story's own explicit instruction) rather than shared
    // across repository classes — repositories shouldn't depend on each
    // other. See PlayerStoreRepository's own copy for the original "why
    // this exists" comment (quality-architect review, 2026-07-21).
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

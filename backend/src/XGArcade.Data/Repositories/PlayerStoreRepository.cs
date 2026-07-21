using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerStoreRepository(XGArcadeDbContext dbContext) : IPlayerStoreRepository
{
    public async Task<Player?> GetPlayerByWikidataQidAsync(string wikidataQid, CancellationToken cancellationToken = default) =>
        await dbContext.Players.AsNoTracking().FirstOrDefaultAsync(p => p.WikidataQid == wikidataQid, cancellationToken);

    public async Task<Player?> GetPlayerByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, Player>> GetPlayersByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return new Dictionary<Guid, Player>();

        return await dbContext.Players
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
    }

    public async Task<Player> AddPlayerAsync(Player player, CancellationToken cancellationToken = default)
    {
        dbContext.Players.Add(player);
        await dbContext.SaveChangesAsync(cancellationToken);
        return player;
    }

    public async Task<IReadOnlyList<Player>> GetPlayersByNormalizedFullNameAsync(
        string normalizedFullName, CancellationToken cancellationToken = default) =>
        await dbContext.Players
            .AsNoTracking()
            .Where(p => p.NormalizedFullName == normalizedFullName)
            .ToListAsync(cancellationToken);

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

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAlias>>> GetPlayerAliasesByPlayerIdsAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        if (playerIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<PlayerAlias>>();

        var idList = playerIds.ToList();
        var aliases = await dbContext.PlayerAliases
            .AsNoTracking()
            .Where(pa => idList.Contains(pa.PlayerId))
            .ToListAsync(cancellationToken);

        return aliases
            .GroupBy(a => a.PlayerId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PlayerAlias>)g.ToList());
    }

    public async Task AddPlayerDataAsync(PlayerData data, CancellationToken cancellationToken = default)
    {
        dbContext.PlayerData.Add(data);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlayerData>> GetUnverifiedPlayerDataAsync(CancellationToken cancellationToken = default) =>
        await dbContext.PlayerData
            .AsNoTracking()
            .Where(pd => pd.Confidence == "unverified")
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PlayerDataApprovalOutcome>> ApprovePlayerDataAsync(
        IReadOnlyCollection<Guid> playerDataIds, Guid adminId, CancellationToken cancellationToken = default)
    {
        if (playerDataIds.Count == 0)
            return [];

        var idList = playerDataIds.ToList();
        var rowsById = await dbContext.PlayerData
            .Where(pd => idList.Contains(pd.Id))
            .ToDictionaryAsync(pd => pd.Id, cancellationToken);

        var approvedAt = DateTime.UtcNow;
        var outcomes = new List<PlayerDataApprovalOutcome>(idList.Count);

        foreach (var id in idList)
        {
            if (!rowsById.TryGetValue(id, out var row))
            {
                outcomes.Add(new PlayerDataApprovalOutcome(id, false, PlayerDataApprovalFailureReason.NotFound));
                continue;
            }

            if (row.Confidence != "unverified")
            {
                outcomes.Add(new PlayerDataApprovalOutcome(id, false, PlayerDataApprovalFailureReason.NotUnverified));
                continue;
            }

            row.Confidence = "verified";
            row.ApprovedByAdminId = adminId;
            row.ApprovedAt = approvedAt;
            outcomes.Add(new PlayerDataApprovalOutcome(id, true, null));
        }

        // One SaveChangesAsync call for the whole batch — load-then-
        // SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteUpdateAsync (the InMemory test provider can't translate it).
        await dbContext.SaveChangesAsync(cancellationToken);

        return outcomes;
    }

    public async Task<IReadOnlyList<PlayerDataRemovalOutcome>> RemovePlayerDataAsync(
        IReadOnlyCollection<Guid> playerDataIds, CancellationToken cancellationToken = default)
    {
        if (playerDataIds.Count == 0)
            return [];

        var idList = playerDataIds.ToList();
        var rowsById = await dbContext.PlayerData
            .Where(pd => idList.Contains(pd.Id))
            .ToDictionaryAsync(pd => pd.Id, cancellationToken);

        var outcomes = new List<PlayerDataRemovalOutcome>(idList.Count);

        foreach (var id in idList)
        {
            if (!rowsById.TryGetValue(id, out var row))
            {
                outcomes.Add(new PlayerDataRemovalOutcome(id, false, PlayerDataRemovalFailureReason.NotFound));
                continue;
            }

            dbContext.PlayerData.Remove(row);
            outcomes.Add(new PlayerDataRemovalOutcome(id, true, null));
        }

        // One SaveChangesAsync call for the whole batch — load-then-
        // SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteDeleteAsync (the InMemory test provider can't translate it).
        await dbContext.SaveChangesAsync(cancellationToken);

        return outcomes;
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

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAttribute>>> GetPlayerAttributesByPlayerIdsAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        if (playerIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<PlayerAttribute>>();

        var idList = playerIds.ToList();
        var attributes = await dbContext.PlayerAttributes
            .AsNoTracking()
            .Where(pa => idList.Contains(pa.PlayerId))
            .ToListAsync(cancellationToken);

        return attributes
            .GroupBy(a => a.PlayerId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PlayerAttribute>)g.ToList());
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

    public async Task<PlayerOverride?> GetOverrideAsync(Guid playerId, string field, CancellationToken cancellationToken = default) =>
        await dbContext.PlayerOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.PlayerId == playerId && o.Field == field, cancellationToken);

    public async Task<PlayerOverride?> GetOverrideByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.PlayerOverrides.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task AddOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default)
    {
        dbContext.PlayerOverrides.Add(playerOverride);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default)
    {
        dbContext.PlayerOverrides.Update(playerOverride);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteOverrideAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var playerOverride = await dbContext.PlayerOverrides.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (playerOverride is null)
            return false;

        dbContext.PlayerOverrides.Remove(playerOverride);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> HasEffectiveAttributeAsync(
        Guid playerId, string attributeType, string attributeValue, CancellationToken cancellationToken = default)
    {
        // REQ-203/REQ-501: a PlayerOverride for this field always wins,
        // replacing every cached PlayerAttribute row of that type for this
        // player — not merged/added to them.
        var overrideRecord = await GetOverrideAsync(playerId, attributeType, cancellationToken);
        if (overrideRecord is not null)
            return overrideRecord.Value == attributeValue;

        return await dbContext.PlayerAttributes
            .AsNoTracking()
            .AnyAsync(pa => pa.PlayerId == playerId && pa.AttributeType == attributeType && pa.AttributeValue == attributeValue, cancellationToken);
    }

    public async Task<IReadOnlyList<Player>> GetPlayersMissingPhotoAsync(
        IReadOnlyCollection<Guid> excludingPlayerIds, int batchSize, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Players
            .AsNoTracking()
            .Where(p => p.WikidataQid != null && p.PhotoUrl == null);

        if (excludingPlayerIds.Count > 0)
            query = query.Where(p => !excludingPlayerIds.Contains(p.Id));

        return await query
            .OrderBy(p => p.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdatePlayerPhotosAsync(
        IReadOnlyDictionary<Guid, string> photoUrlByPlayerId, CancellationToken cancellationToken = default)
    {
        if (photoUrlByPlayerId.Count == 0)
            return;

        var playerIds = photoUrlByPlayerId.Keys.ToList();
        var players = await dbContext.Players
            .Where(p => playerIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        foreach (var player in players)
            player.PhotoUrl = photoUrlByPlayerId[player.Id];

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

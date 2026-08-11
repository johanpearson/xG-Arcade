using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerDataRepository(XGArcadeDbContext dbContext) : IPlayerDataRepository
{
    public async Task AddPlayerDataAsync(PlayerData data, CancellationToken cancellationToken = default)
    {
        dbContext.PlayerData.Add(data);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddPlayerDataBatchAsync(IReadOnlyList<PlayerData> data, CancellationToken cancellationToken = default)
    {
        if (data.Count == 0)
            return;

        dbContext.PlayerData.AddRange(data);

        // One SaveChangesAsync call for the whole batch — load-then-
        // SaveChangesAsync (docs/coding-guidelines.md).
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
}

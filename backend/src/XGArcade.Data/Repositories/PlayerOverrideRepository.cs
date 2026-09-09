using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerOverrideRepository(XGArcadeDbContext dbContext) : IPlayerOverrideRepository
{
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

    // REQ-1501 (xG Higher/Lower, ADR-0110): see this method's own doc
    // comment on IPlayerOverrideRepository for the full "why" — extends
    // HasEffectiveAttributeAsync's override-wins-for-the-whole-type rule
    // (ADR-0015) from a single value check to a count.
    public async Task<IReadOnlyDictionary<Guid, int>> GetEffectivePlayerCountsByAttributeTypeAsync(
        string attributeType, CancellationToken cancellationToken = default)
    {
        // PlayerAttribute's composite key (PlayerId, AttributeType,
        // AttributeValue) already guarantees no duplicate rows, so a plain
        // per-player row count is the same as a distinct-value count here —
        // no separate Distinct() needed.
        var attributeRows = await dbContext.PlayerAttributes
            .AsNoTracking()
            .Where(pa => pa.AttributeType == attributeType)
            .Select(pa => pa.PlayerId)
            .ToListAsync(cancellationToken);

        var counts = attributeRows
            .GroupBy(playerId => playerId)
            .ToDictionary(g => g.Key, g => g.Count());

        // REQ-203/REQ-501/ADR-0015 extended to counting: an override for
        // this (PlayerId, attributeType) REPLACES the whole effective value
        // set with exactly {Override.Value} — always exactly 1, regardless
        // of how many raw PlayerAttribute rows this player has of this type
        // (even if that's 0 — an override alone is enough to make a player
        // eligible).
        var overriddenPlayerIds = await dbContext.PlayerOverrides
            .AsNoTracking()
            .Where(o => o.Field == attributeType)
            .Select(o => o.PlayerId)
            .ToListAsync(cancellationToken);

        foreach (var playerId in overriddenPlayerIds)
            counts[playerId] = 1;

        return counts;
    }
}

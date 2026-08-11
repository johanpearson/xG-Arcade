using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerCareerStintRepository(XGArcadeDbContext dbContext) : IPlayerCareerStintRepository
{
    public async Task<IReadOnlyList<PlayerCareerStint>> GetCareerStintsAsync(
        Guid playerId, CancellationToken cancellationToken = default) =>
        await dbContext.PlayerCareerStints
            .AsNoTracking()
            .Where(s => s.PlayerId == playerId)
            .ToListAsync(cancellationToken);

    public async Task AddCareerStintsAsync(
        Guid playerId, IReadOnlyList<PlayerCareerStint> newStints, CancellationToken cancellationToken = default)
    {
        if (newStints.Count == 0)
            return;

        var existing = await dbContext.PlayerCareerStints
            .Where(s => s.PlayerId == playerId)
            .ToListAsync(cancellationToken);

        dbContext.PlayerCareerStints.AddRange(newStints);

        // ADR-0042/S-079: SequenceOrder is resolved here, across the
        // player's FULL stint set (existing rows + newStints), not just the
        // newly-added ones — a stint discovered later that chronologically
        // precedes existing rows must still shift everyone else's
        // SequenceOrder. Ongoing stints (EndYear null) sort last among
        // stints sharing the same StartYear.
        var chronological = existing
            .Concat(newStints)
            .OrderBy(s => s.StartYear)
            .ThenBy(s => s.EndYear ?? int.MaxValue)
            .ToList();

        for (var i = 0; i < chronological.Count; i++)
            chronological[i].SequenceOrder = i;

        // One SaveChangesAsync call for the whole batch — load-then-
        // SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteUpdateAsync (the InMemory test provider can't translate it).
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetCareerStintCandidatePlayerIdsAsync(
        IReadOnlySet<string> seededClubNames, int minStintCount, CancellationToken cancellationToken = default)
    {
        // Same "materialize via ToListAsync, then GroupBy/filter as
        // LINQ-to-Objects" convention as IPlayerDataQualityRepository's own
        // GetUnseededClubCandidatesAsync — but only a (PlayerId, ClubName)
        // projection here, not the full entity, since this is a hot path
        // (every xG Path round generation) rather than an occasional manual
        // diagnostic job. Exact ordinal/case-sensitive Contains — matches
        // IsEligible's own comparison exactly, NOT
        // GetUnseededClubCandidatesAsync's OrdinalIgnoreCase (a
        // deliberately different, diagnostic-only choice for that method).
        var stints = await dbContext.PlayerCareerStints
            .AsNoTracking()
            .Select(s => new { s.PlayerId, s.ClubName })
            .ToListAsync(cancellationToken);

        return stints
            .GroupBy(s => s.PlayerId)
            .Where(g => g.Count() >= minStintCount && g.Any(s => seededClubNames.Contains(s.ClubName)))
            .Select(g => g.Key)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerCareerStint>>> GetCareerStintsByPlayerIdsAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        if (playerIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>();

        var idList = playerIds.ToList();
        return await GroupByPlayerIdAsync(
            dbContext.PlayerCareerStints.Where(s => idList.Contains(s.PlayerId)),
            stint => stint.PlayerId,
            cancellationToken);
    }

    // Duplicated from PlayerAliasRepository/PlayerAttributeRepository
    // (S-106, per that story's own explicit instruction) rather than shared
    // across repository classes — repositories shouldn't depend on each
    // other. See PlayerAliasRepository's own copy for the original "why
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

    public async Task AddCareerStintsBatchAsync(
        IReadOnlyDictionary<Guid, IReadOnlyList<PlayerCareerStint>> newStintsByPlayerId, CancellationToken cancellationToken = default)
    {
        var playerIds = newStintsByPlayerId.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();
        if (playerIds.Count == 0)
            return;

        var existingByPlayer = (await dbContext.PlayerCareerStints
                .Where(s => playerIds.Contains(s.PlayerId))
                .ToListAsync(cancellationToken))
            .GroupBy(s => s.PlayerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var playerId in playerIds)
        {
            var newStints = newStintsByPlayerId[playerId];
            dbContext.PlayerCareerStints.AddRange(newStints);

            // ADR-0042/S-079: SequenceOrder is resolved here, across the
            // player's FULL stint set (existing rows + newStints), not just
            // the newly-added ones — same chronological re-sequencing rule
            // as AddCareerStintsAsync's own comment, just applied to every
            // affected player in this call rather than one at a time.
            var chronological = existingByPlayer.GetValueOrDefault(playerId, [])
                .Concat(newStints)
                .OrderBy(s => s.StartYear)
                .ThenBy(s => s.EndYear ?? int.MaxValue)
                .ToList();

            for (var i = 0; i < chronological.Count; i++)
                chronological[i].SequenceOrder = i;
        }

        // One SaveChangesAsync call for the whole batch, across every
        // affected player — load-then-SaveChangesAsync
        // (docs/coding-guidelines.md), never ExecuteUpdateAsync.
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

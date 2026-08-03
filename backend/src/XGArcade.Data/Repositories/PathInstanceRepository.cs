using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PathInstanceRepository(XGArcadeDbContext dbContext) : IPathInstanceRepository
{
    // REQ-1208/ADR-0058: PathTargetCycle is a singleton table — this fixed
    // id is what guarantees at most one row can ever exist, the same role
    // League.Type's filtered unique index plays for
    // GetOrCreateGlobalLeagueAsync's own singleton row, just enforced via
    // the primary key instead (simpler here since there's no second,
    // non-singleton row type sharing this table the way League's Type
    // column does).
    private static readonly Guid SingletonCycleId = new("00000000-0000-0000-0000-000000000001");

    public async Task<PathTemplate?> GetTemplateByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.PathTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<PathTemplate?> GetTemplateByPuzzleCountAsync(int puzzleCount, CancellationToken cancellationToken = default) =>
        await dbContext.PathTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.PuzzleCount == puzzleCount, cancellationToken);

    public async Task<PathTemplate> AddTemplateAsync(PathTemplate template, CancellationToken cancellationToken = default)
    {
        dbContext.PathTemplates.Add(template);
        await dbContext.SaveChangesAsync(cancellationToken);
        return template;
    }

    public async Task<PathInstance> AddInstanceAsync(PathInstance instance, CancellationToken cancellationToken = default)
    {
        dbContext.PathInstances.Add(instance);
        await dbContext.SaveChangesAsync(cancellationToken);
        return instance;
    }

    public async Task<PathInstance?> GetInstanceByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.PathInstances
            .AsNoTracking()
            .Include(pi => pi.Puzzles)
            .FirstOrDefaultAsync(pi => pi.Id == id, cancellationToken);

    public async Task<PathTargetCycle?> GetCycleStateAsync(CancellationToken cancellationToken = default) =>
        await dbContext.PathTargetCycles.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    public async Task<PathTargetCycle> GetOrCreateCycleStateAsync(CancellationToken cancellationToken = default)
    {
        var existing = await GetCycleStateAsync(cancellationToken);
        if (existing is not null)
            return existing;

        var initial = new PathTargetCycle
        {
            Id = SingletonCycleId,
            CycleNumber = 1,
            ObservedPoolSize = 0,
            UsedInCycleCount = 0,
            LastCycleCompletedAt = null,
        };
        dbContext.PathTargetCycles.Add(initial);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two concurrent first-ever generations raced past the check
            // above — the fixed SingletonCycleId primary key let only one
            // insert win. Mirrors LeagueRepository.
            // GetOrCreateGlobalLeagueAsync's own race handling: detach this
            // loser's now-invalid tracked entry and return the winner
            // instead of surfacing a raw 500.
            dbContext.Entry(initial).State = EntityState.Detached;
            return await dbContext.PathTargetCycles.AsNoTracking().SingleAsync(cancellationToken);
        }

        return initial;
    }

    public async Task<IReadOnlyList<Guid>> GetUsedPlayerIdsInCycleAsync(int cycleNumber, CancellationToken cancellationToken = default) =>
        await dbContext.PathCycleTargetUsages
            .AsNoTracking()
            .Where(u => u.CycleNumber == cycleNumber)
            .Select(u => u.PlayerId)
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<PathInstance> AddInstanceWithCycleUsageAsync(
        PathInstance instance,
        PathTargetCycle cycleState,
        IReadOnlyCollection<Guid> targetPlayerIds,
        CancellationToken cancellationToken = default)
    {
        dbContext.PathInstances.Add(instance);
        // cycleState was loaded (or built) AsNoTracking by
        // GetOrCreateCycleStateAsync — Update attaches it and marks every
        // property modified, same "load-then-save, no ExecuteUpdateAsync"
        // discipline coding-guidelines.md requires (the InMemory test
        // provider can't translate ExecuteUpdateAsync).
        dbContext.PathTargetCycles.Update(cycleState);
        dbContext.PathCycleTargetUsages.AddRange(targetPlayerIds.Select(playerId => new PathCycleTargetUsage
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            CycleNumber = cycleState.CycleNumber,
        }));

        // REQ-1208: the instance/puzzle write and the cycle-usage write
        // happen in this one SaveChangesAsync call — same unit of work, so
        // they can never diverge on a partial failure.
        await dbContext.SaveChangesAsync(cancellationToken);
        return instance;
    }
}

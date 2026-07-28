using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PathInstanceRepository(XGArcadeDbContext dbContext) : IPathInstanceRepository
{
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
}

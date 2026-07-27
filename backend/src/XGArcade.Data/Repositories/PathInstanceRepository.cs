using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PathInstanceRepository(XGArcadeDbContext dbContext) : IPathInstanceRepository
{
    public async Task<PathTemplate?> GetTemplateByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.PathTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

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

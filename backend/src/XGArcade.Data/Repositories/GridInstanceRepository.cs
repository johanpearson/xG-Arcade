using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class GridInstanceRepository(XGArcadeDbContext dbContext) : IGridInstanceRepository
{
    public async Task<GridTemplate?> GetTemplateByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.GridTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<GridTemplate?> GetTemplateBySizeAsync(int size, CancellationToken cancellationToken = default) =>
        await dbContext.GridTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Size == size, cancellationToken);

    public async Task<GridTemplate> AddTemplateAsync(GridTemplate template, CancellationToken cancellationToken = default)
    {
        dbContext.GridTemplates.Add(template);
        await dbContext.SaveChangesAsync(cancellationToken);
        return template;
    }

    public async Task<GridInstance> AddInstanceAsync(GridInstance instance, CancellationToken cancellationToken = default)
    {
        dbContext.GridInstances.Add(instance);
        await dbContext.SaveChangesAsync(cancellationToken);
        return instance;
    }

    public async Task<GridInstance?> GetInstanceByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.GridInstances
            .AsNoTracking()
            .Include(gi => gi.Cells)
            .FirstOrDefaultAsync(gi => gi.Id == id, cancellationToken);
}

using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class HigherLowerInstanceRepository(XGArcadeDbContext dbContext) : IHigherLowerInstanceRepository
{
    public async Task<HigherLowerInstance> AddInstanceAsync(HigherLowerInstance instance, CancellationToken cancellationToken = default)
    {
        dbContext.HigherLowerInstances.Add(instance);
        await dbContext.SaveChangesAsync(cancellationToken);
        return instance;
    }

    public async Task<HigherLowerInstance?> GetInstanceByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.HigherLowerInstances
            .AsNoTracking()
            .Include(hli => hli.Comparators)
            .FirstOrDefaultAsync(hli => hli.Id == id, cancellationToken);
}

using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PredictInstanceRepository(XGArcadeDbContext dbContext) : IPredictInstanceRepository
{
    public async Task<PredictTemplate?> GetTemplateByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.PredictTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<PredictTemplate?> GetTemplateByMatchCountAsync(int matchCount, CancellationToken cancellationToken = default) =>
        await dbContext.PredictTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.MatchCount == matchCount, cancellationToken);

    public async Task<PredictTemplate> AddTemplateAsync(PredictTemplate template, CancellationToken cancellationToken = default)
    {
        dbContext.PredictTemplates.Add(template);
        await dbContext.SaveChangesAsync(cancellationToken);
        return template;
    }

    public async Task<PredictInstance> AddInstanceAsync(PredictInstance instance, CancellationToken cancellationToken = default)
    {
        dbContext.PredictInstances.Add(instance);
        await dbContext.SaveChangesAsync(cancellationToken);
        return instance;
    }

    public async Task<PredictInstance?> GetInstanceByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.PredictInstances
            .AsNoTracking()
            .Include(pi => pi.Matches)
            .FirstOrDefaultAsync(pi => pi.Id == id, cancellationToken);

    public async Task<PredictMatchPrediction?> GetPredictionAsync(Guid predictMatchId, Guid? userId, CancellationToken cancellationToken = default) =>
        await dbContext.PredictMatchPredictions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PredictMatchId == predictMatchId && p.UserId == userId, cancellationToken);

    public async Task AddOrUpdatePredictionAsync(
        Guid predictMatchId, Guid? userId, int homeGoals, int awayGoals, DateTime submittedAt, CancellationToken cancellationToken = default)
    {
        // Load-then-save (coding-guidelines.md — never ExecuteUpdateAsync,
        // the InMemory test provider can't translate it), tracked this time
        // (unlike the AsNoTracking reads above) since this call may update
        // an existing row in place.
        var existing = await dbContext.PredictMatchPredictions
            .FirstOrDefaultAsync(p => p.PredictMatchId == predictMatchId && p.UserId == userId, cancellationToken);

        if (existing is not null)
        {
            existing.HomeGoals = homeGoals;
            existing.AwayGoals = awayGoals;
            existing.SubmittedAt = submittedAt;
        }
        else
        {
            dbContext.PredictMatchPredictions.Add(new PredictMatchPrediction
            {
                Id = Guid.NewGuid(),
                PredictMatchId = predictMatchId,
                UserId = userId,
                HomeGoals = homeGoals,
                AwayGoals = awayGoals,
                SubmittedAt = submittedAt,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

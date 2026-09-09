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

    public async Task<HigherLowerAttempt?> GetAttemptAsync(Guid higherLowerInstanceId, Guid? userId, CancellationToken cancellationToken = default) =>
        await dbContext.HigherLowerAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.HigherLowerInstanceId == higherLowerInstanceId && a.UserId == userId, cancellationToken);

    // ADR-0100 §3/REQ-1505/S-226: see IHigherLowerInstanceRepository's own
    // doc comment for the "participation, not points" role this plays.
    public async Task<IReadOnlyCollection<Guid>> GetParticipantUserIdsByInstanceIdAsync(
        Guid higherLowerInstanceId, CancellationToken cancellationToken = default) =>
        await dbContext.HigherLowerAttempts
            .AsNoTracking()
            .Where(a => a.HigherLowerInstanceId == higherLowerInstanceId && a.UserId != null)
            .Select(a => a.UserId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

    // ADR-0100 §4/REQ-1505/S-226: see IHigherLowerInstanceRepository's own
    // doc comment for why every attempt row unconditionally contributes its
    // StreakLength (no Graded/Pending distinction the way predict's
    // FinalPoints has).
    public async Task<IReadOnlyDictionary<Guid, int>> GetStreakLengthsByInstanceIdAsync(
        Guid higherLowerInstanceId, CancellationToken cancellationToken = default)
    {
        var streaks = await dbContext.HigherLowerAttempts
            .AsNoTracking()
            .Where(a => a.HigherLowerInstanceId == higherLowerInstanceId && a.UserId != null)
            .Select(a => new { UserId = a.UserId!.Value, a.StreakLength })
            .ToListAsync(cancellationToken);

        return streaks.ToDictionary(s => s.UserId, s => s.StreakLength);
    }

    public async Task SaveAttemptAsync(
        Guid higherLowerInstanceId,
        Guid? userId,
        int streakLength,
        Guid currentBaselinePlayerId,
        int currentBaselineValue,
        bool hasEnded,
        CancellationToken cancellationToken = default)
    {
        // Load-then-save (coding-guidelines.md — never ExecuteUpdateAsync,
        // the InMemory test provider can't translate it), tracked this time
        // (unlike the AsNoTracking reads above) since this call may update
        // an existing row in place.
        var existing = await dbContext.HigherLowerAttempts
            .FirstOrDefaultAsync(a => a.HigherLowerInstanceId == higherLowerInstanceId && a.UserId == userId, cancellationToken);

        if (existing is not null)
        {
            existing.StreakLength = streakLength;
            existing.CurrentBaselinePlayerId = currentBaselinePlayerId;
            existing.CurrentBaselineValue = currentBaselineValue;
            existing.HasEnded = hasEnded;
        }
        else
        {
            dbContext.HigherLowerAttempts.Add(new HigherLowerAttempt
            {
                Id = Guid.NewGuid(),
                HigherLowerInstanceId = higherLowerInstanceId,
                UserId = userId,
                StreakLength = streakLength,
                CurrentBaselinePlayerId = currentBaselinePlayerId,
                CurrentBaselineValue = currentBaselineValue,
                HasEnded = hasEnded,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Load-then-save rather than ExecuteUpdateAsync: this codebase's tests
    // run against EF Core's InMemory provider (docs/coding-guidelines.md),
    // which doesn't support translating bulk ExecuteUpdate/ExecuteDelete
    // calls — same reason PredictInstanceRepository.
    // AnonymizePredictionsByUserIdAsync does too.
    public async Task AnonymizeAttemptsByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var attempts = await dbContext.HigherLowerAttempts
            .Where(a => a.UserId == userId)
            .ToListAsync(cancellationToken);

        foreach (var attempt in attempts)
        {
            attempt.UserId = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

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

    // REQ-1305/ADR-0097 §3: cutoff computed once, in C#, rather than
    // writing `m.KickoffUtc + typicalMatchDuration <= nowUtc` in the LINQ
    // query — a plain DateTime <= DateTime comparison translates
    // identically (and unambiguously) against both the real Npgsql
    // provider and the InMemory provider tests use, avoiding any doubt
    // about whether DateTime/TimeSpan addition itself translates cleanly
    // against Postgres interval arithmetic.
    public async Task<IReadOnlyList<PredictMatch>> GetMatchesReadyForGradingAsync(
        TimeSpan typicalMatchDuration, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var cutoffKickoffUtc = nowUtc - typicalMatchDuration;

        return await dbContext.PredictMatches
            .AsNoTracking()
            .Where(m => m.GradingStatus == PredictMatchGradingStatus.Pending && m.KickoffUtc <= cutoffKickoffUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PredictMatchPrediction>> GetPredictionsForMatchAsync(
        Guid predictMatchId, CancellationToken cancellationToken = default) =>
        await dbContext.PredictMatchPredictions
            .AsNoTracking()
            .Where(p => p.PredictMatchId == predictMatchId)
            .ToListAsync(cancellationToken);

    public async Task GradeMatchAsync(
        Guid predictMatchId,
        int actualHomeGoals,
        int actualAwayGoals,
        IReadOnlyDictionary<Guid, int> finalPointsByPredictionId,
        CancellationToken cancellationToken = default)
    {
        // Load-then-save (coding-guidelines.md — never ExecuteUpdateAsync),
        // tracked (unlike the AsNoTracking reads above) since this writes
        // both the match row and, below, its predictions' rows.
        var match = await dbContext.PredictMatches
            .FirstOrDefaultAsync(m => m.Id == predictMatchId, cancellationToken)
            ?? throw new InvalidOperationException($"PredictMatch '{predictMatchId}' not found.");

        match.GradingStatus = PredictMatchGradingStatus.Graded;
        match.ActualHomeGoals = actualHomeGoals;
        match.ActualAwayGoals = actualAwayGoals;

        if (finalPointsByPredictionId.Count > 0)
        {
            var predictionIds = finalPointsByPredictionId.Keys.ToList();
            var predictions = await dbContext.PredictMatchPredictions
                .Where(p => predictionIds.Contains(p.Id))
                .ToListAsync(cancellationToken);

            foreach (var prediction in predictions)
                prediction.FinalPoints = finalPointsByPredictionId[prediction.Id];
        }

        // One SaveChangesAsync call — the match's own row and every
        // touched prediction row are written together, so a crash between
        // them cannot happen (ADR-0097 Decision §3).
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task VoidMatchAsync(Guid predictMatchId, CancellationToken cancellationToken = default)
    {
        var match = await dbContext.PredictMatches
            .FirstOrDefaultAsync(m => m.Id == predictMatchId, cancellationToken)
            ?? throw new InvalidOperationException($"PredictMatch '{predictMatchId}' not found.");

        match.GradingStatus = PredictMatchGradingStatus.Voided;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetTotalPointsByInstanceIdAsync(
        Guid predictInstanceId, CancellationToken cancellationToken = default)
    {
        // No navigation property from PredictMatchPrediction to PredictMatch
        // (XGArcadeDbContext.OnModelCreating configures that FK with no nav
        // property, mirroring Guess's own shape) — an explicit join, not a
        // Predictions-then-filter shape, is how this crosses from
        // PredictMatch's GradingStatus/PredictInstanceId over to
        // PredictMatchPrediction's FinalPoints/UserId.
        var totals = await (
            from prediction in dbContext.PredictMatchPredictions.AsNoTracking()
            join match in dbContext.PredictMatches.AsNoTracking()
                on prediction.PredictMatchId equals match.Id
            where match.PredictInstanceId == predictInstanceId
                && match.GradingStatus == PredictMatchGradingStatus.Graded
                && prediction.UserId != null
            group prediction by prediction.UserId!.Value into userPredictions
            select new { UserId = userPredictions.Key, Total = userPredictions.Sum(p => p.FinalPoints ?? 0) })
            .ToListAsync(cancellationToken);

        return totals.ToDictionary(t => t.UserId, t => t.Total);
    }

    // REQ-1302/ADR-0098: same explicit-join shape as
    // GetTotalPointsByInstanceIdAsync above (no navigation property from
    // PredictMatchPrediction to PredictMatch), scoped to one user instead of
    // grouped by every user.
    public async Task<IReadOnlyList<PredictMatchPrediction>> GetPredictionsForInstanceAndUserAsync(
        Guid predictInstanceId, Guid userId, CancellationToken cancellationToken = default) =>
        await (
            from prediction in dbContext.PredictMatchPredictions.AsNoTracking()
            join match in dbContext.PredictMatches.AsNoTracking()
                on prediction.PredictMatchId equals match.Id
            where match.PredictInstanceId == predictInstanceId && prediction.UserId == userId
            select prediction)
            .ToListAsync(cancellationToken);

    public async Task<bool> IsPlayerLockedAsync(Guid predictInstanceId, Guid userId, CancellationToken cancellationToken = default) =>
        await dbContext.PredictPlayerLocks
            .AsNoTracking()
            .AnyAsync(l => l.PredictInstanceId == predictInstanceId && l.UserId == userId, cancellationToken);

    public async Task LockPlayerPredictionsAsync(
        Guid predictInstanceId, Guid userId, DateTime lockedAt, CancellationToken cancellationToken = default)
    {
        // Load-then-save (coding-guidelines.md — never ExecuteUpdateAsync),
        // and idempotent by construction: a second call for an already-
        // locked pair finds the existing row and simply does nothing further
        // rather than attempting a second insert that would violate the
        // composite key.
        var existing = await dbContext.PredictPlayerLocks
            .FirstOrDefaultAsync(l => l.PredictInstanceId == predictInstanceId && l.UserId == userId, cancellationToken);

        if (existing is not null)
            return;

        dbContext.PredictPlayerLocks.Add(new PredictPlayerLock
        {
            PredictInstanceId = predictInstanceId,
            UserId = userId,
            LockedAt = lockedAt,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

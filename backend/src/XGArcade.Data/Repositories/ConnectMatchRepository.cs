using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class ConnectMatchRepository(XGArcadeDbContext dbContext) : IConnectMatchRepository
{
    public async Task<ConnectMatch> AddMatchAsync(ConnectMatch match, CancellationToken cancellationToken = default)
    {
        dbContext.ConnectMatches.Add(match);
        await dbContext.SaveChangesAsync(cancellationToken);
        return match;
    }

    public async Task<ConnectMatch?> GetMatchByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.ConnectMatches.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public async Task<ConnectTargetPick> AddOrUpdateTargetPickAsync(
        Guid matchId, Guid? userId, Guid targetPlayerId, DateTime selectedAt, CancellationToken cancellationToken = default)
    {
        // Load-then-save (coding-guidelines.md — never ExecuteUpdateAsync,
        // the InMemory test provider can't translate it), tracked this time
        // (unlike the AsNoTracking reads elsewhere in this class) since this
        // call may update an existing row in place.
        var existing = await dbContext.ConnectTargetPicks
            .FirstOrDefaultAsync(p => p.ConnectMatchId == matchId && p.UserId == userId, cancellationToken);

        if (existing is not null)
        {
            existing.TargetPlayerId = targetPlayerId;
            existing.SelectedAt = selectedAt;
            await dbContext.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var pick = new ConnectTargetPick
        {
            Id = Guid.NewGuid(),
            ConnectMatchId = matchId,
            UserId = userId,
            TargetPlayerId = targetPlayerId,
            SelectedAt = selectedAt,
        };

        dbContext.ConnectTargetPicks.Add(pick);
        await dbContext.SaveChangesAsync(cancellationToken);
        return pick;
    }

    public async Task<ConnectTargetPick?> GetTargetPickAsync(Guid matchId, Guid? userId, CancellationToken cancellationToken = default) =>
        await dbContext.ConnectTargetPicks
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ConnectMatchId == matchId && p.UserId == userId, cancellationToken);

    public async Task<IReadOnlyList<ConnectTargetPick>> GetTargetPicksForMatchAsync(
        Guid matchId, CancellationToken cancellationToken = default) =>
        await dbContext.ConnectTargetPicks
            .AsNoTracking()
            .Where(p => p.ConnectMatchId == matchId)
            .ToListAsync(cancellationToken);

    public async Task<ConnectChainStep> AddChainStepAsync(ConnectChainStep chainStep, CancellationToken cancellationToken = default)
    {
        dbContext.ConnectChainSteps.Add(chainStep);
        await dbContext.SaveChangesAsync(cancellationToken);
        return chainStep;
    }

    public async Task<IReadOnlyList<ConnectChainStep>> GetChainStepsForMatchAndUserAsync(
        Guid matchId, Guid? userId, CancellationToken cancellationToken = default) =>
        await dbContext.ConnectChainSteps
            .AsNoTracking()
            .Where(s => s.ConnectMatchId == matchId && s.UserId == userId)
            .ToListAsync(cancellationToken);

    // REQ-710/ADR-0101: load-then-save (coding-guidelines.md — never
    // ExecuteUpdateAsync, the InMemory test provider can't translate it),
    // tracked (not AsNoTracking) since every row here is mutated in place.
    // Three separate queries/loops rather than one combined LINQ query
    // across entity types, mirroring
    // PredictInstanceRepository.AnonymizePredictionsByUserIdAsync's own
    // one-entity-type-at-a-time shape.
    public async Task AnonymizeUserDataAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var matches = await dbContext.ConnectMatches
            .Where(m => m.PlayerAUserId == userId || m.PlayerBUserId == userId)
            .ToListAsync(cancellationToken);

        foreach (var match in matches)
        {
            if (match.PlayerAUserId == userId)
                match.PlayerAUserId = null;
            if (match.PlayerBUserId == userId)
                match.PlayerBUserId = null;
        }

        var targetPicks = await dbContext.ConnectTargetPicks
            .Where(p => p.UserId == userId)
            .ToListAsync(cancellationToken);

        foreach (var pick in targetPicks)
        {
            pick.UserId = null;
        }

        var chainSteps = await dbContext.ConnectChainSteps
            .Where(s => s.UserId == userId)
            .ToListAsync(cancellationToken);

        foreach (var step in chainSteps)
        {
            step.UserId = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

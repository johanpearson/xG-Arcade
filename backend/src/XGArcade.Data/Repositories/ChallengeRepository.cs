using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class ChallengeRepository(XGArcadeDbContext dbContext) : IChallengeRepository
{
    public async Task<Challenge> AddChallengeAsync(Challenge challenge, CancellationToken cancellationToken = default)
    {
        dbContext.Challenges.Add(challenge);
        await dbContext.SaveChangesAsync(cancellationToken);
        return challenge;
    }

    public async Task<Challenge?> GetChallengeByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.Challenges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Challenge>> GetPendingChallengesForUserAsync(
        Guid challengedUserId, CancellationToken cancellationToken = default) =>
        await dbContext.Challenges
            .AsNoTracking()
            .Where(c => c.ChallengedUserId == challengedUserId && c.Status == ChallengeStatus.Pending)
            .ToListAsync(cancellationToken);

    public async Task UpdateChallengeStatusAsync(
        Guid challengeId, ChallengeStatus status, DateTime resolvedAt, Guid? resultingMatchId = null,
        CancellationToken cancellationToken = default)
    {
        // Load-then-save (coding-guidelines.md — never ExecuteUpdateAsync,
        // the InMemory test provider can't translate it).
        var challenge = await dbContext.Challenges
            .FirstOrDefaultAsync(c => c.Id == challengeId, cancellationToken)
            ?? throw new InvalidOperationException($"Challenge '{challengeId}' not found.");

        challenge.Status = status;
        challenge.ResolvedAt = resolvedAt;
        if (resultingMatchId is not null)
            challenge.ResultingMatchId = resultingMatchId;

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class MatchmakingOptInRepository(XGArcadeDbContext dbContext) : IMatchmakingOptInRepository
{
    public async Task<MatchmakingOptIn> AddOptInAsync(MatchmakingOptIn optIn, CancellationToken cancellationToken = default)
    {
        dbContext.MatchmakingOptIns.Add(optIn);
        await dbContext.SaveChangesAsync(cancellationToken);
        return optIn;
    }

    public async Task<MatchmakingOptIn?> GetOptInByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.MatchmakingOptIns.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<IReadOnlyList<MatchmakingOptIn>> GetWaitingOptInsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.MatchmakingOptIns
            .AsNoTracking()
            .Where(o => o.Status == MatchmakingOptInStatus.Waiting)
            .ToListAsync(cancellationToken);

    public async Task UpdateOptInStatusAsync(
        Guid optInId, MatchmakingOptInStatus status, Guid? resultingMatchId = null,
        CancellationToken cancellationToken = default)
    {
        // Load-then-save (coding-guidelines.md — never ExecuteUpdateAsync,
        // the InMemory test provider can't translate it).
        var optIn = await dbContext.MatchmakingOptIns
            .FirstOrDefaultAsync(o => o.Id == optInId, cancellationToken)
            ?? throw new InvalidOperationException($"MatchmakingOptIn '{optInId}' not found.");

        optIn.Status = status;
        if (resultingMatchId is not null)
            optIn.ResultingMatchId = resultingMatchId;

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

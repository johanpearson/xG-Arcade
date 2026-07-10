using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class LeagueRepository(XGArcadeDbContext dbContext) : ILeagueRepository
{
    public async Task<League> GetOrCreateGlobalLeagueAsync(CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.Leagues
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Type == LeagueTypes.Global, cancellationToken);
        if (existing is not null)
            return existing;

        var league = new League { Id = Guid.NewGuid(), Name = "Global", Type = LeagueTypes.Global };
        dbContext.Leagues.Add(league);
        await dbContext.SaveChangesAsync(cancellationToken);
        return league;
    }

    public async Task AddMembershipAsync(Guid leagueId, Guid userId, CancellationToken cancellationToken = default)
    {
        dbContext.LeagueMemberships.Add(new LeagueMembership { LeagueId = leagueId, UserId = userId });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetMemberUserIdsAsync(Guid leagueId, CancellationToken cancellationToken = default) =>
        await dbContext.LeagueMemberships
            .AsNoTracking()
            .Where(m => m.LeagueId == leagueId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
}

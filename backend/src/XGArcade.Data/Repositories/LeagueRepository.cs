using Microsoft.EntityFrameworkCore;
using Npgsql;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class LeagueRepository(XGArcadeDbContext dbContext) : ILeagueRepository
{
    // Matches XGArcadeDbContext's EF-generated index name for the unique
    // index on League.InviteCode ("IX_<Table>_<Column>") — same naming
    // convention UserRepository's DisplayNameUniqueIndexName relies on.
    private const string InviteCodeUniqueIndexName = "IX_Leagues_InviteCode";

    public async Task<League> GetOrCreateGlobalLeagueAsync(CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.Leagues
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Type == LeagueTypes.Global, cancellationToken);
        if (existing is not null)
            return existing;

        var league = new League { Id = Guid.NewGuid(), Name = "Global", Type = LeagueTypes.Global };
        dbContext.Leagues.Add(league);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two concurrent first-ever signups both raced past the check
            // above — the filtered unique index on Type ("global") let only
            // one insert win. Detach this loser's now-invalid tracked entry
            // and return the winner instead of surfacing a raw 500.
            dbContext.Entry(league).State = EntityState.Detached;
            return await dbContext.Leagues
                .AsNoTracking()
                .SingleAsync(l => l.Type == LeagueTypes.Global, cancellationToken);
        }

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

    public async Task RemoveMembershipsByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var memberships = await dbContext.LeagueMemberships.Where(m => m.UserId == userId).ToListAsync(cancellationToken);
        if (memberships.Count == 0)
            return;

        dbContext.LeagueMemberships.RemoveRange(memberships);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<League> AddCustomLeagueAsync(League league, CancellationToken cancellationToken = default)
    {
        dbContext.Leagues.Add(league);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: InviteCodeUniqueIndexName })
        {
            throw new InviteCodeAlreadyInUseException(league.InviteCode!);
        }

        return league;
    }

    public async Task<bool> InviteCodeExistsAsync(string inviteCode, CancellationToken cancellationToken = default) =>
        await dbContext.Leagues.AsNoTracking().AnyAsync(l => l.InviteCode == inviteCode, cancellationToken);

    public async Task<League?> GetByInviteCodeAsync(string inviteCode, CancellationToken cancellationToken = default) =>
        await dbContext.Leagues.AsNoTracking().FirstOrDefaultAsync(l => l.InviteCode == inviteCode, cancellationToken);

    public async Task<bool> IsMemberAsync(Guid leagueId, Guid userId, CancellationToken cancellationToken = default) =>
        await dbContext.LeagueMemberships.AsNoTracking().AnyAsync(m => m.LeagueId == leagueId && m.UserId == userId, cancellationToken);

    public async Task<IReadOnlyList<League>> GetCustomLeaguesByMemberUserIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await (
            from membership in dbContext.LeagueMemberships.AsNoTracking()
            join league in dbContext.Leagues.AsNoTracking() on membership.LeagueId equals league.Id
            where membership.UserId == userId && league.Type == LeagueTypes.Custom
            select league
        ).ToListAsync(cancellationToken);
}

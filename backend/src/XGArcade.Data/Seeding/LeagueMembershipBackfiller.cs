using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Seeding;

// One-time backfill for LeagueMembership (S-011): AuthController.Signup only
// auto-enrolls a user into the global league (REQ-401) at the moment of
// signup — any User row created before this migration/feature shipped has
// no corresponding LeagueMembership at all, and nothing else ever creates
// one for them. Without this, such a user would never appear on the
// leaderboard, silently and permanently, with no error to surface the gap.
// Idempotent: only users with no existing global-league membership are
// touched.
public static class LeagueMembershipBackfiller
{
    public static async Task BackfillAsync(XGArcadeDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var globalLeague = await dbContext.Leagues.FirstOrDefaultAsync(l => l.Type == LeagueTypes.Global, cancellationToken);
        if (globalLeague is null)
        {
            globalLeague = new League { Id = Guid.NewGuid(), Name = "Global", Type = LeagueTypes.Global };
            dbContext.Leagues.Add(globalLeague);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var existingMemberIds = await dbContext.LeagueMemberships
            .Where(m => m.LeagueId == globalLeague.Id)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

        var unenrolledUserIds = await dbContext.Users
            .Where(u => !existingMemberIds.Contains(u.Id))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        foreach (var userId in unenrolledUserIds)
        {
            dbContext.LeagueMemberships.Add(new LeagueMembership { LeagueId = globalLeague.Id, UserId = userId });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

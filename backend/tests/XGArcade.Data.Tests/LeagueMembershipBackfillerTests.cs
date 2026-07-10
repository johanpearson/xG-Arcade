using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// S-011: LeagueMembershipBackfiller fixes User rows that predate REQ-401's
// auto-enrollment-at-signup — the scenario a pre-existing database row
// would be in, since only AuthController.Signup creates a membership today.
public class LeagueMembershipBackfillerTests
{
    private XGArcadeDbContext _dbContext = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private async Task<User> SeedUserAsync()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = $"{Guid.NewGuid()}@example.com",
            DisplayName = "Pre-existing Player",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user;
    }

    [Test]
    public async Task BackfillAsync_UserWithNoMembership_CreatesGlobalLeagueAndEnrollsThem()
    {
        var user = await SeedUserAsync();

        await LeagueMembershipBackfiller.BackfillAsync(_dbContext);

        var globalLeague = await _dbContext.Leagues.AsNoTracking().SingleAsync(l => l.Type == LeagueTypes.Global);
        var membership = await _dbContext.LeagueMemberships.AsNoTracking()
            .SingleOrDefaultAsync(m => m.UserId == user.Id && m.LeagueId == globalLeague.Id);
        Assert.That(membership, Is.Not.Null);
    }

    [Test]
    public async Task BackfillAsync_UserAlreadyEnrolled_DoesNotCreateADuplicateMembership()
    {
        var user = await SeedUserAsync();
        var globalLeague = new League { Id = Guid.NewGuid(), Name = "Global", Type = LeagueTypes.Global };
        _dbContext.Leagues.Add(globalLeague);
        _dbContext.LeagueMemberships.Add(new LeagueMembership { LeagueId = globalLeague.Id, UserId = user.Id });
        await _dbContext.SaveChangesAsync();

        await LeagueMembershipBackfiller.BackfillAsync(_dbContext);

        var membershipCount = await _dbContext.LeagueMemberships.AsNoTracking()
            .CountAsync(m => m.UserId == user.Id);
        Assert.That(membershipCount, Is.EqualTo(1));
    }

    [Test]
    public async Task BackfillAsync_NoUsers_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(async () => await LeagueMembershipBackfiller.BackfillAsync(_dbContext));
    }
}

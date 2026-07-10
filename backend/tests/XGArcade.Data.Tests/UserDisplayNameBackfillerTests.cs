using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// S-011: UserDisplayNameBackfiller fixes User rows whose DisplayName is
// empty — the scenario a pre-migration database row would be in, since the
// migration that added the column defaults existing rows to "".
public class UserDisplayNameBackfillerTests
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

    [Test]
    public async Task BackfillAsync_FixesEmptyDisplayName_UsingEmailLocalPart()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "pre-existing-player@example.com",
            DisplayName = "",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        await UserDisplayNameBackfiller.BackfillAsync(_dbContext);

        var reloaded = await _dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.That(reloaded.DisplayName, Is.EqualTo("pre-existing-player"));
    }

    [Test]
    public async Task BackfillAsync_IsIdempotent_LeavesAnAlreadySetDisplayNameUnchanged()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "chosen-name@example.com",
            DisplayName = "Chosen Name",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        await UserDisplayNameBackfiller.BackfillAsync(_dbContext);

        var reloaded = await _dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.That(reloaded.DisplayName, Is.EqualTo("Chosen Name"));
    }

    [Test]
    public async Task BackfillAsync_NoUsers_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(async () => await UserDisplayNameBackfiller.BackfillAsync(_dbContext));
    }
}

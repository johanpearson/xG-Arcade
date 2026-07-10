using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// S-009: PlayerNormalizedFullNameBackfiller fixes Player rows whose
// NormalizedFullName is stale/empty relative to what PlayerNameNormalizer
// would currently compute — the scenario a pre-existing (pre-migration, or
// pre-punctuation-stripping-fix) database row would be in.
public class PlayerNormalizedFullNameBackfillerTests
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
    public async Task BackfillAsync_FixesStaleEmptyNormalizedFullName()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Kaká", WikidataQid = "Q1" };
        _dbContext.Players.Add(player);
        await _dbContext.SaveChangesAsync();
        // Simulate a row that predates NormalizedFullName's introduction —
        // directly overwrite the tracked column value, bypassing FullName's
        // setter, the same way a pre-migration database row would look.
        _dbContext.Entry(player).Property(p => p.NormalizedFullName).CurrentValue = "";
        await _dbContext.SaveChangesAsync();

        await PlayerNormalizedFullNameBackfiller.BackfillAsync(_dbContext);

        var reloaded = await _dbContext.Players.AsNoTracking().SingleAsync(p => p.Id == player.Id);
        Assert.That(reloaded.NormalizedFullName, Is.EqualTo("kaka"));
    }

    [Test]
    public async Task BackfillAsync_IsIdempotent_NoChangeWhenAlreadyCorrect()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        _dbContext.Players.Add(player);
        await _dbContext.SaveChangesAsync();

        await PlayerNormalizedFullNameBackfiller.BackfillAsync(_dbContext);

        var reloaded = await _dbContext.Players.AsNoTracking().SingleAsync(p => p.Id == player.Id);
        Assert.That(reloaded.NormalizedFullName, Is.EqualTo("thierry henry"));
    }

    [Test]
    public async Task BackfillAsync_NoPlayers_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(async () => await PlayerNormalizedFullNameBackfiller.BackfillAsync(_dbContext));
    }
}

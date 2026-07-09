using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// S-005 (docs/backlog.md): seeds the Tier 0 hand-curated reference data
// (MVP-SCOPE.md's verified QID tables) — pure data entry, so these tests
// check the seeder's mechanics (idempotency, row counts) rather than
// re-verifying the QIDs themselves.
public class ReferenceDataSeederTests
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
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task REQ109_SeedAsync_PopulatesAllCountriesAndClubsFromMvpScope()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        Assert.That(await _dbContext.CountryDefinitions.CountAsync(), Is.EqualTo(20));
        Assert.That(await _dbContext.ClubDefinitions.CountAsync(), Is.EqualTo(15));
    }

    [Test]
    public async Task REQ109_SeedAsync_SeededRows_HaveNonEmptyWikidataQids()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        Assert.That(await _dbContext.CountryDefinitions.AnyAsync(c => string.IsNullOrEmpty(c.WikidataQid)), Is.False);
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => string.IsNullOrEmpty(c.WikidataQid)), Is.False);
    }

    [Test]
    public async Task REQ109_SeedAsync_RunTwice_IsIdempotent_CreatesNoDuplicateRows()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        Assert.That(await _dbContext.CountryDefinitions.CountAsync(), Is.EqualTo(20));
        Assert.That(await _dbContext.ClubDefinitions.CountAsync(), Is.EqualTo(15));
    }

    [Test]
    public async Task REQ109_SeedAsync_DoesNotDuplicate_WhenSomeRowsAlreadyExist()
    {
        _dbContext.CountryDefinitions.Add(new CountryDefinition { Id = Guid.NewGuid(), Name = "France", WikidataQid = "Q142" });
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Arsenal", WikidataQid = "Q9617" });
        await _dbContext.SaveChangesAsync();

        await ReferenceDataSeeder.SeedAsync(_dbContext);

        Assert.That(await _dbContext.CountryDefinitions.CountAsync(c => c.Name == "France"), Is.EqualTo(1));
        Assert.That(await _dbContext.ClubDefinitions.CountAsync(c => c.Name == "Arsenal"), Is.EqualTo(1));
        Assert.That(await _dbContext.CountryDefinitions.CountAsync(), Is.EqualTo(20));
        Assert.That(await _dbContext.ClubDefinitions.CountAsync(), Is.EqualTo(15));
    }

    [Test]
    public async Task REQ109_SeedAsync_UnitedKingdom_IsSeeded_NotEngland()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        Assert.That(await _dbContext.CountryDefinitions.AnyAsync(c => c.Name == "United Kingdom" && c.WikidataQid == "Q145"), Is.True);
        Assert.That(await _dbContext.CountryDefinitions.AnyAsync(c => c.Name == "England"), Is.False);
    }
}

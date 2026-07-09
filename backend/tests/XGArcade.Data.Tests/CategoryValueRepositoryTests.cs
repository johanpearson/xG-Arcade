using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// REQ-109 (docs/requirements-document.md): CountryDefinition/ClubDefinition/
// TrophyDefinition are the sole source of truth for grid category values —
// "the value is picked from these reference tables, never derived ad hoc
// from whatever happens to already be in PlayerAttribute". Grid generation
// itself doesn't exist yet (that's S-007); this is the data-layer-scoped
// slice of that guarantee available today: ICategoryValueRepository (the
// only read surface COMP-05 will call, per ICategoryValueRepository.cs's
// own doc comment) must return exactly the rows in the definition tables
// and must be unaffected by whatever is (or isn't) present in
// PlayerAttribute. Uses EF Core's InMemory provider — a unit test per
// coding-guidelines.md ("repositories, EF Core model config",
// implementation-document.md §7), not an API/integration test.
public class CategoryValueRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private ICategoryValueRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new CategoryValueRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task REQ109_GetCountriesAsync_ReturnsOnlyCountryDefinitionRows_IgnoringPlayerAttributeValues()
    {
        _dbContext.CountryDefinitions.AddRange(
            new CountryDefinition { Id = Guid.NewGuid(), Name = "France", WikidataQid = "Q142" },
            new CountryDefinition { Id = Guid.NewGuid(), Name = "Spain", WikidataQid = "Q29" });
        // A PlayerAttribute value that looks like a plausible country but
        // has no corresponding CountryDefinition row — if the repository
        // (or anything under it) ever derived category values ad hoc from
        // PlayerAttribute, "Brazil" would leak into the result here.
        _dbContext.PlayerAttributes.Add(
            new PlayerAttribute { PlayerId = Guid.NewGuid(), AttributeType = "nationality", AttributeValue = "Brazil" });
        await _dbContext.SaveChangesAsync();

        var countries = await _repository.GetCountriesAsync();

        Assert.That(countries.Select(c => c.Name), Is.EquivalentTo(new[] { "France", "Spain" }));
        Assert.That(countries.Select(c => c.Name), Does.Not.Contain("Brazil"));
    }

    [Test]
    public async Task REQ109_GetClubsAsync_ReturnsOnlyClubDefinitionRows_IgnoringPlayerAttributeValues()
    {
        _dbContext.ClubDefinitions.AddRange(
            new ClubDefinition { Id = Guid.NewGuid(), Name = "Arsenal", WikidataQid = "Q9617" },
            new ClubDefinition { Id = Guid.NewGuid(), Name = "Real Madrid", WikidataQid = "Q8682" });
        // Looks like a plausible club value, but only exists as a
        // PlayerAttribute row — never promoted to ClubDefinition.
        _dbContext.PlayerAttributes.Add(
            new PlayerAttribute { PlayerId = Guid.NewGuid(), AttributeType = "club", AttributeValue = "Boca Juniors" });
        await _dbContext.SaveChangesAsync();

        var clubs = await _repository.GetClubsAsync();

        Assert.That(clubs.Select(c => c.Name), Is.EquivalentTo(new[] { "Arsenal", "Real Madrid" }));
        Assert.That(clubs.Select(c => c.Name), Does.Not.Contain("Boca Juniors"));
    }

    [Test]
    public async Task REQ109_GetTrophiesAsync_ReturnsOnlyTrophyDefinitionRows_IgnoringPlayerAttributeValues()
    {
        _dbContext.TrophyDefinitions.AddRange(
            new TrophyDefinition { Id = Guid.NewGuid(), Name = "FIFA World Cup", IsTeamTrophy = true, WikidataQid = "Q19317" },
            new TrophyDefinition { Id = Guid.NewGuid(), Name = "Ballon d'Or", IsTeamTrophy = false, WikidataQid = "Q166177" });
        // Looks like a plausible trophy value, but only exists as a
        // PlayerAttribute row — never promoted to TrophyDefinition.
        _dbContext.PlayerAttributes.Add(
            new PlayerAttribute { PlayerId = Guid.NewGuid(), AttributeType = "trophy", AttributeValue = "Copa América" });
        await _dbContext.SaveChangesAsync();

        var trophies = await _repository.GetTrophiesAsync();

        Assert.That(trophies.Select(t => t.Name), Is.EquivalentTo(new[] { "FIFA World Cup", "Ballon d'Or" }));
        Assert.That(trophies.Select(t => t.Name), Does.Not.Contain("Copa América"));
    }

    [Test]
    public async Task REQ109_GetCountriesAsync_ReturnsDefinitionRow_EvenWithNoMatchingPlayerAttributeRows()
    {
        // Proves the read is a direct, unjoined select from CountryDefinition
        // rather than something derived from (or filtered by) PlayerAttribute
        // usage — a freshly-added reference-table value with zero players
        // recorded against it yet must still come back.
        _dbContext.CountryDefinitions.Add(
            new CountryDefinition { Id = Guid.NewGuid(), Name = "Iceland", WikidataQid = "Q189" });
        await _dbContext.SaveChangesAsync();

        var countries = await _repository.GetCountriesAsync();

        Assert.That(countries.Select(c => c.Name), Is.EquivalentTo(new[] { "Iceland" }));
    }
}

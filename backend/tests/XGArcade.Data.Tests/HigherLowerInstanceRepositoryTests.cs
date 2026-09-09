using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// COMP-18 (Games.XGHigherLower)/ADR-0110: HigherLowerInstanceRepository's own
// persistence round-trip (AddInstanceAsync/GetInstanceByIdAsync, including
// its owned Comparators collection). Same InMemory-backed DbContext pattern
// as PredictInstanceRepositoryTests.
public class HigherLowerInstanceRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IHigherLowerInstanceRepository _repository = null!;
    private IPlayerRepository _playerRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new HigherLowerInstanceRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task AddInstanceAsync_ThenGetInstanceByIdAsync_ReturnsInstanceWithComparators()
    {
        var baseline = await AddPlayerAsync("Baseline Player");
        var comparatorPlayer = await AddPlayerAsync("Comparator Player");
        var instanceId = Guid.NewGuid();
        var instance = new HigherLowerInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            StatCategory = "trophy",
            BaselinePlayerId = baseline.Id,
            BaselineValue = 3,
            Comparators =
            [
                new HigherLowerComparator
                {
                    Id = Guid.NewGuid(),
                    HigherLowerInstanceId = instanceId,
                    SequencePosition = 0,
                    PlayerId = comparatorPlayer.Id,
                    Value = 5,
                },
            ],
        };

        await _repository.AddInstanceAsync(instance);

        var found = await _repository.GetInstanceByIdAsync(instanceId);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.StatCategory, Is.EqualTo("trophy"));
        Assert.That(found.BaselinePlayerId, Is.EqualTo(baseline.Id));
        Assert.That(found.BaselineValue, Is.EqualTo(3));
        Assert.That(found.Comparators, Has.Count.EqualTo(1));
        Assert.That(found.Comparators[0].PlayerId, Is.EqualTo(comparatorPlayer.Id));
        Assert.That(found.Comparators[0].Value, Is.EqualTo(5));
        Assert.That(found.Comparators[0].SequencePosition, Is.EqualTo(0));
    }

    [Test]
    public async Task GetInstanceByIdAsync_ReturnsNull_WhenNoInstanceMatches()
    {
        var found = await _repository.GetInstanceByIdAsync(Guid.NewGuid());

        Assert.That(found, Is.Null);
    }

    private async Task<Player> AddPlayerAsync(string fullName)
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = fullName };
        await _playerRepository.AddPlayerAsync(player);
        return player;
    }
}

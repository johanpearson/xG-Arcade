using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// ADR-0054: same real-InMemory-repository-plus-FakeWikidataClient pattern as
// PlayerPhotoBackfillServiceTests (docs/coding-guidelines.md "don't
// over-mock").
public class PlayerCareerStintRefreshServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerStoreRepository _playerStoreRepository = null!;
    private FakeWikidataClient _wikidataClient = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerStoreRepository = new PlayerStoreRepository(_dbContext);
        _wikidataClient = new FakeWikidataClient();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerCareerStintRefreshService BuildService() =>
        new(_wikidataClient, _playerStoreRepository, NullLogger<PlayerCareerStintRefreshService>.Instance);

    private async Task<Player> SeedPlayerAsync(string wikidataQid) =>
        await _playerStoreRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = $"Player {wikidataQid}", WikidataQid = wikidataQid });

    [Test]
    public async Task RefreshCareerStintsAsync_PlayerWithNoExistingStints_PersistsEveryFetchedStint()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Monaco", 1994, 1999, 105),
            new WikidataCareerStintEntry("Juventus", 1999, 1999, 16),
            new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerStoreRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Monaco", "Juventus", "Arsenal" }));
    }

    // The whole point of ADR-0054: a club xG Grid's own byproduct queries
    // never happened to discover (not in ClubDefinition, or simply never
    // queried yet) still gets picked up by the full-career fetch.
    [Test]
    public async Task RefreshCareerStintsAsync_ClubNotInAnyPriorByproductData_IsStillPersisted()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Celtic", 2007, 2008, 31));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerStoreRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Does.Contain("Celtic"));
    }

    [Test]
    public async Task RefreshCareerStintsAsync_StintAlreadyPersisted_IsNotDuplicated()
    {
        var player = await SeedPlayerAsync("Q1519");
        await _playerStoreRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254 }]);

        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerStoreRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(1), "an already-stored, identical stint must not be duplicated");
    }

    [Test]
    public async Task RefreshCareerStintsAsync_PlayerWithNoWikidataQid_IsNeverQueried()
    {
        var player = await _playerStoreRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = "No QID Player" });

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        Assert.That(_wikidataClient.QueriedCareerStintBatches, Is.Empty);
    }

    [Test]
    public async Task RefreshCareerStintsAsync_EmptyPlayerIdList_DoesNothing()
    {
        await BuildService().RefreshCareerStintsAsync([]);

        Assert.That(_wikidataClient.QueriedCareerStintBatches, Is.Empty);
    }

    // ADR-0054's core safety property: a Wikidata failure here must never
    // propagate — it would fail the whole xG Path round-generation call it's
    // invoked from (XGPathGameModule.GenerateInstanceAsync), which REQ-103's
    // "never block generation on a Wikidata failure" reasoning forbids.
    [Test]
    public async Task RefreshCareerStintsAsync_WikidataQueryFails_DoesNotThrow_ExistingStintsUntouched()
    {
        var player = await SeedPlayerAsync("Q1519");
        await _playerStoreRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);

        _wikidataClient.FailNextCareerStintBatches(1);

        Assert.DoesNotThrowAsync(async () => await BuildService().RefreshCareerStintsAsync([player.Id]));

        var stints = await _playerStoreRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Arsenal" }),
            "a failed refresh must leave whatever data already existed untouched, not wipe it");
    }

    [Test]
    public async Task RefreshCareerStintsAsync_PlayerWithNoWikidataCareerData_PersistsNothing_IsNotTreatedAsAFailure()
    {
        var player = await SeedPlayerAsync("Q1519"); // No SetCareerStints call — genuinely no P54 data.

        Assert.DoesNotThrowAsync(async () => await BuildService().RefreshCareerStintsAsync([player.Id]));

        Assert.That(await _playerStoreRepository.GetCareerStintsAsync(player.Id), Is.Empty);
    }
}

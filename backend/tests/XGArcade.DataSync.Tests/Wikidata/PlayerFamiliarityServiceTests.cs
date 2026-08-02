using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// ADR-0056: same real-InMemory-repository-plus-FakeWikidataClient pattern as
// PlayerCareerStintRefreshServiceTests (docs/coding-guidelines.md "don't
// over-mock").
public class PlayerFamiliarityServiceTests
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

    private PlayerFamiliarityService BuildService() =>
        new(_wikidataClient, _playerStoreRepository, NullLogger<PlayerFamiliarityService>.Instance);

    private async Task<Player> SeedPlayerAsync(string wikidataQid) =>
        await _playerStoreRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = $"Player {wikidataQid}", WikidataQid = wikidataQid });

    [Test]
    public async Task FilterFamiliarAsync_SitelinkCountAtOrAboveThreshold_IsIncluded()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetSitelinkCount("Q1519", PlayerFamiliarityService.MinSitelinkCount);

        var result = await BuildService().FilterFamiliarAsync([player.Id]);

        Assert.That(result, Does.Contain(player.Id), "the check is >=, not >");
    }

    [Test]
    public async Task FilterFamiliarAsync_SitelinkCountBelowThreshold_IsExcluded()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetSitelinkCount("Q1519", PlayerFamiliarityService.MinSitelinkCount - 1);

        var result = await BuildService().FilterFamiliarAsync([player.Id]);

        Assert.That(result, Does.Not.Contain(player.Id));
    }

    [Test]
    public async Task FilterFamiliarAsync_NoSitelinkBindingResolvedForAKnownQid_IsExcluded()
    {
        // QuerySitelinkCountsByQidsAsync's own "absent means unknown, never
        // confirmed 0" contract — the familiarity filter still must not give
        // an unresolved candidate the benefit of the doubt.
        var player = await SeedPlayerAsync("Q1519");
        var otherFamiliarPlayer = await SeedPlayerAsync("Q9617");
        _wikidataClient.SetSitelinkCount("Q9617", PlayerFamiliarityService.MinSitelinkCount);

        var result = await BuildService().FilterFamiliarAsync([player.Id, otherFamiliarPlayer.Id]);

        Assert.That(result, Does.Not.Contain(player.Id));
        Assert.That(result, Does.Contain(otherFamiliarPlayer.Id));
    }

    [Test]
    public async Task FilterFamiliarAsync_PlayerWithNoWikidataQid_IsExcluded_WhenOtherCandidatesCanBeChecked()
    {
        var noQidPlayer = await _playerStoreRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "No QID Player" });
        var checkable = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetSitelinkCount("Q1519", PlayerFamiliarityService.MinSitelinkCount);

        var result = await BuildService().FilterFamiliarAsync([noQidPlayer.Id, checkable.Id]);

        Assert.That(result, Does.Not.Contain(noQidPlayer.Id));
        Assert.That(result, Does.Contain(checkable.Id));
    }

    [Test]
    public async Task FilterFamiliarAsync_NoCandidateHasAResolvableWikidataQid_FailsOpen_ReturnsWholePoolUnfiltered()
    {
        var noQidPlayer1 = await _playerStoreRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "No QID Player 1" });
        var noQidPlayer2 = await _playerStoreRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "No QID Player 2" });

        var result = await BuildService().FilterFamiliarAsync([noQidPlayer1.Id, noQidPlayer2.Id]);

        Assert.That(result, Is.EquivalentTo(new[] { noQidPlayer1.Id, noQidPlayer2.Id }),
            "a systemic inability to fame-check anyone must never be treated as 'everyone failed the filter'");
    }

    [Test]
    public async Task FilterFamiliarAsync_WikidataQueryExceptionDuringSitelinkBatch_FailsOpen_ReturnsWholePoolUnfiltered()
    {
        var player1 = await SeedPlayerAsync("Q1519");
        var player2 = await SeedPlayerAsync("Q9617");
        _wikidataClient.FailNextSitelinkBatches(1);

        var result = await BuildService().FilterFamiliarAsync([player1.Id, player2.Id]);

        Assert.That(result, Is.EquivalentTo(new[] { player1.Id, player2.Id }),
            "REQ-103's established reasoning: never block round generation on a Wikidata failure — skip the filter for this round instead");
    }

    [Test]
    public async Task FilterFamiliarAsync_EmptyCandidateList_ReturnsEmptySet_WithoutQueryingWikidata()
    {
        var result = await BuildService().FilterFamiliarAsync([]);

        Assert.That(result, Is.Empty);
        Assert.That(_wikidataClient.QueriedSitelinkBatches, Is.Empty);
    }

    [Test]
    public async Task FilterFamiliarAsync_CandidatePoolLargerThanBatchSize_IsQueriedInMultipleBatches()
    {
        var players = new List<Player>();
        for (var i = 0; i < PlayerFamiliarityService.BatchSize + 1; i++)
        {
            var player = await SeedPlayerAsync($"Q{1000 + i}");
            _wikidataClient.SetSitelinkCount(player.WikidataQid!, PlayerFamiliarityService.MinSitelinkCount);
            players.Add(player);
        }

        var result = await BuildService().FilterFamiliarAsync(players.Select(p => p.Id).ToList());

        Assert.That(_wikidataClient.QueriedSitelinkBatches, Has.Count.EqualTo(2),
            $"{PlayerFamiliarityService.BatchSize + 1} candidates must be split across two batches of at most {PlayerFamiliarityService.BatchSize}");
        Assert.That(result, Has.Count.EqualTo(players.Count));
    }
}

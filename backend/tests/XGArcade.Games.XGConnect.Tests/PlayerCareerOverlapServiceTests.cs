using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect.Tests;

// REQ-1404 (docs/requirements-document.md §4.15)/S-211: the shared
// career-overlap check in isolation, independent of
// ConnectTargetPickService's own orchestration (covered separately in
// ConnectTargetPickServiceTests.cs). Same real-InMemory-repository-plus-fake
// pattern as XGArcade.DataSync.Tests.Wikidata.PlayerCareerStintRefreshServiceTests
// (ADR-0054, docs/coding-guidelines.md "don't over-mock") — as of the
// 2026-09-02 architecture-review follow-up, PlayerCareerOverlapService
// delegates its refresh to IPlayerCareerStintRefreshService rather than
// IWikidataClient directly, so this fixture uses
// FakePlayerCareerStintRefreshService instead of the pre-refactor
// FakeWikidataClient.
public class PlayerCareerOverlapServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerCareerStintRepository _playerCareerStintRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private FakePlayerCareerStintRefreshService _careerStintRefreshService = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerCareerStintRepository = new PlayerCareerStintRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _careerStintRefreshService = new FakePlayerCareerStintRefreshService(_playerCareerStintRepository);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerCareerOverlapService BuildService() =>
        new(_playerCareerStintRepository, _careerStintRefreshService);

    private async Task<Player> SeedPlayerAsync(string? wikidataQid = null) =>
        await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = $"Player {Guid.NewGuid():N}", WikidataQid = wikidataQid });

    // ---- Overlap-detection logic itself ------------------------------------

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_SharedClubOverlappingYears_ReturnsTrue()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Arsenal", StartYear = 2003, EndYear = 2010 }]);

        var overlaps = await BuildService().HaveSharedClubOverlapAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Is.True);
    }

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_SharedClubNonOverlappingYears_ReturnsFalse()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2003 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Arsenal", StartYear = 2010, EndYear = 2015 }]);

        var overlaps = await BuildService().HaveSharedClubOverlapAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Is.False);
    }

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_DifferentClubs_ReturnsFalse()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Chelsea", StartYear = 1999, EndYear = 2007 }]);

        var overlaps = await BuildService().HaveSharedClubOverlapAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Is.False);
    }

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_OngoingStintOverlapsWithLaterStint_ReturnsTrue()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        // Player A's stint is still ongoing (EndYear null) as of the data
        // snapshot — must still be treated as overlapping a later, bounded
        // stint at the same club, not as "unknown"/excluded.
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 2015, EndYear = null }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Arsenal", StartYear = 2018, EndYear = 2020 }]);

        var overlaps = await BuildService().HaveSharedClubOverlapAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Is.True);
    }

    // ---- "Fetch once, cache forever" behavior ------------------------------

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_BothPlayersHaveCachedStints_DoesNotCallRefreshService()
    {
        var playerA = await SeedPlayerAsync("Q1");
        var playerB = await SeedPlayerAsync("Q2");
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Chelsea", StartYear = 1999, EndYear = 2007 }]);
        // Configured on the fake but must never be reached — proves the
        // "already cached" short-circuit skips the live refresh entirely.
        _careerStintRefreshService.SetCareerStints(playerA.Id,
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Should Never Be Reached", StartYear = 2000, EndYear = 2001 });

        await BuildService().HaveSharedClubOverlapAsync(playerA.Id, playerB.Id);

        Assert.That(_careerStintRefreshService.Calls, Is.Empty,
            "a player who already has cached PlayerCareerStint rows must never trigger a live refresh");
    }

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_OnePlayerHasNoCachedStints_TriggersExactlyOneBatchedCallCoveringOnlyThatPlayer()
    {
        var playerA = await SeedPlayerAsync("Q1"); // Already cached.
        var playerB = await SeedPlayerAsync("Q2"); // Zero cached rows — needs a refresh.
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);
        _careerStintRefreshService.SetCareerStints(playerB.Id,
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Arsenal", StartYear = 2003, EndYear = 2010, AppearanceCount = 100 });

        var overlaps = await BuildService().HaveSharedClubOverlapAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Is.True, "the freshly-fetched stint must be persisted and used in the same call");
        Assert.That(_careerStintRefreshService.Calls, Has.Count.EqualTo(1),
            "exactly one batched refresh call, even though only one of the two players needed a refresh");
        Assert.That(_careerStintRefreshService.Calls[0], Is.EquivalentTo(new[] { playerB.Id }),
            "the batch must cover only the player that actually needs a refresh, not the already-cached one");
        var persisted = await _playerCareerStintRepository.GetCareerStintsAsync(playerB.Id);
        Assert.That(persisted.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Arsenal" }));
    }

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_WikidataQueryExceptionOnLiveLookup_ThrowsLiveLookupUnavailableException()
    {
        var playerA = await SeedPlayerAsync("Q1"); // Zero cached rows.
        var playerB = await SeedPlayerAsync("Q2"); // Zero cached rows.
        _careerStintRefreshService.FailNextBatches(1);

        Assert.ThrowsAsync<LiveLookupUnavailableException>(
            async () => await BuildService().HaveSharedClubOverlapAsync(playerA.Id, playerB.Id),
            "a Wikidata technical failure must surface as LiveLookupUnavailableException — genuinely unknown, never swallowed to false/true");
    }
}

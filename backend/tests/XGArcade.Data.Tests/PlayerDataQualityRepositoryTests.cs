using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// audit-club-gaps diagnostic (GetUnseededClubCandidatesAsync). Split out of
// PlayerStoreRepositoryTests.cs (S-107, docs/backlog.md Epic 8, pure
// refactor — see ADR-0067 for the full split) — test bodies/assertions are
// unchanged from their original PlayerStoreRepositoryTests.cs form, this is
// a structural move only.
// _playerRepository/_playerCareerStintRepository below are only used to
// seed fixtures — AddPlayerAsync/AddCareerStintsAsync themselves are
// covered directly in PlayerRepositoryTests.cs/PlayerCareerStintRepositoryTests.cs.
//
// Pre-existing gap, carried over unchanged from the original
// PlayerStoreRepositoryTests.cs (not introduced by this split): IsConfirmedLowAsync/
// RecordConfirmedLowAsync/IsPersistentTechnicalFailureAsync/
// RecordTechnicalFailureAsync/ClearTechnicalFailureAsync have no direct
// repository-level test coverage in this file — they're exercised only
// indirectly, through the real repository, by GridGameModuleTests.cs/
// PlayerCacheWarmingServiceTests.cs. Not fixed here (pure refactor, no new
// REQ IDs, no behavior change) — flagged so a future session adding direct
// coverage knows this is where it belongs.
public class PlayerDataQualityRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerDataQualityRepository _repository = null!;
    private IPlayerRepository _playerRepository = null!;
    private IPlayerCareerStintRepository _playerCareerStintRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PlayerDataQualityRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _playerCareerStintRepository = new PlayerCareerStintRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    // ---- audit-club-gaps diagnostic (GetUnseededClubCandidatesAsync) -------

    [Test]
    public async Task GetUnseededClubCandidatesAsync_ExcludesClubsAlreadyInClubDefinition()
    {
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Arsenal", WikidataQid = "Q9617" });
        await _dbContext.SaveChangesAsync();

        var seededClubPlayer = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        var unseededClubPlayer = new Player { Id = Guid.NewGuid(), FullName = "Someone Else", WikidataQid = "Q999" };
        await _playerRepository.AddPlayerAsync(seededClubPlayer);
        await _playerRepository.AddPlayerAsync(unseededClubPlayer);
        await _playerCareerStintRepository.AddCareerStintsAsync(seededClubPlayer.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = seededClubPlayer.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(unseededClubPlayer.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = unseededClubPlayer.Id, ClubName = "Napoli", StartYear = 2010, EndYear = 2015 }]);

        var candidates = await _repository.GetUnseededClubCandidatesAsync(30);

        Assert.That(candidates.Select(c => c.ClubName), Is.EquivalentTo(new[] { "Napoli" }),
            "Arsenal already has a matching ClubDefinition and must not be surfaced as a gap");
    }

    [Test]
    public async Task GetUnseededClubCandidatesAsync_CountsDistinctPlayers_NotStints()
    {
        var playerWithTwoStints = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "Q1" };
        await _playerRepository.AddPlayerAsync(playerWithTwoStints);
        // Two separate stints at the same unseeded club (e.g. a loan then a
        // later permanent return) — must still count as ONE distinct player.
        await _playerCareerStintRepository.AddCareerStintsAsync(playerWithTwoStints.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerWithTwoStints.Id, ClubName = "Napoli", StartYear = 2005, EndYear = 2007 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerWithTwoStints.Id, ClubName = "Napoli", StartYear = 2010, EndYear = 2012 },
        ]);

        var candidates = await _repository.GetUnseededClubCandidatesAsync(30);

        Assert.That(candidates, Has.Count.EqualTo(1));
        Assert.That(candidates[0].ClubName, Is.EqualTo("Napoli"));
        Assert.That(candidates[0].PlayerCount, Is.EqualTo(1), "two stints for the same player at the same club must count as one distinct player");
    }

    [Test]
    public async Task GetUnseededClubCandidatesAsync_OrdersByDistinctPlayerCountDescending()
    {
        var playerA = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "Q1" };
        var playerB = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "Q2" };
        var playerC = new Player { Id = Guid.NewGuid(), FullName = "Player C", WikidataQid = "Q3" };
        await _playerRepository.AddPlayerAsync(playerA);
        await _playerRepository.AddPlayerAsync(playerB);
        await _playerRepository.AddPlayerAsync(playerC);

        // "Popular Unseeded Club": 2 distinct players. "Rare Unseeded Club": 1.
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Popular Unseeded Club", StartYear = 2000, EndYear = 2005 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Popular Unseeded Club", StartYear = 2001, EndYear = 2006 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerC.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerC.Id, ClubName = "Rare Unseeded Club", StartYear = 2002, EndYear = 2004 }]);

        var candidates = await _repository.GetUnseededClubCandidatesAsync(30);

        Assert.That(candidates.Select(c => c.ClubName), Is.EqualTo(new[] { "Popular Unseeded Club", "Rare Unseeded Club" }));
        Assert.That(candidates[0].PlayerCount, Is.EqualTo(2));
        Assert.That(candidates[1].PlayerCount, Is.EqualTo(1));
    }

    [Test]
    public async Task GetUnseededClubCandidatesAsync_RespectsTopLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            var player = new Player { Id = Guid.NewGuid(), FullName = $"Player {i}", WikidataQid = $"Q{i}" };
            await _playerRepository.AddPlayerAsync(player);
            await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
                [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = $"Unseeded Club {i}", StartYear = 2000, EndYear = 2005 }]);
        }

        var candidates = await _repository.GetUnseededClubCandidatesAsync(3);

        Assert.That(candidates, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task GetUnseededClubCandidatesAsync_CaseInsensitiveMatch_ExcludesClubDespiteCaseDifference()
    {
        // Flagged assumption (see GetUnseededClubCandidatesAsync's own doc
        // comment): a case-only difference between a Wikidata-sourced
        // ClubName and a hand-seeded ClubDefinition.Name is treated as the
        // same club, not a gap.
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Arsenal", WikidataQid = "Q9617" });
        await _dbContext.SaveChangesAsync();

        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "ARSENAL", StartYear = 1999, EndYear = 2007 }]);

        var candidates = await _repository.GetUnseededClubCandidatesAsync(30);

        Assert.That(candidates, Is.Empty, "a case-only difference from a seeded ClubDefinition.Name must not be surfaced as a gap");
    }

    [Test]
    public async Task GetUnseededClubCandidatesAsync_ReturnsEmpty_WhenNoCareerStintsExist()
    {
        var candidates = await _repository.GetUnseededClubCandidatesAsync(30);

        Assert.That(candidates, Is.Empty);
    }
}

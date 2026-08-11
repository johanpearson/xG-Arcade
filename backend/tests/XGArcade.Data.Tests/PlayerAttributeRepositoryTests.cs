using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// S-106 (docs/backlog.md, Epic 8, pure refactor): moved out of
// PlayerStoreRepositoryTests.cs — see PlayerRepositoryTests.cs's own header
// comment for the full "why no REQ-prefixed class name" rationale, which
// applies identically here. Test bodies/assertions are unchanged from their
// original PlayerStoreRepositoryTests.cs form — this is a structural move
// only. _playerRepository is only used to seed the Player rows each
// PlayerAttribute row's FK requires — AddPlayerAsync itself moved to
// IPlayerRepository (PlayerRepositoryTests.cs covers it directly).
public class PlayerAttributeRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerAttributeRepository _repository = null!;
    private IPlayerRepository _playerRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PlayerAttributeRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task AddPlayerAttributeAsync_ThenGetPlayerAttributesAsync_ReturnsOnlyMatchingTypeAndValue()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" });
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" });

        var clubAttributes = await _repository.GetPlayerAttributesAsync("club", "Arsenal");

        Assert.That(clubAttributes, Has.Count.EqualTo(1));
        Assert.That(clubAttributes[0].PlayerId, Is.EqualTo(player.Id));
    }

    // ---- REQ-208: guess-time alias/fuzzy matching's supporting repository
    // methods (GridGameModule.FindMatchAsync/FindFuzzyCandidatesAsync) -------

    [Test]
    public async Task GetPlayersWithEitherAttributeAsync_ReturnsPlayersSatisfyingEitherAttribute_DistinctAndNoOthers()
    {
        var satisfiesFirst = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA" };
        var satisfiesSecond = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QB" };
        var satisfiesBoth = new Player { Id = Guid.NewGuid(), FullName = "Player C", WikidataQid = "QC" };
        var satisfiesNeither = new Player { Id = Guid.NewGuid(), FullName = "Player D", WikidataQid = "QD" };
        foreach (var p in new[] { satisfiesFirst, satisfiesSecond, satisfiesBoth, satisfiesNeither })
            await _playerRepository.AddPlayerAsync(p);

        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = satisfiesFirst.Id, AttributeType = "nationality", AttributeValue = "France" });
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = satisfiesSecond.Id, AttributeType = "club", AttributeValue = "Arsenal" });
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = satisfiesBoth.Id, AttributeType = "nationality", AttributeValue = "France" });
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = satisfiesBoth.Id, AttributeType = "club", AttributeValue = "Arsenal" });
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = satisfiesNeither.Id, AttributeType = "nationality", AttributeValue = "England" });

        var found = await _repository.GetPlayersWithEitherAttributeAsync("nationality", "France", "club", "Arsenal");

        Assert.That(found.Select(p => p.Id), Is.EquivalentTo(new[] { satisfiesFirst.Id, satisfiesSecond.Id, satisfiesBoth.Id }));
    }

    [Test]
    public async Task GetPlayersWithEitherAttributeAsync_ReturnsEmpty_WhenNoPlayerSatisfiesEither()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "England" });

        var found = await _repository.GetPlayersWithEitherAttributeAsync("nationality", "France", "club", "Arsenal");

        Assert.That(found, Is.Empty);
    }

    // ---- REQ-209: disambiguation-prompt candidate building -----------------

    [Test]
    public async Task GetPlayerAttributesByPlayerIdsAsync_ReturnsOnlyRequestedPlayersAttributes_GroupedByPlayerId()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "John Smith", WikidataQid = "Q1" };
        var otherPlayer = new Player { Id = Guid.NewGuid(), FullName = "John Smith", WikidataQid = "Q2" };
        var uninvolvedPlayer = new Player { Id = Guid.NewGuid(), FullName = "Someone Else", WikidataQid = "Q3" };
        await _playerRepository.AddPlayerAsync(player);
        await _playerRepository.AddPlayerAsync(otherPlayer);
        await _playerRepository.AddPlayerAsync(uninvolvedPlayer);
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Monaco" });
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = otherPlayer.Id, AttributeType = "club", AttributeValue = "Lyon" });
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = uninvolvedPlayer.Id, AttributeType = "club", AttributeValue = "Nope" });

        var attributesByPlayerId = await _repository.GetPlayerAttributesByPlayerIdsAsync([player.Id, otherPlayer.Id]);

        Assert.That(attributesByPlayerId.Keys, Is.EquivalentTo(new[] { player.Id, otherPlayer.Id }));
        Assert.That(attributesByPlayerId[player.Id].Single().AttributeValue, Is.EqualTo("Monaco"));
        Assert.That(attributesByPlayerId[otherPlayer.Id].Single().AttributeValue, Is.EqualTo("Lyon"));
    }

    [Test]
    public async Task GetPlayerAttributesByPlayerIdsAsync_PlayerWithNoAttributes_IsAbsentFromResult()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "John Smith", WikidataQid = "Q1" };
        await _playerRepository.AddPlayerAsync(player);

        var attributesByPlayerId = await _repository.GetPlayerAttributesByPlayerIdsAsync([player.Id]);

        Assert.That(attributesByPlayerId.Keys, Is.Empty);
    }

    [Test]
    public async Task GetPlayerAttributesByPlayerIdsAsync_EmptyIdList_ReturnsEmptyDictionary()
    {
        var attributesByPlayerId = await _repository.GetPlayerAttributesByPlayerIdsAsync([]);

        Assert.That(attributesByPlayerId, Is.Empty);
    }

    // ---- Bug-bundle fix (2026-07-27): batched player-persist methods -------

    [Test]
    public async Task AddPlayerAttributesBatchAsync_PersistsEveryRow_InOneCall()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);

        await _repository.AddPlayerAttributesBatchAsync([
            new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" },
            new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" },
        ]);

        var attributes = await _repository.GetPlayerAttributesByPlayerIdsAsync([player.Id]);
        Assert.That(attributes[player.Id], Has.Count.EqualTo(2));
    }

    [Test]
    public void AddPlayerAttributesBatchAsync_EmptyList_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _repository.AddPlayerAttributesBatchAsync([]));
    }
}

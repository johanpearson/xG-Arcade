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
// PlayerAlias row's FK requires — AddPlayerAsync itself moved to
// IPlayerRepository (PlayerRepositoryTests.cs covers it directly).
public class PlayerAliasRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerAliasRepository _repository = null!;
    private IPlayerRepository _playerRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PlayerAliasRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task AddPlayerAliasAsync_ThenGetPlayerAliasesAsync_ReturnsOnlyThatPlayersAliases()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        var otherPlayer = new Player { Id = Guid.NewGuid(), FullName = "Kaka", WikidataQid = "Q123" };
        await _playerRepository.AddPlayerAsync(player);
        await _playerRepository.AddPlayerAsync(otherPlayer);
        await _repository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = player.Id, Alias = "Titi", NormalizedAlias = "titi" });
        await _repository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = otherPlayer.Id, Alias = "Kaka", NormalizedAlias = "kaka" });

        var aliases = await _repository.GetPlayerAliasesAsync(player.Id);

        Assert.That(aliases, Has.Count.EqualTo(1));
        Assert.That(aliases[0].Alias, Is.EqualTo("Titi"));
    }

    [Test]
    public async Task GetPlayerAliasesAsync_ReturnsEmpty_WhenPlayerHasNoAliases()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);

        var aliases = await _repository.GetPlayerAliasesAsync(player.Id);

        Assert.That(aliases, Is.Empty);
    }

    // ---- REQ-208: guess-time alias/fuzzy matching's supporting repository
    // methods (GridGameModule.FindMatchAsync/FindFuzzyCandidatesAsync) -------

    [Test]
    public async Task GetPlayersByNormalizedAliasAsync_ReturnsPlayer_WhenNormalizedAliasMatches()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Ricardo Izecson dos Santos Leite", WikidataQid = "Q123" };
        await _playerRepository.AddPlayerAsync(player);
        await _repository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = player.Id, Alias = "Kaka", NormalizedAlias = "kaka" });

        var found = await _repository.GetPlayersByNormalizedAliasAsync("kaka");

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].Id, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task GetPlayersByNormalizedAliasAsync_ReturnsEmpty_WhenNoAliasMatches()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _repository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = player.Id, Alias = "Titi", NormalizedAlias = "titi" });

        var found = await _repository.GetPlayersByNormalizedAliasAsync("kaka");

        Assert.That(found, Is.Empty);
    }

    [Test]
    public async Task GetPlayerAliasesByPlayerIdsAsync_ReturnsOnlyRequestedPlayersAliases_GroupedByPlayerId()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Ricardo Izecson dos Santos Leite", WikidataQid = "Q123" };
        var otherPlayer = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        var uninvolvedPlayer = new Player { Id = Guid.NewGuid(), FullName = "Someone Else", WikidataQid = "Q999" };
        await _playerRepository.AddPlayerAsync(player);
        await _playerRepository.AddPlayerAsync(otherPlayer);
        await _playerRepository.AddPlayerAsync(uninvolvedPlayer);
        await _repository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = player.Id, Alias = "Kaka", NormalizedAlias = "kaka" });
        await _repository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = otherPlayer.Id, Alias = "Titi", NormalizedAlias = "titi" });
        await _repository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = uninvolvedPlayer.Id, Alias = "Nope", NormalizedAlias = "nope" });

        var aliasesByPlayerId = await _repository.GetPlayerAliasesByPlayerIdsAsync([player.Id, otherPlayer.Id]);

        Assert.That(aliasesByPlayerId.Keys, Is.EquivalentTo(new[] { player.Id, otherPlayer.Id }));
        Assert.That(aliasesByPlayerId[player.Id].Single().NormalizedAlias, Is.EqualTo("kaka"));
        Assert.That(aliasesByPlayerId[otherPlayer.Id].Single().NormalizedAlias, Is.EqualTo("titi"));
    }

    [Test]
    public async Task GetPlayerAliasesByPlayerIdsAsync_EmptyIdList_ReturnsEmptyDictionary()
    {
        var aliasesByPlayerId = await _repository.GetPlayerAliasesByPlayerIdsAsync([]);

        Assert.That(aliasesByPlayerId, Is.Empty);
    }

    // ---- Bug-bundle fix (2026-07-27): batched player-persist methods -------

    [Test]
    public async Task AddPlayerAliasesBatchAsync_PersistsEveryRow_InOneCall()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Ricardo Izecson dos Santos Leite", WikidataQid = "Qkaka" };
        await _playerRepository.AddPlayerAsync(player);

        await _repository.AddPlayerAliasesBatchAsync([
            new PlayerAlias { PlayerId = player.Id, Alias = "Kaka", NormalizedAlias = "kaka" },
        ]);

        var aliases = await _repository.GetPlayerAliasesAsync(player.Id);
        Assert.That(aliases, Has.Count.EqualTo(1));
        Assert.That(aliases[0].Alias, Is.EqualTo("Kaka"));
    }

    [Test]
    public void AddPlayerAliasesBatchAsync_EmptyList_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _repository.AddPlayerAliasesBatchAsync([]));
    }
}

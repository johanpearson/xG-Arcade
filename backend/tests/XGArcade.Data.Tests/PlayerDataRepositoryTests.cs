using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// S-106 (docs/backlog.md, Epic 8, pure refactor): moved out of
// PlayerStoreRepositoryTests.cs — see PlayerRepositoryTests.cs's own header
// comment for the full "why no REQ-prefixed class name" rationale, which
// applies identically here. Test bodies/assertions are unchanged from their
// original PlayerStoreRepositoryTests.cs form — this is a structural move
// only. _playerRepository is only used to seed the Player row each
// PlayerData row's FK requires — AddPlayerAsync itself moved to
// IPlayerRepository (PlayerRepositoryTests.cs covers it directly).
public class PlayerDataRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerDataRepository _repository = null!;
    private IPlayerRepository _playerRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PlayerDataRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task AddPlayerDataAsync_PersistsRawSourceData()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);

        await _repository.AddPlayerDataAsync(new PlayerData
        {
            Id = Guid.NewGuid(),
            PlayerId = player.Id,
            Field = "club",
            Value = "Arsenal",
            Source = "wikidata",
            Confidence = "unverified",
            SyncedAt = DateTime.UtcNow,
        });

        var stored = await _dbContext.PlayerData.SingleAsync(pd => pd.PlayerId == player.Id);
        Assert.That(stored.Value, Is.EqualTo("Arsenal"));
    }

    [Test]
    public async Task GetUnverifiedPlayerDataAsync_ReturnsOnlyUnverifiedRows()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _repository.AddPlayerDataAsync(new PlayerData
        {
            Id = Guid.NewGuid(), PlayerId = player.Id, Field = "club", Value = "Arsenal",
            Source = "wikidata", Confidence = "unverified", SyncedAt = DateTime.UtcNow,
        });
        await _repository.AddPlayerDataAsync(new PlayerData
        {
            Id = Guid.NewGuid(), PlayerId = player.Id, Field = "nationality", Value = "France",
            Source = "wikidata", Confidence = "verified", SyncedAt = DateTime.UtcNow,
        });

        var unverified = await _repository.GetUnverifiedPlayerDataAsync();

        Assert.That(unverified, Has.Count.EqualTo(1));
        Assert.That(unverified[0].Field, Is.EqualTo("club"));
    }

    // ---- REQ-503 (2026-07-20 extension): ApprovePlayerDataAsync -----------

    private async Task<Guid> SeedUnverifiedPlayerDataAsync(Guid playerId, string field = "club", string value = "Arsenal")
    {
        var data = new PlayerData
        {
            Id = Guid.NewGuid(), PlayerId = playerId, Field = field, Value = value,
            Source = "wikidata", Confidence = "unverified", SyncedAt = DateTime.UtcNow,
        };
        await _repository.AddPlayerDataAsync(data);
        return data.Id;
    }

    [Test]
    public async Task REQ503_ApprovePlayerDataAsync_SingleRow_FlipsConfidenceToVerified_AndLogsAdminIdAndTimestamp()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        var dataId = await SeedUnverifiedPlayerDataAsync(player.Id);
        var adminId = Guid.NewGuid();

        var outcomes = await _repository.ApprovePlayerDataAsync([dataId], adminId);

        Assert.That(outcomes, Has.Count.EqualTo(1));
        Assert.That(outcomes[0].PlayerDataId, Is.EqualTo(dataId));
        Assert.That(outcomes[0].Approved, Is.True);
        Assert.That(outcomes[0].FailureReason, Is.Null);

        var stored = await _dbContext.PlayerData.SingleAsync(pd => pd.Id == dataId);
        Assert.That(stored.Confidence, Is.EqualTo("verified"));
        Assert.That(stored.ApprovedByAdminId, Is.EqualTo(adminId));
        Assert.That(stored.ApprovedAt, Is.Not.Null);
    }

    [Test]
    public async Task REQ503_ApprovePlayerDataAsync_Bulk_ApprovesEveryRow_EachWithItsOwnAdminIdAndTimestamp()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        var firstId = await SeedUnverifiedPlayerDataAsync(player.Id, "club", "Arsenal");
        var secondId = await SeedUnverifiedPlayerDataAsync(player.Id, "nationality", "France");
        var adminId = Guid.NewGuid();

        var outcomes = await _repository.ApprovePlayerDataAsync([firstId, secondId], adminId);

        Assert.That(outcomes, Has.Count.EqualTo(2));
        Assert.That(outcomes, Has.All.Matches<PlayerDataApprovalOutcome>(o => o.Approved));

        var rows = await _dbContext.PlayerData.Where(pd => pd.Id == firstId || pd.Id == secondId).ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows, Has.All.Matches<PlayerData>(pd => pd.Confidence == "verified" && pd.ApprovedByAdminId == adminId && pd.ApprovedAt != null));
    }

    [Test]
    public async Task REQ503_ApprovePlayerDataAsync_UnknownId_ReportsNotFound_WithoutAffectingOtherRows()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        var realId = await SeedUnverifiedPlayerDataAsync(player.Id);
        var missingId = Guid.NewGuid();

        var outcomes = await _repository.ApprovePlayerDataAsync([realId, missingId], Guid.NewGuid());

        var realOutcome = outcomes.Single(o => o.PlayerDataId == realId);
        var missingOutcome = outcomes.Single(o => o.PlayerDataId == missingId);
        Assert.That(realOutcome.Approved, Is.True, "a deleted/unknown row in the same batch must not block the rest from succeeding");
        Assert.That(missingOutcome.Approved, Is.False);
        Assert.That(missingOutcome.FailureReason, Is.EqualTo(PlayerDataApprovalFailureReason.NotFound));

        var stored = await _dbContext.PlayerData.SingleAsync(pd => pd.Id == realId);
        Assert.That(stored.Confidence, Is.EqualTo("verified"));
    }

    [Test]
    public async Task REQ503_ApprovePlayerDataAsync_RowAlreadyVerified_ReportsNotUnverified_AndLeavesItUnchanged()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        var data = new PlayerData
        {
            Id = Guid.NewGuid(), PlayerId = player.Id, Field = "club", Value = "Arsenal",
            Source = "wikidata", Confidence = "verified", SyncedAt = DateTime.UtcNow,
        };
        await _repository.AddPlayerDataAsync(data);

        var outcomes = await _repository.ApprovePlayerDataAsync([data.Id], Guid.NewGuid());

        Assert.That(outcomes[0].Approved, Is.False, "a row already changed away from 'unverified' between selection and submission must fail, not silently re-approve");
        Assert.That(outcomes[0].FailureReason, Is.EqualTo(PlayerDataApprovalFailureReason.NotUnverified));

        var stored = await _dbContext.PlayerData.SingleAsync(pd => pd.Id == data.Id);
        Assert.That(stored.ApprovedByAdminId, Is.Null);
        Assert.That(stored.ApprovedAt, Is.Null);
    }

    [Test]
    public async Task REQ503_ApprovePlayerDataAsync_EmptyIdCollection_ReturnsEmptyOutcomes()
    {
        var outcomes = await _repository.ApprovePlayerDataAsync([], Guid.NewGuid());

        Assert.That(outcomes, Is.Empty);
    }

    // ---- Bug-bundle fix (2026-07-27): batched player-persist methods -------

    [Test]
    public async Task AddPlayerDataBatchAsync_PersistsEveryRow_InOneCall()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);

        await _repository.AddPlayerDataBatchAsync([
            new PlayerData { Id = Guid.NewGuid(), PlayerId = player.Id, Field = "nationality", Value = "France", Source = "wikidata", Confidence = "verified", SyncedAt = DateTime.UtcNow },
            new PlayerData { Id = Guid.NewGuid(), PlayerId = player.Id, Field = "club", Value = "Arsenal", Source = "wikidata", Confidence = "verified", SyncedAt = DateTime.UtcNow },
        ]);

        Assert.That(await _dbContext.PlayerData.CountAsync(d => d.PlayerId == player.Id), Is.EqualTo(2));
    }

    [Test]
    public void AddPlayerDataBatchAsync_EmptyList_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _repository.AddPlayerDataBatchAsync([]));
    }
}

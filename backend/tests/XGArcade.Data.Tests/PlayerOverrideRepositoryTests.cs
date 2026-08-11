using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// S-012 (docs/backlog.md): PlayerOverride CRUD + REQ-203's override-wins
// check. Split out of PlayerStoreRepositoryTests.cs (S-107, docs/backlog.md
// Epic 8, pure refactor — see ADR-0067 for the full split) — test
// bodies/assertions are unchanged from their original PlayerStoreRepositoryTests.cs
// form, this is a structural move only.
// _playerRepository/_playerAttributeRepository below are only used to seed
// fixtures — AddPlayerAsync/AddPlayerAttributeAsync themselves are covered
// directly in PlayerRepositoryTests.cs/PlayerAttributeRepositoryTests.cs.
public class PlayerOverrideRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerOverrideRepository _repository = null!;
    private IPlayerRepository _playerRepository = null!;
    private IPlayerAttributeRepository _playerAttributeRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PlayerOverrideRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _playerAttributeRepository = new PlayerAttributeRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task AddOverrideAsync_ThenGetOverrideAsync_ReturnsIt()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _repository.AddOverrideAsync(new PlayerOverride
        {
            Id = Guid.NewGuid(),
            PlayerId = player.Id,
            Field = "club",
            Value = "Arsenal",
            Reason = "Manual correction",
            LockedByAdminId = Guid.NewGuid(),
            LockedAt = DateTime.UtcNow,
        });

        var found = await _repository.GetOverrideAsync(player.Id, "club");

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Value, Is.EqualTo("Arsenal"));
    }

    [Test]
    public async Task GetOverrideAsync_ReturnsNull_WhenNoOverrideExistsForField()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);

        var found = await _repository.GetOverrideAsync(player.Id, "club");

        Assert.That(found, Is.Null);
    }

    // ---- REQ-203: an override always takes precedence over synced/unverified
    // data ---------------------------------------------------------------

    [Test]
    public async Task REQ203_HasEffectiveAttributeAsync_ReturnsTrue_WhenPlayerAttributeMatches_AndNoOverrideExists()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" });

        var hasIt = await _repository.HasEffectiveAttributeAsync(player.Id, "club", "Arsenal");

        Assert.That(hasIt, Is.True);
    }

    [Test]
    public async Task REQ203_HasEffectiveAttributeAsync_ReturnsFalse_WhenNoOverrideOrAttributeMatches()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" });

        var hasIt = await _repository.HasEffectiveAttributeAsync(player.Id, "club", "Barcelona");

        Assert.That(hasIt, Is.False);
    }

    [Test]
    public async Task REQ203_HasEffectiveAttributeAsync_OverridePresent_WinsOverConflictingCachedPlayerAttribute()
    {
        // The cached (unverified) PlayerAttribute says "Arsenal", but an
        // admin override for the same field says "Barcelona" — the override
        // must always win, per REQ-203/REQ-501.
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" });
        await _repository.AddOverrideAsync(new PlayerOverride
        {
            Id = Guid.NewGuid(),
            PlayerId = player.Id,
            Field = "club",
            Value = "Barcelona",
            Reason = "Manual correction",
            LockedByAdminId = Guid.NewGuid(),
            LockedAt = DateTime.UtcNow,
        });

        var stillMatchesCachedValue = await _repository.HasEffectiveAttributeAsync(player.Id, "club", "Arsenal");
        var matchesOverrideValue = await _repository.HasEffectiveAttributeAsync(player.Id, "club", "Barcelona");

        Assert.That(stillMatchesCachedValue, Is.False, "the stale cached PlayerAttribute must no longer count once an override exists for that field");
        Assert.That(matchesOverrideValue, Is.True);
    }

    // ---- S-012: admin data correction (PlayerOverride CRUD's read/update/delete) ----

    [Test]
    public async Task GetOverrideByIdAsync_ReturnsMatchingOverride()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        var playerOverride = new PlayerOverride
        {
            Id = Guid.NewGuid(), PlayerId = player.Id, Field = "club", Value = "Arsenal",
            Reason = "Manual correction", LockedByAdminId = Guid.NewGuid(), LockedAt = DateTime.UtcNow,
        };
        await _repository.AddOverrideAsync(playerOverride);

        var found = await _repository.GetOverrideByIdAsync(playerOverride.Id);

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Value, Is.EqualTo("Arsenal"));
    }

    [Test]
    public async Task GetOverrideByIdAsync_ReturnsNull_WhenNoOverrideMatches()
    {
        var found = await _repository.GetOverrideByIdAsync(Guid.NewGuid());

        Assert.That(found, Is.Null);
    }

    [Test]
    public async Task UpdateOverrideAsync_PersistsChangedValue()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        var playerOverride = new PlayerOverride
        {
            Id = Guid.NewGuid(), PlayerId = player.Id, Field = "club", Value = "Arsenal",
            Reason = "Manual correction", LockedByAdminId = Guid.NewGuid(), LockedAt = DateTime.UtcNow,
        };
        await _repository.AddOverrideAsync(playerOverride);

        playerOverride.Value = "Barcelona";
        playerOverride.Reason = "Corrected again";
        await _repository.UpdateOverrideAsync(playerOverride);

        var found = await _repository.GetOverrideByIdAsync(playerOverride.Id);
        Assert.That(found!.Value, Is.EqualTo("Barcelona"));
        Assert.That(found.Reason, Is.EqualTo("Corrected again"));
    }

    [Test]
    public async Task DeleteOverrideAsync_RemovesRow_AndReturnsTrue()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        var playerOverride = new PlayerOverride
        {
            Id = Guid.NewGuid(), PlayerId = player.Id, Field = "club", Value = "Arsenal",
            Reason = "Manual correction", LockedByAdminId = Guid.NewGuid(), LockedAt = DateTime.UtcNow,
        };
        await _repository.AddOverrideAsync(playerOverride);

        var deleted = await _repository.DeleteOverrideAsync(playerOverride.Id);

        Assert.That(deleted, Is.True);
        Assert.That(await _repository.GetOverrideByIdAsync(playerOverride.Id), Is.Null);
    }

    [Test]
    public async Task DeleteOverrideAsync_ReturnsFalse_WhenNoOverrideMatches()
    {
        var deleted = await _repository.DeleteOverrideAsync(Guid.NewGuid());

        Assert.That(deleted, Is.False);
    }
}

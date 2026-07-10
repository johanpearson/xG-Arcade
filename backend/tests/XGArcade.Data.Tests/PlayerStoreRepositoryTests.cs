using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// S-003 (docs/backlog.md): no REQ-xxx exists yet for this repository's own
// behavior (it's foundational plumbing other stories build on — S-006
// writes PlayerData, S-012 writes PlayerOverride), so named descriptively
// rather than REQ-prefixed, same pattern as HealthEndpointTests.
public class PlayerStoreRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerStoreRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PlayerStoreRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task GetPlayerByWikidataQidAsync_ReturnsMatchingPlayer()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);

        var found = await _repository.GetPlayerByWikidataQidAsync("Q1519");

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.FullName, Is.EqualTo("Thierry Henry"));
    }

    [Test]
    public async Task GetPlayerByWikidataQidAsync_ReturnsNull_WhenNoPlayerMatches()
    {
        var found = await _repository.GetPlayerByWikidataQidAsync("Q999999");

        Assert.That(found, Is.Null);
    }

    [Test]
    public async Task AddPlayerAttributeAsync_ThenGetPlayerAttributesAsync_ReturnsOnlyMatchingTypeAndValue()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" });
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" });

        var clubAttributes = await _repository.GetPlayerAttributesAsync("club", "Arsenal");

        Assert.That(clubAttributes, Has.Count.EqualTo(1));
        Assert.That(clubAttributes[0].PlayerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task AddOverrideAsync_ThenGetOverrideAsync_ReturnsIt()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);
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
        await _repository.AddPlayerAsync(player);

        var found = await _repository.GetOverrideAsync(player.Id, "club");

        Assert.That(found, Is.Null);
    }

    [Test]
    public async Task AddPlayerAliasAsync_ThenGetPlayerAliasesAsync_ReturnsOnlyThatPlayersAliases()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        var otherPlayer = new Player { Id = Guid.NewGuid(), FullName = "Kaka", WikidataQid = "Q123" };
        await _repository.AddPlayerAsync(player);
        await _repository.AddPlayerAsync(otherPlayer);
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
        await _repository.AddPlayerAsync(player);

        var aliases = await _repository.GetPlayerAliasesAsync(player.Id);

        Assert.That(aliases, Is.Empty);
    }

    // ---- REQ-203: an override always takes precedence over synced/unverified
    // data ---------------------------------------------------------------

    [Test]
    public async Task REQ203_HasEffectiveAttributeAsync_ReturnsTrue_WhenPlayerAttributeMatches_AndNoOverrideExists()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" });

        var hasIt = await _repository.HasEffectiveAttributeAsync(player.Id, "club", "Arsenal");

        Assert.That(hasIt, Is.True);
    }

    [Test]
    public async Task REQ203_HasEffectiveAttributeAsync_ReturnsFalse_WhenNoOverrideOrAttributeMatches()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" });

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
        await _repository.AddPlayerAsync(player);
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" });
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

    [Test]
    public async Task REQ203_HasEffectiveAttributeAsync_OverridePresent_ButValueDiffers_ReturnsFalse_EvenThoughCachedAttributeMatches()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" });
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

        var hasIt = await _repository.HasEffectiveAttributeAsync(player.Id, "club", "Arsenal");

        Assert.That(hasIt, Is.False, "an override for this field replaces the cached value entirely, it isn't merged with it");
    }

    [Test]
    public async Task AddPlayerDataAsync_PersistsRawSourceData()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);

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
}

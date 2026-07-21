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

    // ---- REQ-208: guess-time alias/fuzzy matching's supporting repository
    // methods (GridGameModule.FindMatchAsync/FindFuzzyCandidatesAsync) -------

    [Test]
    public async Task GetPlayersByNormalizedAliasAsync_ReturnsPlayer_WhenNormalizedAliasMatches()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Ricardo Izecson dos Santos Leite", WikidataQid = "Q123" };
        await _repository.AddPlayerAsync(player);
        await _repository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = player.Id, Alias = "Kaka", NormalizedAlias = "kaka" });

        var found = await _repository.GetPlayersByNormalizedAliasAsync("kaka");

        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].Id, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task GetPlayersByNormalizedAliasAsync_ReturnsEmpty_WhenNoAliasMatches()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);
        await _repository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = player.Id, Alias = "Titi", NormalizedAlias = "titi" });

        var found = await _repository.GetPlayersByNormalizedAliasAsync("kaka");

        Assert.That(found, Is.Empty);
    }

    [Test]
    public async Task GetPlayersWithEitherAttributeAsync_ReturnsPlayersSatisfyingEitherAttribute_DistinctAndNoOthers()
    {
        var satisfiesFirst = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA" };
        var satisfiesSecond = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QB" };
        var satisfiesBoth = new Player { Id = Guid.NewGuid(), FullName = "Player C", WikidataQid = "QC" };
        var satisfiesNeither = new Player { Id = Guid.NewGuid(), FullName = "Player D", WikidataQid = "QD" };
        foreach (var p in new[] { satisfiesFirst, satisfiesSecond, satisfiesBoth, satisfiesNeither })
            await _repository.AddPlayerAsync(p);

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
        await _repository.AddPlayerAsync(player);
        await _repository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "England" });

        var found = await _repository.GetPlayersWithEitherAttributeAsync("nationality", "France", "club", "Arsenal");

        Assert.That(found, Is.Empty);
    }

    [Test]
    public async Task GetPlayerAliasesByPlayerIdsAsync_ReturnsOnlyRequestedPlayersAliases_GroupedByPlayerId()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Ricardo Izecson dos Santos Leite", WikidataQid = "Q123" };
        var otherPlayer = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        var uninvolvedPlayer = new Player { Id = Guid.NewGuid(), FullName = "Someone Else", WikidataQid = "Q999" };
        await _repository.AddPlayerAsync(player);
        await _repository.AddPlayerAsync(otherPlayer);
        await _repository.AddPlayerAsync(uninvolvedPlayer);
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

    // ---- REQ-209: disambiguation-prompt candidate building -----------------

    [Test]
    public async Task GetPlayerAttributesByPlayerIdsAsync_ReturnsOnlyRequestedPlayersAttributes_GroupedByPlayerId()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "John Smith", WikidataQid = "Q1" };
        var otherPlayer = new Player { Id = Guid.NewGuid(), FullName = "John Smith", WikidataQid = "Q2" };
        var uninvolvedPlayer = new Player { Id = Guid.NewGuid(), FullName = "Someone Else", WikidataQid = "Q3" };
        await _repository.AddPlayerAsync(player);
        await _repository.AddPlayerAsync(otherPlayer);
        await _repository.AddPlayerAsync(uninvolvedPlayer);
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
        await _repository.AddPlayerAsync(player);

        var attributesByPlayerId = await _repository.GetPlayerAttributesByPlayerIdsAsync([player.Id]);

        Assert.That(attributesByPlayerId.Keys, Is.Empty);
    }

    [Test]
    public async Task GetPlayerAttributesByPlayerIdsAsync_EmptyIdList_ReturnsEmptyDictionary()
    {
        var attributesByPlayerId = await _repository.GetPlayerAttributesByPlayerIdsAsync([]);

        Assert.That(attributesByPlayerId, Is.Empty);
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

    // ---- S-012: admin data correction (GetPlayerByIdAsync, unverified
    // PlayerData listing, PlayerOverride CRUD's read/update/delete) --------

    [Test]
    public async Task GetPlayerByIdAsync_ReturnsMatchingPlayer()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);

        var found = await _repository.GetPlayerByIdAsync(player.Id);

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.FullName, Is.EqualTo("Thierry Henry"));
    }

    [Test]
    public async Task GetPlayerByIdAsync_ReturnsNull_WhenNoPlayerMatches()
    {
        var found = await _repository.GetPlayerByIdAsync(Guid.NewGuid());

        Assert.That(found, Is.Null);
    }

    [Test]
    public async Task GetUnverifiedPlayerDataAsync_ReturnsOnlyUnverifiedRows()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);
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

    [Test]
    public async Task GetOverrideByIdAsync_ReturnsMatchingOverride()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);
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
        await _repository.AddPlayerAsync(player);
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
        await _repository.AddPlayerAsync(player);
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

    // ---- GetPlayersMissingPhotoAsync / UpdatePlayerPhotosAsync -------------
    // REQ-214 backfill (S-045): PlayerPhotoBackfillService's read/write pair.

    [Test]
    public async Task REQ214_GetPlayersMissingPhotoAsync_ReturnsOnlyPlayersWithQidAndNoPhoto()
    {
        var missingPhoto = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        var alreadyHasPhoto = new Player { Id = Guid.NewGuid(), FullName = "Didier Drogba", WikidataQid = "Q42233", PhotoUrl = "https://example.com/drogba.jpg" };
        var noQid = new Player { Id = Guid.NewGuid(), FullName = "No QID Player" };
        await _repository.AddPlayerAsync(missingPhoto);
        await _repository.AddPlayerAsync(alreadyHasPhoto);
        await _repository.AddPlayerAsync(noQid);

        var result = await _repository.GetPlayersMissingPhotoAsync([], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { missingPhoto.Id }));
    }

    [Test]
    public async Task REQ214_GetPlayersMissingPhotoAsync_RespectsBatchSize()
    {
        for (var i = 0; i < 5; i++)
            await _repository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = $"Player {i}", WikidataQid = $"Q{i}" });

        var result = await _repository.GetPlayersMissingPhotoAsync([], batchSize: 3);

        Assert.That(result, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task REQ214_GetPlayersMissingPhotoAsync_ExcludesGivenPlayerIds()
    {
        var first = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA" };
        var second = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QB" };
        await _repository.AddPlayerAsync(first);
        await _repository.AddPlayerAsync(second);

        var result = await _repository.GetPlayersMissingPhotoAsync([first.Id], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { second.Id }));
    }

    [Test]
    public async Task REQ214_GetPlayersMissingPhotoAsync_NoMissingPhotoPlayers_ReturnsEmpty()
    {
        var result = await _repository.GetPlayersMissingPhotoAsync([], batchSize: 200);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task REQ214_UpdatePlayerPhotosAsync_SetsPhotoUrl_ForEveryGivenPlayer()
    {
        var first = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA" };
        var second = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QB" };
        await _repository.AddPlayerAsync(first);
        await _repository.AddPlayerAsync(second);

        await _repository.UpdatePlayerPhotosAsync(new Dictionary<Guid, string>
        {
            [first.Id] = "https://example.com/a.jpg",
            [second.Id] = "https://example.com/b.jpg",
        });

        Assert.That((await _repository.GetPlayerByIdAsync(first.Id))!.PhotoUrl, Is.EqualTo("https://example.com/a.jpg"));
        Assert.That((await _repository.GetPlayerByIdAsync(second.Id))!.PhotoUrl, Is.EqualTo("https://example.com/b.jpg"));
    }

    [Test]
    public async Task REQ214_UpdatePlayerPhotosAsync_EmptyDictionary_DoesNothing()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);

        await _repository.UpdatePlayerPhotosAsync(new Dictionary<Guid, string>());

        Assert.That((await _repository.GetPlayerByIdAsync(player.Id))!.PhotoUrl, Is.Null);
    }

    [Test]
    public async Task REQ214_UpdatePlayerPhotosAsync_UnknownPlayerId_IsSilentlySkipped()
    {
        // Best-effort backfill of already-cached data, not a
        // correctness-critical write — a player deleted between the read
        // and this write (e.g. by purge-player-pool) must not fail the
        // whole batch.
        Assert.DoesNotThrowAsync(() => _repository.UpdatePlayerPhotosAsync(new Dictionary<Guid, string>
        {
            [Guid.NewGuid()] = "https://example.com/unknown.jpg",
        }));
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
        await _repository.AddPlayerAsync(player);
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
        await _repository.AddPlayerAsync(player);
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
        await _repository.AddPlayerAsync(player);
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
        await _repository.AddPlayerAsync(player);
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
}

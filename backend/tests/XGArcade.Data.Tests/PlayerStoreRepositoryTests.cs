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

    // ---- GetPlayersMissingPositionOrBirthYearAsync / UpdatePlayerPositionsAndBirthYearsAsync ----
    // REQ-1207 backfill (bug-bundle fix, 2026-08-02): PlayerPositionBirthYearBackfillService's
    // read/write pair — mirrors GetPlayersMissingPhotoAsync/UpdatePlayerPhotosAsync's
    // own coverage above, adapted for the two-field "either is missing" shape.

    [Test]
    public async Task REQ1207_GetPlayersMissingPositionOrBirthYearAsync_ReturnsPlayersMissingEitherField()
    {
        var missingBoth = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        var missingPositionOnly = new Player { Id = Guid.NewGuid(), FullName = "Didier Drogba", WikidataQid = "Q42233", BirthYear = 1978 };
        var missingBirthYearOnly = new Player { Id = Guid.NewGuid(), FullName = "Kaká", WikidataQid = "Q11571", Position = "midfielder" };
        var hasBoth = new Player { Id = Guid.NewGuid(), FullName = "Pelé", WikidataQid = "Q80956", Position = "forward", BirthYear = 1940 };
        var noQid = new Player { Id = Guid.NewGuid(), FullName = "No QID Player" };
        await _repository.AddPlayerAsync(missingBoth);
        await _repository.AddPlayerAsync(missingPositionOnly);
        await _repository.AddPlayerAsync(missingBirthYearOnly);
        await _repository.AddPlayerAsync(hasBoth);
        await _repository.AddPlayerAsync(noQid);

        var result = await _repository.GetPlayersMissingPositionOrBirthYearAsync([], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { missingBoth.Id, missingPositionOnly.Id, missingBirthYearOnly.Id }));
    }

    [Test]
    public async Task REQ1207_GetPlayersMissingPositionOrBirthYearAsync_IncludesPlayersWithRawWikidataUriPosition()
    {
        // Bug fix (2026-08-10, bug-bundle): rows created before the
        // 2026-08-02 WikidataClient fix hold the raw P413 entity URI as
        // Position, not a resolved label. Position is NOT NULL on these
        // rows, so they must still surface as backfill candidates or they're
        // permanently skipped.
        var rawUriPosition = new Player
        {
            Id = Guid.NewGuid(),
            FullName = "Raw URI Player",
            WikidataQid = "Q8025128",
            Position = "http://www.wikidata.org/entity/Q8025128",
            BirthYear = 1990,
        };
        var resolvedPosition = new Player
        {
            Id = Guid.NewGuid(),
            FullName = "Resolved Position Player",
            WikidataQid = "Q42233",
            Position = "midfielder",
            BirthYear = 1978,
        };
        await _repository.AddPlayerAsync(rawUriPosition);
        await _repository.AddPlayerAsync(resolvedPosition);

        var result = await _repository.GetPlayersMissingPositionOrBirthYearAsync([], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { rawUriPosition.Id }));
    }

    [Test]
    public async Task REQ1207_GetPlayersMissingPositionOrBirthYearAsync_RespectsBatchSize()
    {
        for (var i = 0; i < 5; i++)
            await _repository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = $"Player {i}", WikidataQid = $"Q{i}" });

        var result = await _repository.GetPlayersMissingPositionOrBirthYearAsync([], batchSize: 3);

        Assert.That(result, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task REQ1207_GetPlayersMissingPositionOrBirthYearAsync_ExcludesGivenPlayerIds()
    {
        var first = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA" };
        var second = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QB" };
        await _repository.AddPlayerAsync(first);
        await _repository.AddPlayerAsync(second);

        var result = await _repository.GetPlayersMissingPositionOrBirthYearAsync([first.Id], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { second.Id }));
    }

    [Test]
    public async Task REQ1207_GetPlayersMissingPositionOrBirthYearAsync_NoMissingFieldPlayers_ReturnsEmpty()
    {
        var result = await _repository.GetPlayersMissingPositionOrBirthYearAsync([], batchSize: 200);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task REQ1207_UpdatePlayerPositionsAndBirthYearsAsync_SetsBothFields_ForEveryGivenPlayer()
    {
        var first = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA" };
        var second = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QB" };
        await _repository.AddPlayerAsync(first);
        await _repository.AddPlayerAsync(second);

        await _repository.UpdatePlayerPositionsAndBirthYearsAsync(new Dictionary<Guid, PlayerPositionBirthYearUpdate>
        {
            [first.Id] = new PlayerPositionBirthYearUpdate("forward", 1990),
            [second.Id] = new PlayerPositionBirthYearUpdate("goalkeeper", 1985),
        });

        var reloadedFirst = await _repository.GetPlayerByIdAsync(first.Id);
        Assert.That(reloadedFirst!.Position, Is.EqualTo("forward"));
        Assert.That(reloadedFirst.BirthYear, Is.EqualTo(1990));
        var reloadedSecond = await _repository.GetPlayerByIdAsync(second.Id);
        Assert.That(reloadedSecond!.Position, Is.EqualTo("goalkeeper"));
        Assert.That(reloadedSecond.BirthYear, Is.EqualTo(1985));
    }

    [Test]
    public async Task REQ1207_UpdatePlayerPositionsAndBirthYearsAsync_NullFieldOnUpdate_LeavesThatFieldUntouched()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA", BirthYear = 1990 };
        await _repository.AddPlayerAsync(player);

        // Position resolved this run, BirthYear didn't (null means "no
        // update," never "clear the existing value") — the already-set
        // BirthYear must survive unchanged.
        await _repository.UpdatePlayerPositionsAndBirthYearsAsync(new Dictionary<Guid, PlayerPositionBirthYearUpdate>
        {
            [player.Id] = new PlayerPositionBirthYearUpdate("forward", null),
        });

        var reloaded = await _repository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.EqualTo("forward"));
        Assert.That(reloaded.BirthYear, Is.EqualTo(1990));
    }

    [Test]
    public async Task REQ1207_UpdatePlayerPositionsAndBirthYearsAsync_AlreadySetField_IsNeverOverwritten()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA", Position = "defender" };
        await _repository.AddPlayerAsync(player);

        await _repository.UpdatePlayerPositionsAndBirthYearsAsync(new Dictionary<Guid, PlayerPositionBirthYearUpdate>
        {
            [player.Id] = new PlayerPositionBirthYearUpdate("forward", 1990),
        });

        var reloaded = await _repository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.EqualTo("defender"), "REQ-1207's set-once contract must hold for this backfill too");
        Assert.That(reloaded.BirthYear, Is.EqualTo(1990));
    }

    [Test]
    public async Task REQ1207_UpdatePlayerPositionsAndBirthYearsAsync_RawWikidataUriPosition_IsOverwrittenWithResolvedLabel()
    {
        // Bug fix (2026-08-10, bug-bundle): the raw-URI shape is the one
        // deliberate exception to the "already-set field is never
        // overwritten" rule above — otherwise widening the read-side
        // candidate query would be a no-op.
        var player = new Player
        {
            Id = Guid.NewGuid(),
            FullName = "Raw URI Player",
            WikidataQid = "Q8025128",
            Position = "http://www.wikidata.org/entity/Q8025128",
        };
        await _repository.AddPlayerAsync(player);

        await _repository.UpdatePlayerPositionsAndBirthYearsAsync(new Dictionary<Guid, PlayerPositionBirthYearUpdate>
        {
            [player.Id] = new PlayerPositionBirthYearUpdate("midfielder", 1990),
        });

        var reloaded = await _repository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.EqualTo("midfielder"));
        Assert.That(reloaded.BirthYear, Is.EqualTo(1990));
    }

    [Test]
    public async Task REQ1207_UpdatePlayerPositionsAndBirthYearsAsync_EmptyDictionary_DoesNothing()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);

        await _repository.UpdatePlayerPositionsAndBirthYearsAsync(new Dictionary<Guid, PlayerPositionBirthYearUpdate>());

        var reloaded = await _repository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.Null);
        Assert.That(reloaded.BirthYear, Is.Null);
    }

    [Test]
    public async Task REQ1207_UpdatePlayerPositionsAndBirthYearsAsync_UnknownPlayerId_IsSilentlySkipped()
    {
        // Best-effort backfill of already-cached data, not a
        // correctness-critical write — a player deleted between the read
        // and this write (e.g. by purge-player-pool) must not fail the
        // whole batch.
        Assert.DoesNotThrowAsync(() => _repository.UpdatePlayerPositionsAndBirthYearsAsync(new Dictionary<Guid, PlayerPositionBirthYearUpdate>
        {
            [Guid.NewGuid()] = new PlayerPositionBirthYearUpdate("forward", 1990),
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

    // ---- ADR-0042/S-079: PlayerCareerStint (GetCareerStintsAsync/AddCareerStintsAsync) ----

    [Test]
    public async Task AddCareerStintsAsync_ThenGetCareerStintsAsync_ReturnsAddedStints()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);

        await _repository.AddCareerStintsAsync(player.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254 },
        ]);

        var stints = await _repository.GetCareerStintsAsync(player.Id);

        Assert.That(stints, Has.Count.EqualTo(1));
        Assert.That(stints[0].ClubName, Is.EqualTo("Arsenal"));
        Assert.That(stints[0].SequenceOrder, Is.EqualTo(0));
    }

    [Test]
    public async Task AddCareerStintsAsync_ResequencesExistingStints_WhenNewStintIsChronologicallyEarlier()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);
        await _repository.AddCareerStintsAsync(player.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Barcelona", StartYear = 2010, EndYear = 2015 },
        ]);

        await _repository.AddCareerStintsAsync(player.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254 },
        ]);

        var stints = (await _repository.GetCareerStintsAsync(player.Id)).OrderBy(s => s.SequenceOrder).ToList();

        Assert.That(stints, Has.Count.EqualTo(2));
        Assert.That(stints[0].ClubName, Is.EqualTo("Arsenal"));
        Assert.That(stints[0].SequenceOrder, Is.EqualTo(0));
        Assert.That(stints[1].ClubName, Is.EqualTo("Barcelona"));
        Assert.That(stints[1].SequenceOrder, Is.EqualTo(1),
            "the pre-existing Barcelona row must be re-sequenced to make room for the chronologically earlier Arsenal stint");
    }

    [Test]
    public async Task AddCareerStintsAsync_OngoingStint_SortsLastAmongStintsSharingTheSameStartYear()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);

        await _repository.AddCareerStintsAsync(player.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Loan Club", StartYear = 2020, EndYear = 2021 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Parent Club", StartYear = 2020, EndYear = null },
        ]);

        var stints = (await _repository.GetCareerStintsAsync(player.Id)).OrderBy(s => s.SequenceOrder).ToList();

        Assert.That(stints[0].ClubName, Is.EqualTo("Loan Club"));
        Assert.That(stints[1].ClubName, Is.EqualTo("Parent Club"), "an ongoing (null EndYear) stint sorts last among stints sharing the same StartYear");
    }

    [Test]
    public void AddCareerStintsAsync_EmptyNewStintsList_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _repository.AddCareerStintsAsync(Guid.NewGuid(), []));
    }

    [Test]
    public async Task GetCareerStintsAsync_ReturnsEmpty_WhenPlayerHasNoStints()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);

        var stints = await _repository.GetCareerStintsAsync(player.Id);

        Assert.That(stints, Is.Empty);
    }

    // ---- Bug-bundle fix (2026-07-27): batched player-persist methods -------
    // (WikidataLookupService.PersistMatchesAsync/PersistCareerStintsAsync's
    // new O(1)-round-trips shape.)

    [Test]
    public async Task GetOrCreatePlayersByWikidataQidAsync_UnknownQids_CreatesOnePlayerPerRequest()
    {
        var requests = new List<PlayerCreationRequest>
        {
            new("Q1519", "Thierry Henry", null),
            new("Q182804", "Nicolas Anelka", "https://example.com/anelka.jpg"),
        };

        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync(requests);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result["Q1519"].FullName, Is.EqualTo("Thierry Henry"));
        Assert.That(result["Q1519"].PhotoUrl, Is.Null);
        Assert.That(result["Q182804"].FullName, Is.EqualTo("Nicolas Anelka"));
        Assert.That(result["Q182804"].PhotoUrl, Is.EqualTo("https://example.com/anelka.jpg"));
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task GetOrCreatePlayersByWikidataQidAsync_ExistingQid_ReusesExistingPlayer_NeverInserts()
    {
        var existing = await _repository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" });

        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync([new PlayerCreationRequest("Q1519", "Thierry Henry", null)]);

        Assert.That(result["Q1519"].Id, Is.EqualTo(existing.Id));
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task GetOrCreatePlayersByWikidataQidAsync_MixOfExistingAndNewQids_HandlesBothInOneCall()
    {
        var existing = await _repository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" });

        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync([
            new PlayerCreationRequest("Q1519", "Thierry Henry", null),
            new PlayerCreationRequest("Q182804", "Nicolas Anelka", null),
        ]);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result["Q1519"].Id, Is.EqualTo(existing.Id));
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task GetOrCreatePlayersByWikidataQidAsync_EmptyRequestList_ReturnsEmptyDictionary_DoesNotThrow()
    {
        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync([]);

        Assert.That(result, Is.Empty);
    }

    // ---- REQ-1207/S-082: Position/BirthYear, set once at Player creation --

    [Test]
    public async Task REQ1207_GetOrCreatePlayersByWikidataQidAsync_UnknownQid_SetsPositionAndBirthYearFromTheRequest()
    {
        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync(
            [new PlayerCreationRequest("Q1519", "Thierry Henry", null, "forward", 1977)]);

        Assert.That(result["Q1519"].Position, Is.EqualTo("forward"));
        Assert.That(result["Q1519"].BirthYear, Is.EqualTo(1977));
    }

    [Test]
    public async Task REQ1207_GetOrCreatePlayersByWikidataQidAsync_UnknownQid_NoPositionOrBirthYearGiven_BothAreNull()
    {
        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync(
            [new PlayerCreationRequest("Q1519", "Thierry Henry", null)]);

        Assert.That(result["Q1519"].Position, Is.Null);
        Assert.That(result["Q1519"].BirthYear, Is.Null);
    }

    [Test]
    public async Task REQ1207_GetOrCreatePlayersByWikidataQidAsync_ExistingQid_LeavesItsPositionAndBirthYearCompletelyUntouched_EvenWhenTheNewRequestDisagrees()
    {
        // Set-once contract, direct at the repository level (this method
        // never touches an existing Player row at all — see its own
        // "if (result.ContainsKey(...)) continue;" comment) — a second call
        // for the same QID carrying different Position/BirthYear values must
        // have zero effect on the already-persisted row.
        var existing = await _repository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519", Position = "forward", BirthYear = 1977 });

        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync(
            [new PlayerCreationRequest("Q1519", "Thierry Henry", null, "midfielder", 1980)]);

        Assert.That(result["Q1519"].Id, Is.EqualTo(existing.Id));
        Assert.That(result["Q1519"].Position, Is.EqualTo("forward"), "the new request's 'midfielder' must never overwrite the existing row");
        Assert.That(result["Q1519"].BirthYear, Is.EqualTo(1977), "the new request's 1980 must never overwrite the existing row");
    }

    [Test]
    public async Task REQ1207_GetOrCreatePlayersByWikidataQidAsync_ExistingQidWithNullPositionAndBirthYear_LaterRequestWithRealValues_LeavesThemNull()
    {
        // The set-once rule applies regardless of whether the existing row's
        // CURRENT value is null or already set (REQ-1207's own text) — not
        // just the "overwriting a real value" case above.
        var existing = await _repository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" });

        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync(
            [new PlayerCreationRequest("Q1519", "Thierry Henry", null, "forward", 1977)]);

        Assert.That(result["Q1519"].Id, Is.EqualTo(existing.Id));
        Assert.That(result["Q1519"].Position, Is.Null, "a null already on the existing row is never backfilled by a later request");
        Assert.That(result["Q1519"].BirthYear, Is.Null, "a null already on the existing row is never backfilled by a later request");
    }

    [Test]
    public async Task AddPlayerDataBatchAsync_PersistsEveryRow_InOneCall()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);

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

    [Test]
    public async Task AddPlayerAttributesBatchAsync_PersistsEveryRow_InOneCall()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);

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

    [Test]
    public async Task AddPlayerAliasesBatchAsync_PersistsEveryRow_InOneCall()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Ricardo Izecson dos Santos Leite", WikidataQid = "Qkaka" };
        await _repository.AddPlayerAsync(player);

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

    [Test]
    public async Task GetCareerStintsByPlayerIdsAsync_ReturnsOnlyRequestedPlayersStints_GroupedByPlayerId()
    {
        var playerA = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QplayerA" };
        var playerB = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QplayerB" };
        await _repository.AddPlayerAsync(playerA);
        await _repository.AddPlayerAsync(playerB);
        await _repository.AddCareerStintsAsync(playerA.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 },
        ]);
        await _repository.AddCareerStintsAsync(playerB.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Barcelona", StartYear = 2004, EndYear = 2014 },
        ]);

        var result = await _repository.GetCareerStintsByPlayerIdsAsync([playerA.Id]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[playerA.Id], Has.Count.EqualTo(1));
        Assert.That(result[playerA.Id][0].ClubName, Is.EqualTo("Arsenal"));
        Assert.That(result.ContainsKey(playerB.Id), Is.False, "a player not requested must be absent from the result");
    }

    [Test]
    public async Task GetCareerStintsByPlayerIdsAsync_EmptyIdList_ReturnsEmptyDictionary()
    {
        var result = await _repository.GetCareerStintsByPlayerIdsAsync([]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task AddCareerStintsBatchAsync_PersistsStintsForMultiplePlayers_InOneCall()
    {
        var playerA = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QplayerA" };
        var playerB = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QplayerB" };
        await _repository.AddPlayerAsync(playerA);
        await _repository.AddPlayerAsync(playerB);

        await _repository.AddCareerStintsBatchAsync(new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>
        {
            [playerA.Id] = [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }],
            [playerB.Id] = [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Barcelona", StartYear = 2004, EndYear = 2014 }],
        });

        var stintsA = await _repository.GetCareerStintsAsync(playerA.Id);
        var stintsB = await _repository.GetCareerStintsAsync(playerB.Id);
        Assert.That(stintsA, Has.Count.EqualTo(1));
        Assert.That(stintsA[0].SequenceOrder, Is.EqualTo(0));
        Assert.That(stintsB, Has.Count.EqualTo(1));
        Assert.That(stintsB[0].SequenceOrder, Is.EqualTo(0));
    }

    [Test]
    public async Task AddCareerStintsBatchAsync_ResequencesEachPlayersExistingStints_Independently()
    {
        // Same re-sequencing rule as AddCareerStintsAsync's own test above,
        // proven here across two DIFFERENT players in the SAME batch call —
        // one player's chronologically-earlier new stint must never affect
        // another player's own SequenceOrder.
        var playerA = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QplayerA" };
        var playerB = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QplayerB" };
        await _repository.AddPlayerAsync(playerA);
        await _repository.AddPlayerAsync(playerB);
        await _repository.AddCareerStintsAsync(playerA.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Barcelona", StartYear = 2010, EndYear = 2015 },
        ]);
        await _repository.AddCareerStintsAsync(playerB.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Chelsea", StartYear = 2005, EndYear = 2010 },
        ]);

        await _repository.AddCareerStintsBatchAsync(new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>
        {
            // Chronologically earlier than playerA's existing Barcelona stint.
            [playerA.Id] = [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }],
            // playerB gets no new stints in this batch call.
            [playerB.Id] = [],
        });

        var stintsA = (await _repository.GetCareerStintsAsync(playerA.Id)).OrderBy(s => s.SequenceOrder).ToList();
        var stintsB = await _repository.GetCareerStintsAsync(playerB.Id);
        Assert.That(stintsA, Has.Count.EqualTo(2));
        Assert.That(stintsA[0].ClubName, Is.EqualTo("Arsenal"));
        Assert.That(stintsA[1].ClubName, Is.EqualTo("Barcelona"), "playerA's pre-existing stint must be re-sequenced");
        Assert.That(stintsB, Has.Count.EqualTo(1));
        Assert.That(stintsB[0].SequenceOrder, Is.EqualTo(0), "playerB's own stint must be untouched by playerA's re-sequencing");
    }

    [Test]
    public void AddCareerStintsBatchAsync_EmptyDictionary_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _repository.AddCareerStintsBatchAsync(new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>()));
    }

    [Test]
    public void AddCareerStintsBatchAsync_EveryEntryHasEmptyNewStintsList_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _repository.AddCareerStintsBatchAsync(new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>
        {
            [Guid.NewGuid()] = [],
        }));
    }

    // ---- audit-club-gaps diagnostic (GetUnseededClubCandidatesAsync) -------

    [Test]
    public async Task GetUnseededClubCandidatesAsync_ExcludesClubsAlreadyInClubDefinition()
    {
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Arsenal", WikidataQid = "Q9617" });
        await _dbContext.SaveChangesAsync();

        var seededClubPlayer = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        var unseededClubPlayer = new Player { Id = Guid.NewGuid(), FullName = "Someone Else", WikidataQid = "Q999" };
        await _repository.AddPlayerAsync(seededClubPlayer);
        await _repository.AddPlayerAsync(unseededClubPlayer);
        await _repository.AddCareerStintsAsync(seededClubPlayer.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = seededClubPlayer.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);
        await _repository.AddCareerStintsAsync(unseededClubPlayer.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = unseededClubPlayer.Id, ClubName = "Napoli", StartYear = 2010, EndYear = 2015 }]);

        var candidates = await _repository.GetUnseededClubCandidatesAsync(30);

        Assert.That(candidates.Select(c => c.ClubName), Is.EquivalentTo(new[] { "Napoli" }),
            "Arsenal already has a matching ClubDefinition and must not be surfaced as a gap");
    }

    [Test]
    public async Task GetUnseededClubCandidatesAsync_CountsDistinctPlayers_NotStints()
    {
        var playerWithTwoStints = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "Q1" };
        await _repository.AddPlayerAsync(playerWithTwoStints);
        // Two separate stints at the same unseeded club (e.g. a loan then a
        // later permanent return) — must still count as ONE distinct player.
        await _repository.AddCareerStintsAsync(playerWithTwoStints.Id,
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
        await _repository.AddPlayerAsync(playerA);
        await _repository.AddPlayerAsync(playerB);
        await _repository.AddPlayerAsync(playerC);

        // "Popular Unseeded Club": 2 distinct players. "Rare Unseeded Club": 1.
        await _repository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Popular Unseeded Club", StartYear = 2000, EndYear = 2005 }]);
        await _repository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Popular Unseeded Club", StartYear = 2001, EndYear = 2006 }]);
        await _repository.AddCareerStintsAsync(playerC.Id,
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
            await _repository.AddPlayerAsync(player);
            await _repository.AddCareerStintsAsync(player.Id,
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
        await _repository.AddPlayerAsync(player);
        await _repository.AddCareerStintsAsync(player.Id,
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

    // ---- REQ-1201 perf fix (GetCareerStintCandidatePlayerIdsAsync) --------
    // Same "narrow read" testing shape as the GetUnseededClubCandidatesAsync
    // tests above, but proving the narrower two-condition ("enough rows" AND
    // "any stint at a seeded club") superset filter this hot path relies on,
    // rather than the diagnostic method's own case-insensitive club-name
    // grouping.

    [Test]
    public async Task GetCareerStintCandidatePlayerIdsAsync_ExcludesPlayersWithFewerThanMinStintCount()
    {
        var seededClubNames = new HashSet<string> { "Seeded FC" };
        var tooFew = new Player { Id = Guid.NewGuid(), FullName = "Too Few", WikidataQid = "Q1" };
        await _repository.AddPlayerAsync(tooFew);
        await _repository.AddCareerStintsAsync(tooFew.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = tooFew.Id, ClubName = "Seeded FC", StartYear = 2010, EndYear = 2013 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = tooFew.Id, ClubName = "Other FC", StartYear = 2013, EndYear = null },
        ]);

        var candidateIds = await _repository.GetCareerStintCandidatePlayerIdsAsync(seededClubNames, minStintCount: 3);

        Assert.That(candidateIds, Does.Not.Contain(tooFew.Id));
    }

    [Test]
    public async Task GetCareerStintCandidatePlayerIdsAsync_ExcludesPlayersWithNoStintAtSeededClub()
    {
        var seededClubNames = new HashSet<string> { "Seeded FC" };
        var noSeededClub = new Player { Id = Guid.NewGuid(), FullName = "No Seeded Club", WikidataQid = "Q1" };
        await _repository.AddPlayerAsync(noSeededClub);
        await _repository.AddCareerStintsAsync(noSeededClub.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = noSeededClub.Id, ClubName = "Unseeded A", StartYear = 2010, EndYear = 2013 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = noSeededClub.Id, ClubName = "Unseeded B", StartYear = 2013, EndYear = 2016 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = noSeededClub.Id, ClubName = "Unseeded C", StartYear = 2016, EndYear = null },
        ]);

        var candidateIds = await _repository.GetCareerStintCandidatePlayerIdsAsync(seededClubNames, minStintCount: 3);

        Assert.That(candidateIds, Does.Not.Contain(noSeededClub.Id));
    }

    [Test]
    public async Task GetCareerStintCandidatePlayerIdsAsync_IncludesPlayerMeetingBothConditions()
    {
        var seededClubNames = new HashSet<string> { "Seeded FC" };
        var eligible = new Player { Id = Guid.NewGuid(), FullName = "Eligible", WikidataQid = "Q1" };
        await _repository.AddPlayerAsync(eligible);
        await _repository.AddCareerStintsAsync(eligible.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = eligible.Id, ClubName = "Seeded FC", StartYear = 2010, EndYear = 2013 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = eligible.Id, ClubName = "Unseeded A", StartYear = 2013, EndYear = 2016 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = eligible.Id, ClubName = "Unseeded B", StartYear = 2016, EndYear = null },
        ]);

        var candidateIds = await _repository.GetCareerStintCandidatePlayerIdsAsync(seededClubNames, minStintCount: 3);

        Assert.That(candidateIds, Does.Contain(eligible.Id));
    }

    [Test]
    public async Task GetCareerStintCandidatePlayerIdsAsync_CaseSensitiveMatch_ExcludesPlayerWhoseOnlySeededClubStintDiffersOnlyInCase()
    {
        // Deliberately diverges from GetUnseededClubCandidatesAsync's own
        // OrdinalIgnoreCase precedent: this method must match IsEligible's
        // exact seededClubNames.Contains(s.ClubName) behavior, so a stint at
        // a club differing only in case from a seeded name must NOT count.
        var seededClubNames = new HashSet<string> { "Seeded FC" };
        var caseMismatch = new Player { Id = Guid.NewGuid(), FullName = "Case Mismatch", WikidataQid = "Q1" };
        await _repository.AddPlayerAsync(caseMismatch);
        await _repository.AddCareerStintsAsync(caseMismatch.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = caseMismatch.Id, ClubName = "SEEDED FC", StartYear = 2010, EndYear = 2013 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = caseMismatch.Id, ClubName = "Unseeded A", StartYear = 2013, EndYear = 2016 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = caseMismatch.Id, ClubName = "Unseeded B", StartYear = 2016, EndYear = null },
        ]);

        var candidateIds = await _repository.GetCareerStintCandidatePlayerIdsAsync(seededClubNames, minStintCount: 3);

        Assert.That(candidateIds, Does.Not.Contain(caseMismatch.Id),
            "a club name differing only in case from a seeded name must NOT count, matching IsEligible's own exact-match behavior");
    }

    [Test]
    public async Task GetCareerStintCandidatePlayerIdsAsync_EmptyTable_ReturnsEmpty()
    {
        var candidateIds = await _repository.GetCareerStintCandidatePlayerIdsAsync(new HashSet<string> { "Seeded FC" }, minStintCount: 3);

        Assert.That(candidateIds, Is.Empty);
    }
}

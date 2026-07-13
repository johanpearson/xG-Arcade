using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGGrid.Tests;

// REQ-110 — docs/requirements-document.md. Same real-InMemory-repositories-
// plus-FakeWikidataLookupService pattern as GridGameModuleTests.cs (see
// that file's own doc comment for why: docs/coding-guidelines.md's
// "don't over-mock").
public class PlayerCacheWarmingServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private ICategoryValueRepository _categoryValueRepository = null!;
    private IPlayerStoreRepository _playerStoreRepository = null!;
    private FakeWikidataLookupService _wikidataLookupService = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _playerStoreRepository = new PlayerStoreRepository(_dbContext);
        _wikidataLookupService = new FakeWikidataLookupService(_playerStoreRepository);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerCacheWarmingService BuildService(int minValidAnswers) =>
        new(_categoryValueRepository, _playerStoreRepository, _wikidataLookupService,
            new GridGenerationOptions { MinValidAnswers = minValidAnswers },
            NullLogger<PlayerCacheWarmingService>.Instance);

    private CountryDefinition SeedCountry(string name) =>
        Seed(new CountryDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = $"Qcountry-{name}" }, _dbContext.CountryDefinitions);

    private ClubDefinition SeedClub(string name) =>
        Seed(new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = $"Qclub-{name}" }, _dbContext.ClubDefinitions);

    private T Seed<T>(T entity, DbSet<T> set) where T : class
    {
        set.Add(entity);
        _dbContext.SaveChanges();
        return entity;
    }

    private void SeedCachedMatches(string firstType, string firstValue, string secondType, string secondValue, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var player = new Player { Id = Guid.NewGuid(), FullName = $"{firstValue}-{secondValue}-Player{i}", WikidataQid = $"Qplayer-{firstValue}-{secondValue}-{i}" };
            _dbContext.Players.Add(player);
            _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = firstType, AttributeValue = firstValue });
            _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = secondType, AttributeValue = secondValue });
        }
        _dbContext.SaveChanges();
    }

    [Test]
    public async Task REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive()
    {
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 2 countries x 2 clubs = 4 Country x Club pairs, plus 1 unique
        // Club x Club pair (Arsenal x Barcelona) = 5 total.
        Assert.That(result.TotalPairs, Is.EqualTo(5));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(5));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Barcelona"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Barcelona"), Is.EqualTo(1));
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the pair could have been queried as (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — summing both possible key orders instead
        // of asserting a specific one, same defensive technique as
        // GridGameModuleTests.cs's shuffle-order-independent assertions.
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(1));
    }

    [Test]
    public async Task REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 5);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsAlreadyValid, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already meeting MinValidAnswers must never trigger a live Wikidata call");
    }

    // Documents a known, accepted limitation (see PlayerCacheWarmingService's
    // own doc comment): a pair cached BELOW MinValidAnswers looks identical
    // to a never-checked pair from CountPlayersWithBothAttributesAsync's
    // return value alone, so it's re-queried every run rather than being
    // recognized as "already confirmed low, don't bother again."
    [Test]
    public async Task REQ110_WarmAsync_PairCachedBelowMinValidAnswers_StillQueriesLiveAgain()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 2);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs()
    {
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.TotalPairs, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 1 country x 1 club = 1 Country x Club pair; C(1,2) = 0 Club x Club
        // pairs — a single club can never pair with itself.
        Assert.That(result.TotalPairs, Is.EqualTo(1));
    }
}

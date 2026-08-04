using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// ADR-0055: same real-InMemory-repository-plus-FakeWikidataClient pattern as
// PlayerPhotoBackfillServiceTests/PlayerCareerStintRefreshServiceTests
// (docs/coding-guidelines.md "don't over-mock").
public class PlayerCareerPrefetchServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerStoreRepository _playerStoreRepository = null!;
    private ICategoryValueRepository _categoryValueRepository = null!;
    private FakeWikidataClient _wikidataClient = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerStoreRepository = new PlayerStoreRepository(_dbContext);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _wikidataClient = new FakeWikidataClient();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerCareerPrefetchService BuildService() =>
        new(_categoryValueRepository, _playerStoreRepository, _wikidataClient, NullLogger<PlayerCareerPrefetchService>.Instance);

    private async Task<CountryDefinition> SeedCountryAsync(string name, string wikidataQid, bool usesCountryForSportProperty = false)
    {
        var country = new CountryDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid, UsesCountryForSportProperty = usesCountryForSportProperty };
        await _categoryValueRepository.AddCountryAsync(country);
        return country;
    }

    private async Task<ClubDefinition> SeedClubAsync(string name, string wikidataQid)
    {
        var club = new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid };
        await _categoryValueRepository.AddClubAsync(club);
        return club;
    }

    [Test]
    public async Task PrefetchAsync_SeededCountryWithPool_CreatesPlayersAndPersistsCareers()
    {
        await SeedCountryAsync("France", "Q142");
        _wikidataClient.SetPoolForNationality("Q142", [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France")]);
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Monaco", 1994, 1999, 105),
            new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.PlayersTouched, Is.EqualTo(1));
        Assert.That(result.StintsAdded, Is.EqualTo(2));

        var player = await _playerStoreRepository.GetPlayerByWikidataQidAsync("Q1519");
        Assert.That(player, Is.Not.Null, "a player never seen by xG Grid before must still get a Player row");

        var stints = await _playerStoreRepository.GetCareerStintsAsync(player!.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Monaco", "Arsenal" }));
    }

    // Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203 follow-up,
    // ADR-0059): PlayerCareerPrefetchService shares
    // PlayerCareerStintRefreshService.BuildNewStintsByPlayerId's
    // canonicalization — a fetched stint whose ClubQid matches a seeded
    // ClubDefinition must persist under that ClubDefinition.Name, not
    // Wikidata's own raw label.
    [Test]
    public async Task REQ1203_PrefetchAsync_FetchedClubQidMatchesSeededClub_PersistsSeededClubDefinitionName()
    {
        await SeedCountryAsync("France", "Q142");
        await SeedClubAsync("Lyon", "Q704");
        _wikidataClient.SetPoolForNationality("Q142", [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France")]);
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Olympique Lyonnais", 2000, 2003, 90, ClubQid: "Q704"));

        await BuildService().PrefetchAsync();

        var player = await _playerStoreRepository.GetPlayerByWikidataQidAsync("Q1519");
        var stints = await _playerStoreRepository.GetCareerStintsAsync(player!.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Lyon" }));
    }

    [Test]
    public async Task PrefetchAsync_NationalTeamCountry_UsesCountryForSportProperty()
    {
        var england = await SeedCountryAsync("England", "Q21", usesCountryForSportProperty: true);
        _wikidataClient.SetPoolForNationality("Q21", []);

        await BuildService().PrefetchAsync();

        Assert.That(_wikidataClient.QueriedNationalityQids, Does.Contain(england.WikidataQid));
        var index = _wikidataClient.QueriedNationalityQids.IndexOf(england.WikidataQid!);
        Assert.That(_wikidataClient.QueriedUsesCountryForSportProperty[index], Is.True);
    }

    [Test]
    public async Task PrefetchAsync_AlreadyKnownPlayer_GetsCareerCompleted_NotDuplicated()
    {
        await SeedCountryAsync("France", "Q142");
        var existingPlayer = await _playerStoreRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" });
        // Simulates a stint xG Grid's own byproduct lookup already recorded.
        await _playerStoreRepository.AddCareerStintsAsync(existingPlayer.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = existingPlayer.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254 }]);

        _wikidataClient.SetPoolForNationality("Q142", [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France")]);
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Monaco", 1994, 1999, 105), // Wikidata reveals this one, Grid never had it.
            new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254)); // Already known — must not duplicate.

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.StintsAdded, Is.EqualTo(1), "only the genuinely new stint (Monaco) counts");

        var stints = await _playerStoreRepository.GetCareerStintsAsync(existingPlayer.Id);
        Assert.That(stints, Has.Count.EqualTo(2));
        Assert.That(stints.Count(s => s.ClubName == "Arsenal"), Is.EqualTo(1), "the already-known Arsenal stint must not be duplicated");

        var rowCount = await _dbContext.Players.CountAsync(p => p.WikidataQid == "Q1519");
        Assert.That(rowCount, Is.EqualTo(1), "an already-known player must be reused, never duplicated");
    }

    [Test]
    public async Task PrefetchAsync_CountryWithNoQid_IsSkipped_NeverQueried()
    {
        await _categoryValueRepository.AddCountryAsync(new CountryDefinition { Id = Guid.NewGuid(), Name = "No QID Country", WikidataQid = null });

        var result = await BuildService().PrefetchAsync();

        Assert.That(_wikidataClient.QueriedNationalityQids, Is.Empty);
        Assert.That(result.CountriesFailed, Is.EqualTo(0));
    }

    [Test]
    public async Task PrefetchAsync_EmptyPoolForACountry_IsNotAFailure()
    {
        await SeedCountryAsync("Iceland", "Q189"); // No SetPoolForNationality call — genuinely zero eligible players configured.

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.CountriesFailed, Is.EqualTo(0));
        Assert.That(result.CountriesProcessed, Is.EqualTo(1));
    }

    // The run's own fail-loud contract: keep processing every country
    // regardless of one country's failure, only throw once everything has
    // been attempted — same shape as PlayerNameIndexImporter.ImportAsync's
    // failed-birth-year-slice handling.
    [Test]
    public async Task PrefetchAsync_OneCountryPoolQueryFails_StillProcessesTheRest_ThenThrows()
    {
        await SeedCountryAsync("France", "Q142");
        await SeedCountryAsync("Spain", "Q29");
        _wikidataClient.FailNextNationalityPoolCalls(1); // Fails whichever country is queried first.
        _wikidataClient.SetPoolForNationality("Q29", [new WikidataNameIndexEntry("Q9617", "Someone", 1990, "Spain")]);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await BuildService().PrefetchAsync());

        Assert.That(ex!.Message, Does.Contain("1 countr"));
        // The country queried second (not failed) must still have been processed.
        Assert.That(_wikidataClient.QueriedNationalityQids, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task PrefetchAsync_CareerFetchBatchFails_StillPersistsOtherBatches_ThenThrows()
    {
        await SeedCountryAsync("France", "Q142");
        var pool = Enumerable.Range(0, PlayerCareerPrefetchService.CareerBatchSize + 1)
            .Select(i => new WikidataNameIndexEntry($"Q{1000 + i}", $"Player {i}", 1990, "France"))
            .ToList();
        _wikidataClient.SetPoolForNationality("Q142", pool);
        // Second career batch (the lone extra player) gets a real stint;
        // the first CareerBatchSize-sized batch fails.
        _wikidataClient.SetCareerStints($"Q{1000 + PlayerCareerPrefetchService.CareerBatchSize}",
            new WikidataCareerStintEntry("Some Club", 2010, 2015, null));
        _wikidataClient.FailNextCareerStintBatches(1);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await BuildService().PrefetchAsync());

        Assert.That(ex!.Message, Does.Contain("1 career-fetch batch"));
        Assert.That(_wikidataClient.QueriedCareerStintBatches, Has.Count.EqualTo(2), "both batches must still be attempted");
    }

    [Test]
    public async Task PrefetchAsync_EveryCountrySucceeds_ReturnsResultWithoutThrowing()
    {
        await SeedCountryAsync("France", "Q142");
        _wikidataClient.SetPoolForNationality("Q142", [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France")]);
        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.CountriesFailed, Is.EqualTo(0));
        Assert.That(result.CareerBatchesFailed, Is.EqualTo(0));
        Assert.That(result.CountriesProcessed, Is.EqualTo(1));
    }
}

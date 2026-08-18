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
    // S-106/S-107 (pure refactor): GetOrCreatePlayersByWikidataQidAsync
    // lives on IPlayerRepository; GetCareerStintsByPlayerIdsAsync/
    // AddCareerStintsBatchAsync live on IPlayerCareerStintRepository — see
    // ADR-0067 for the full split of the original, now-deleted
    // IPlayerStoreRepository.
    private IPlayerCareerStintRepository _playerCareerStintRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private IPlayerAttributeRepository _playerAttributeRepository = null!;
    private IPlayerDataRepository _playerDataRepository = null!;
    private ICategoryValueRepository _categoryValueRepository = null!;
    private FakeWikidataClient _wikidataClient = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerCareerStintRepository = new PlayerCareerStintRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _playerAttributeRepository = new PlayerAttributeRepository(_dbContext);
        _playerDataRepository = new PlayerDataRepository(_dbContext);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _wikidataClient = new FakeWikidataClient();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerCareerPrefetchService BuildService() =>
        new(_categoryValueRepository, _playerCareerStintRepository, _playerRepository, _playerAttributeRepository,
            _playerDataRepository, _wikidataClient, NullLogger<PlayerCareerPrefetchService>.Instance);

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

        var player = await _playerRepository.GetPlayerByWikidataQidAsync("Q1519");
        Assert.That(player, Is.Not.Null, "a player never seen by xG Grid before must still get a Player row");

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player!.Id);
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

        var player = await _playerRepository.GetPlayerByWikidataQidAsync("Q1519");
        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player!.Id);
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
        var existingPlayer = await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" });
        // Simulates a stint xG Grid's own byproduct lookup already recorded.
        await _playerCareerStintRepository.AddCareerStintsAsync(existingPlayer.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = existingPlayer.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254 }]);

        _wikidataClient.SetPoolForNationality("Q142", [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France")]);
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Monaco", 1994, 1999, 105), // Wikidata reveals this one, Grid never had it.
            new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254)); // Already known — must not duplicate.

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.StintsAdded, Is.EqualTo(1), "only the genuinely new stint (Monaco) counts");

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(existingPlayer.Id);
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
        // Quality-gate fix (2026-08-18): the attribute write is unrelated to
        // and unaffected by the career-fetch batch failure above — every
        // one of the pool's CareerBatchSize+1 distinct players (spanning
        // both chunks) still gets its attribute recorded.
        Assert.That(ex.Message, Does.Contain($"{PlayerCareerPrefetchService.CareerBatchSize + 1} attribute(s) added"));
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

    // ---- Club sweep (ADR-0069) — mirrors the country-loop tests above ----

    [Test]
    public async Task ADR0069_PrefetchAsync_SeededClubWithPool_CreatesPlayersAndPersistsCareers()
    {
        await SeedClubAsync("Celtic", "Q19593");
        _wikidataClient.SetPoolForClub("Q19593", [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France")]);
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Monaco", 1994, 1999, 105),
            new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.PlayersTouched, Is.EqualTo(1));
        Assert.That(result.StintsAdded, Is.EqualTo(2));
        Assert.That(result.ClubsProcessed, Is.EqualTo(1));

        var player = await _playerRepository.GetPlayerByWikidataQidAsync("Q1519");
        Assert.That(player, Is.Not.Null,
            "a player from an unseeded country who played for a seeded club must still get a Player row — this is the whole point of ADR-0069");

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player!.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Monaco", "Arsenal" }));
    }

    [Test]
    public async Task ADR0069_PrefetchAsync_ClubWithNoQid_IsSkipped_NeverQueried()
    {
        await _categoryValueRepository.AddClubAsync(new ClubDefinition { Id = Guid.NewGuid(), Name = "No QID Club", WikidataQid = null });

        var result = await BuildService().PrefetchAsync();

        Assert.That(_wikidataClient.QueriedClubQids, Is.Empty);
        Assert.That(result.ClubsFailed, Is.EqualTo(0));
    }

    [Test]
    public async Task ADR0069_PrefetchAsync_EmptyPoolForAClub_IsNotAFailure()
    {
        await SeedClubAsync("Some Small Club", "Q999999"); // No SetPoolForClub call — genuinely zero eligible players configured.

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.ClubsFailed, Is.EqualTo(0));
        Assert.That(result.ClubsProcessed, Is.EqualTo(1));
    }

    [Test]
    public async Task ADR0069_PrefetchAsync_OneClubPoolQueryFails_StillProcessesTheRest_ThenThrows()
    {
        await SeedClubAsync("Celtic", "Q19593");
        await SeedClubAsync("Rangers", "Q734589");
        _wikidataClient.FailNextClubPoolCalls(1); // Fails whichever club is queried first.
        _wikidataClient.SetPoolForClub("Q734589", [new WikidataNameIndexEntry("Q9617", "Someone", 1990, "Scotland")]);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await BuildService().PrefetchAsync());

        Assert.That(ex!.Message, Does.Contain("1 club"));
        // The club queried second (not failed) must still have been processed.
        Assert.That(_wikidataClient.QueriedClubQids, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task ADR0069_PrefetchAsync_CountryAndClubSweepsBothRun_InTheSamePass()
    {
        await SeedCountryAsync("France", "Q142");
        await SeedClubAsync("Celtic", "Q19593");
        _wikidataClient.SetPoolForNationality("Q142", [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France")]);
        _wikidataClient.SetPoolForClub("Q19593", [new WikidataNameIndexEntry("Q9617", "Someone Else", 1990, "Scotland")]);
        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));
        _wikidataClient.SetCareerStints("Q9617", new WikidataCareerStintEntry("Celtic", 2010, 2015, 90));

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.CountriesProcessed, Is.EqualTo(1));
        Assert.That(result.ClubsProcessed, Is.EqualTo(1));
        Assert.That(result.PlayersTouched, Is.EqualTo(2), "both sweeps' players must be touched in the same run");
        Assert.That(result.StintsAdded, Is.EqualTo(2));
    }

    [Test]
    public async Task ADR0069_PrefetchAsync_EveryClubSucceeds_ReturnsResultWithoutThrowing()
    {
        await SeedClubAsync("Celtic", "Q19593");
        _wikidataClient.SetPoolForClub("Q19593", [new WikidataNameIndexEntry("Q9617", "Someone", 1990, "Scotland")]);
        _wikidataClient.SetCareerStints("Q9617", new WikidataCareerStintEntry("Celtic", 2010, 2015, 90));

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.ClubsFailed, Is.EqualTo(0));
        Assert.That(result.CareerBatchesFailed, Is.EqualTo(0));
        Assert.That(result.ClubsProcessed, Is.EqualTo(1));
    }

    // ---- PlayerAttribute persistence (REQ-110 follow-up) ----
    // These sweeps' pool queries already filter by nationality/club in their
    // own WHERE clause (FakeWikidataClient.SetPoolForNationality/
    // SetPoolForClub simulate exactly that), so every pooled player is known
    // to satisfy the attribute without any further Wikidata round trip —
    // see PlayerCareerPrefetchService's own doc comment.

    [Test]
    public async Task REQ110_PrefetchAsync_CountryPoolSweep_WritesNationalityAttributePerPooledPlayer()
    {
        await SeedCountryAsync("France", "Q142");
        _wikidataClient.SetPoolForNationality("Q142",
        [
            new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France"),
            new WikidataNameIndexEntry("Q1521", "Zinedine Zidane", 1972, "France"),
        ]);

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.AttributesAdded, Is.EqualTo(2));

        var henry = await _playerRepository.GetPlayerByWikidataQidAsync("Q1519");
        var zidane = await _playerRepository.GetPlayerByWikidataQidAsync("Q1521");
        var attributes = await _playerAttributeRepository.GetPlayerAttributesAsync("nationality", "France");
        Assert.That(attributes.Select(a => a.PlayerId), Is.EquivalentTo(new[] { henry!.Id, zidane!.Id }));

        // Quality-gate fix (2026-08-18): REQ-502's admin view needs a
        // Source/Confidence to show for every PlayerAttribute row — assert
        // the paired PlayerData row exists, same "wikidata"/"verified"
        // shape WikidataLookupService.QueueAttribute already writes.
        var henryData = await _dbContext.PlayerData.Where(d => d.PlayerId == henry.Id && d.Field == "nationality").ToListAsync();
        Assert.That(henryData, Has.Count.EqualTo(1));
        Assert.That(henryData[0].Value, Is.EqualTo("France"));
        Assert.That(henryData[0].Source, Is.EqualTo("wikidata"));
        Assert.That(henryData[0].Confidence, Is.EqualTo("verified"));
    }

    [Test]
    public async Task ADR0069_PrefetchAsync_ClubPoolSweep_WritesClubAttributePerPooledPlayer()
    {
        await SeedClubAsync("Celtic", "Q19593");
        _wikidataClient.SetPoolForClub("Q19593",
        [
            new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France"),
            new WikidataNameIndexEntry("Q9617", "Someone Else", 1990, "Scotland"),
        ]);

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.AttributesAdded, Is.EqualTo(2));

        var henry = await _playerRepository.GetPlayerByWikidataQidAsync("Q1519");
        var someoneElse = await _playerRepository.GetPlayerByWikidataQidAsync("Q9617");
        var attributes = await _playerAttributeRepository.GetPlayerAttributesAsync("club", "Celtic");
        Assert.That(attributes.Select(a => a.PlayerId), Is.EquivalentTo(new[] { henry!.Id, someoneElse!.Id }));

        var henryData = await _dbContext.PlayerData.Where(d => d.PlayerId == henry.Id && d.Field == "club").ToListAsync();
        Assert.That(henryData, Has.Count.EqualTo(1));
        Assert.That(henryData[0].Value, Is.EqualTo("Celtic"));
        Assert.That(henryData[0].Source, Is.EqualTo("wikidata"));
        Assert.That(henryData[0].Confidence, Is.EqualTo("verified"));
    }

    [Test]
    public async Task REQ110_PrefetchAsync_PlayerAlreadyHasAttribute_DoesNotDuplicate()
    {
        await SeedCountryAsync("France", "Q142");
        var existingPlayer = await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" });
        await _playerAttributeRepository.AddPlayerAttributeAsync(
            new PlayerAttribute { PlayerId = existingPlayer.Id, AttributeType = "nationality", AttributeValue = "France" });

        _wikidataClient.SetPoolForNationality("Q142", [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France")]);

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.AttributesAdded, Is.EqualTo(0), "the already-existing attribute must not be counted as newly added");

        var attributes = await _playerAttributeRepository.GetPlayerAttributesAsync("nationality", "France");
        Assert.That(attributes, Has.Count.EqualTo(1), "the already-existing attribute must not be duplicated");

        // No new PlayerAttribute means no new paired PlayerData either — the
        // write is gated by the same dedup check, not issued independently.
        var existingPlayerData = await _dbContext.PlayerData.Where(d => d.PlayerId == existingPlayer.Id && d.Field == "nationality").ToListAsync();
        Assert.That(existingPlayerData, Is.Empty, "no PlayerData row is written when the paired PlayerAttribute is a duplicate");
    }

    [Test]
    public async Task REQ110_PrefetchAsync_CountryAndClubSweeps_OnlyCountsGenuinelyNewAttributes()
    {
        await SeedCountryAsync("France", "Q142");
        await SeedClubAsync("Celtic", "Q19593");
        // Same player (Q1519) appears in both pools — nationality "France"
        // and club "Celtic" are two distinct attribute rows, so both count
        // as new, but a second appearance under the SAME attribute type
        // must not double-count.
        _wikidataClient.SetPoolForNationality("Q142", [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France")]);
        _wikidataClient.SetPoolForClub("Q19593", [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France")]);

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.AttributesAdded, Is.EqualTo(2), "one nationality row and one club row for the same player, both new");

        var player = await _playerRepository.GetPlayerByWikidataQidAsync("Q1519");
        var allAttributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == player!.Id).ToListAsync();
        Assert.That(allAttributes.Select(a => (a.AttributeType, a.AttributeValue)),
            Is.EquivalentTo(new[] { ("nationality", "France"), ("club", "Celtic") }));

        var allPlayerData = await _dbContext.PlayerData.Where(d => d.PlayerId == player!.Id).ToListAsync();
        Assert.That(allPlayerData.Select(d => (d.Field, d.Value)),
            Is.EquivalentTo(new[] { ("nationality", "France"), ("club", "Celtic") }));
        Assert.That(allPlayerData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "verified"));
    }

    // Quality-gate fix (2026-08-18): the dedup HashSet<Guid> is built once
    // per country/club and passed BY REFERENCE into FetchAndPersistBatchAsync
    // across the WHOLE pool's set of CareerBatchSize-sized chunks — this
    // proves cross-chunk dedup actually works, not just within a single
    // chunk. If the HashSet were rebuilt per-chunk instead of shared across
    // the pool (the exact regression this guards against), the duplicate
    // QID appended below (in the pool's second chunk) would be wrongly
    // counted as a second, genuinely-new attribute.
    [Test]
    public async Task REQ110_PrefetchAsync_AttributeDedup_CatchesDuplicatePlayerAcrossChunkBoundary()
    {
        await SeedCountryAsync("France", "Q142");
        var pool = Enumerable.Range(0, PlayerCareerPrefetchService.CareerBatchSize)
            .Select(i => new WikidataNameIndexEntry($"Q{1000 + i}", $"Player {i}", 1990, "France"))
            .ToList();
        // One extra entry beyond the first CareerBatchSize-sized chunk,
        // reusing the very first entry's QID (Q1000) — lands in the pool's
        // second chunk while duplicating a player already processed in the
        // first.
        pool.Add(new WikidataNameIndexEntry("Q1000", "Player 0 (duplicate)", 1990, "France"));
        _wikidataClient.SetPoolForNationality("Q142", pool);

        var result = await BuildService().PrefetchAsync();

        Assert.That(result.AttributesAdded, Is.EqualTo(PlayerCareerPrefetchService.CareerBatchSize),
            "the duplicate QID in the second chunk must not be counted as a new attribute");

        var attributes = await _playerAttributeRepository.GetPlayerAttributesAsync("nationality", "France");
        Assert.That(attributes, Has.Count.EqualTo(PlayerCareerPrefetchService.CareerBatchSize),
            "no duplicate PlayerAttribute row for the player appearing in both chunks");
    }
}

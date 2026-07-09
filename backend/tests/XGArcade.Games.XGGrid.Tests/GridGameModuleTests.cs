using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGGrid.Tests;

// REQ-101 (generate a valid grid), REQ-102 (configurable grid size),
// REQ-107 (category pairing constraint), REQ-109 (category value reference
// tables) — docs/requirements-document.md §4.1. Follows this repo's
// no-mocking-framework pattern (docs/coding-guidelines.md "don't over-mock"):
// real, InMemory-backed repositories (same setup as
// XGArcade.DataSync.Tests/Wikidata/WikidataLookupServiceTests.cs) plus a
// small hand-rolled FakeWikidataLookupService for the live-lookup fallback.
public class GridGameModuleTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IGridInstanceRepository _gridInstanceRepository = null!;
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
        _gridInstanceRepository = new GridInstanceRepository(_dbContext);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _playerStoreRepository = new PlayerStoreRepository(_dbContext);
        _wikidataLookupService = new FakeWikidataLookupService();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private GridGameModule BuildModule(int minValidAnswers, int maxAttempts) =>
        new(_gridInstanceRepository, _categoryValueRepository, _playerStoreRepository, _wikidataLookupService,
            new GridGenerationOptions { MinValidAnswers = minValidAnswers, MaxAttempts = maxAttempts });

    private GridTemplate SeedTemplate(int size)
    {
        var template = new GridTemplate { Id = Guid.NewGuid(), Size = size, AllowedCategoryTypes = ["country", "club"] };
        _dbContext.GridTemplates.Add(template);
        _dbContext.SaveChanges();
        return template;
    }

    private CountryDefinition SeedCountry(string name, string? wikidataQid = "unset")
    {
        var country = new CountryDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid == "unset" ? $"Qcountry-{name}" : wikidataQid };
        _dbContext.CountryDefinitions.Add(country);
        _dbContext.SaveChanges();
        return country;
    }

    private ClubDefinition SeedClub(string name, string? wikidataQid = "unset")
    {
        var club = new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid == "unset" ? $"Qclub-{name}" : wikidataQid };
        _dbContext.ClubDefinitions.Add(club);
        _dbContext.SaveChanges();
        return club;
    }

    // Seeds `count` distinct players in the local cache, each satisfying
    // both (nationality = countryName) and (club = clubName), so
    // CountPlayersWithBothAttributesAsync(countryName, clubName) == count.
    private void SeedCachedMatches(string countryName, string clubName, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var player = new Player
            {
                Id = Guid.NewGuid(),
                FullName = $"{countryName}-{clubName}-Player{i}",
                WikidataQid = $"Qplayer-{countryName}-{clubName}-{i}",
            };
            _dbContext.Players.Add(player);
            _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = countryName });
            _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = clubName });
        }
        _dbContext.SaveChanges();
    }

    private static List<Player> BuildFakeLivePlayers(string label, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Player { Id = Guid.NewGuid(), FullName = $"{label}-Live{i}", WikidataQid = $"Qlive-{label}-{i}" })
            .ToList();

    // ---- REQ-101: generate a valid grid -----------------------------------

    [Test]
    public async Task REQ101_GridGeneration_DiscardsCellWithFewerThanMinimumAnswers()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        // Four candidates below MinValidAnswers, plus exactly one that meets
        // it. Whichever order the module's internal shuffle tries them in,
        // only "GoodClub" can ever be accepted — so asserting the final
        // header is "GoodClub" proves the too-few-answers candidates were
        // discarded and retried past, not that they got lucky first.
        SeedClub("WeakClub0");
        SeedClub("WeakClub1");
        SeedClub("WeakClub2");
        SeedClub("WeakClub3");
        SeedClub("GoodClub");
        SeedCachedMatches("France", "WeakClub0", 0);
        SeedCachedMatches("France", "WeakClub1", 1);
        SeedCachedMatches("France", "WeakClub2", 2);
        SeedCachedMatches("France", "WeakClub3", 2);
        SeedCachedMatches("France", "GoodClub", 3);
        var module = BuildModule(minValidAnswers: 3, maxAttempts: 5);

        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        Assert.That(instance.Cells[0].RowCategoryValue, Is.EqualTo("France"));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("GoodClub"));
    }

    [Test]
    public async Task REQ101_GridGeneration_AbortsWithGridGenerationException_WhenMaxAttemptsExhausted()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        // Five club candidates, none ever satisfying MinValidAnswers=5 (all
        // cached at 0) — with MaxAttempts=3, the loop must abort before
        // exhausting the candidate pool.
        for (var i = 0; i < 5; i++)
        {
            SeedClub($"NeverEnoughClub{i}");
            SeedCachedMatches("France", $"NeverEnoughClub{i}", 0);
        }
        var module = BuildModule(minValidAnswers: 5, maxAttempts: 3);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("3 attempts"));
    }

    [Test]
    public async Task REQ101_GridGeneration_CacheMiss_FallsBackToLiveLookupAndSucceeds()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("Arsenal");
        // No cached PlayerAttribute rows for France/Arsenal at all — this is
        // a pure cache miss, so the live lookup is the only source of match
        // data for this candidate.
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakeLivePlayers("Arsenal", 3));
        var module = BuildModule(minValidAnswers: 3, maxAttempts: 5);

        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("Arsenal"));
        Assert.That(await _playerStoreRepository.CountPlayersWithBothAttributesAsync(
            "nationality", "France", "club", "Arsenal"), Is.EqualTo(0),
            "the cache itself was never populated — the match came only from the fake live lookup");
    }

    [Test]
    public async Task GenerateInstanceAsync_UnknownTemplateId_ThrowsGridGenerationException()
    {
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));

        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    // ---- REQ-102: configurable grid size -----------------------------------

    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task REQ102_GenerateInstanceAsync_ProducesExactlySizeSquaredCellsWithUniqueRowAndColumnValues(int size)
    {
        var template = SeedTemplate(size);
        var countryNames = Enumerable.Range(0, size).Select(i => $"Country{i}").ToList();
        var clubNames = Enumerable.Range(0, size).Select(i => $"Club{i}").ToList();
        foreach (var countryName in countryNames)
            SeedCountry(countryName);
        foreach (var clubName in clubNames)
            SeedClub(clubName);
        foreach (var countryName in countryNames)
            foreach (var clubName in clubNames)
                SeedCachedMatches(countryName, clubName, count: 2);
        var module = BuildModule(minValidAnswers: 2, maxAttempts: 50);

        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(size * size));

        var rowValues = instance.Cells.Select(c => c.RowCategoryValue).Distinct().ToList();
        var colValues = instance.Cells.Select(c => c.ColCategoryValue).Distinct().ToList();
        Assert.That(rowValues, Has.Count.EqualTo(size));
        Assert.That(colValues, Has.Count.EqualTo(size));
        Assert.That(rowValues.Intersect(colValues), Is.Empty, "no row category value may equal a column category value");
    }

    // ---- REQ-107: category pairing constraint ------------------------------

    [Test]
    public void REQ107_IsAllowedPairing_RejectsCountryCountryPairing()
    {
        var isAllowed = CategoryPairingRules.IsAllowedPairing(CategoryPairingRules.Country, CategoryPairingRules.Country);

        Assert.That(isAllowed, Is.False);
    }

    [TestCase(CategoryPairingRules.Club, CategoryPairingRules.Club)]
    [TestCase(CategoryPairingRules.Club, CategoryPairingRules.Country)]
    [TestCase(CategoryPairingRules.Country, CategoryPairingRules.Club)]
    public void REQ107_IsAllowedPairing_AllowsEveryPairingOtherThanCountryCountry(string rowType, string colType)
    {
        var isAllowed = CategoryPairingRules.IsAllowedPairing(rowType, colType);

        Assert.That(isAllowed, Is.True);
    }

    [Test]
    public async Task REQ107_GenerateInstanceAsync_NeverProducesCountryCountryPairing()
    {
        var template = SeedTemplate(size: 2);
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedCachedMatches("France", "Arsenal", 2);
        SeedCachedMatches("France", "Barcelona", 2);
        SeedCachedMatches("Spain", "Arsenal", 2);
        SeedCachedMatches("Spain", "Barcelona", 2);
        var module = BuildModule(minValidAnswers: 2, maxAttempts: 20);

        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.None.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Country && c.ColCategoryType == CategoryPairingRules.Country));
    }

    // ---- REQ-109: category value reference tables --------------------------

    [Test]
    public async Task REQ109_GenerateInstanceAsync_OnlyUsesValuesFromReferenceTables_NeverFromPlayerAttributeAlone()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("France", "Arsenal", 3);
        // "PhantomClub" has abundant matching data in PlayerAttribute but was
        // never added as a ClubDefinition row — it must never be considered
        // as a candidate, however good its match count.
        SeedCachedMatches("France", "PhantomClub", 10);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells[0].ColCategoryValue, Is.EqualTo("Arsenal"));
        Assert.That(instance.Cells.Select(c => c.ColCategoryValue), Does.Not.Contain("PhantomClub"));
    }

    [Test]
    public async Task REQ109_GenerateInstanceAsync_NullWikidataQid_DoesNotThrow_AndDiscardsThroughOrdinaryRetry()
    {
        var template = SeedTemplate(size: 1);
        // No resolved WikidataQid yet (REQ-109) — must not crash generation.
        SeedCountry("Ruritania", wikidataQid: null);
        SeedClub("NoDataClub");   // cache miss; live lookup is skipped (null country QID) -> 0 matches, discarded
        SeedClub("GoodClub");     // cache hit -> accepted without ever needing a live lookup
        SeedCachedMatches("Ruritania", "GoodClub", 2);
        // Configured on the fake, but unreachable via the real contract since
        // the country QID is null — proves the module never gets a match for
        // "NoDataClub" from this path, only from the (absent) cache.
        _wikidataLookupService.SetMatches("Ruritania", "NoDataClub", BuildFakeLivePlayers("NoDataClub", 5));
        var module = BuildModule(minValidAnswers: 2, maxAttempts: 5);

        GameInstance? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result!.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells[0].RowCategoryValue, Is.EqualTo("Ruritania"));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("GoodClub"));
    }
}

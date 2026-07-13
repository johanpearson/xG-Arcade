using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

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
        _wikidataLookupService = new FakeWikidataLookupService(_playerStoreRepository);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    // S-030's SelectPairing coin-flips between Country x Club and Club x
    // Club whenever the seeded reference data can support either — every
    // pre-existing test in this file (written before Club x Club existed)
    // asserts a specific Country x Club outcome, so BuildModule pins that
    // choice by default (nextValue: 0) rather than letting Random.Shared
    // make those tests flaky. Most REQ107_/REQ211_-named Club x Club tests
    // below instead seed too few countries for Country x Club to be
    // feasible at all, forcing Club x Club regardless of the injected
    // Random — the more robust technique, since it also covers
    // SelectPairing's "only one pairing feasible" branches. The one
    // exception is REQ107_GenerateInstanceAsync_BothPairingsFeasible_
    // CoinFlipsBetweenCountryClubAndClubClub below, which explicitly passes
    // nextValue: 1 to exercise the "both feasible, coin-flip picks Club x
    // Club" branch that no data-starved test can reach.
    private sealed class FixedChoiceRandom(int nextValue) : Random
    {
        public override int Next(int maxValue) => nextValue;
    }

    // ADR-0023: maxDuration defaults to a generous 10 minutes so none of the
    // pre-existing tests below (none of which advance a fake clock) can
    // ever trip the deadline-abort branch by accident — only tests that
    // explicitly pass a short maxDuration plus a controllable timeProvider
    // exercise that path.
    private GridGameModule BuildModule(
        int minValidAnswers, int maxAttempts, Random? random = null,
        TimeSpan? maxDuration = null, TimeProvider? timeProvider = null,
        IWikidataLookupService? wikidataLookupService = null) =>
        new(_gridInstanceRepository, _categoryValueRepository, _playerStoreRepository, wikidataLookupService ?? _wikidataLookupService,
            new GridGenerationOptions { MinValidAnswers = minValidAnswers, MaxAttempts = maxAttempts, MaxDuration = maxDuration ?? TimeSpan.FromMinutes(10) },
            NullLogger<GridGameModule>.Instance,
            random ?? new FixedChoiceRandom(0),
            timeProvider);

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

    // S-030: Club x Club counterpart to SeedCachedMatches above — both
    // category values are AttributeType "club" (never "nationality"), since
    // CountPlayersWithBothAttributesAsync is symmetric in its two
    // type/value pairs (PlayerStoreRepositoryTests), one call per unordered
    // club pair is enough to satisfy a match-count check regardless of
    // which club ends up on the row axis vs the column axis.
    private void SeedCachedClubClubMatches(string clubAName, string clubBName, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var player = new Player
            {
                Id = Guid.NewGuid(),
                FullName = $"{clubAName}-{clubBName}-Player{i}",
                WikidataQid = $"Qplayer-{clubAName}-{clubBName}-{i}",
            };
            _dbContext.Players.Add(player);
            _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = clubAName });
            _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = clubBName });
        }
        _dbContext.SaveChanges();
    }

    private static List<Player> BuildFakeLivePlayers(string label, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Player { Id = Guid.NewGuid(), FullName = $"{label}-Live{i}", WikidataQid = $"Qlive-{label}-{i}" })
            .ToList();

    // Seeds a single-cell GridInstance directly (bypassing GenerateInstanceAsync
    // entirely) — S-009's ScoreSubmissionAsync tests only need a fixed cell to
    // score guesses against, not a whole generated grid.
    private async Task<(Guid InstanceId, Guid CellId)> SeedGridInstanceAsync(
        string rowCategoryValue, string colCategoryValue,
        string rowCategoryType = CategoryPairingRules.Country, string colCategoryType = CategoryPairingRules.Club)
    {
        var instanceId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        var instance = new GridInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            Cells =
            [
                new GridCell
                {
                    Id = cellId,
                    GridInstanceId = instanceId,
                    Row = 0,
                    Col = 0,
                    RowCategoryType = rowCategoryType,
                    RowCategoryValue = rowCategoryValue,
                    ColCategoryType = colCategoryType,
                    ColCategoryValue = colCategoryValue,
                },
            ],
        };
        await _gridInstanceRepository.AddInstanceAsync(instance);
        return (instanceId, cellId);
    }

    // Seeds a Player with cached PlayerAttribute rows for both nationality and
    // club — the "effective data" ScoreSubmissionAsync's guess-checking reads
    // via HasEffectiveAttributeAsync (REQ-203).
    private async Task<Player> SeedPlayerAsync(string fullName, string nationality, string club)
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = fullName, WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        _dbContext.Players.Add(player);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = nationality });
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = club });
        await _dbContext.SaveChangesAsync();
        return player;
    }

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

    // ---- ADR-0021: cell ids for round-close's unanswered-cell penalty -----

    [Test]
    public async Task REQ206_GetCellIdsAsync_GeneratedInstance_ReturnsEveryCellId()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("France", "Arsenal", 3);
        var module = BuildModule(minValidAnswers: 3, maxAttempts: 5);
        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);

        var cellIds = await module.GetCellIdsAsync(result.Id);

        Assert.That(cellIds, Is.EquivalentTo(instance!.Cells.Select(c => c.Id)));
    }

    [Test]
    public void GetCellIdsAsync_UnknownInstanceId_ThrowsGuessScoringException()
    {
        var module = BuildModule(minValidAnswers: 3, maxAttempts: 5);

        Assert.ThrowsAsync<GuessScoringException>(async () =>
            await module.GetCellIdsAsync(Guid.NewGuid()));
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

    // ADR-0023: the 2026-07-12/13 dev incident this abort condition exists
    // for — MaxAttempts alone (500 by default) never helped, since a real
    // run can chain enough genuinely-slow-or-cache-missing live Wikidata
    // calls to blow well past any infrastructure request timeout long
    // before exhausting that count.
    [Test]
    public async Task REQ101_GridGeneration_AbortsWithGridGenerationException_WhenMaxDurationExceeded()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        for (var i = 0; i < 5; i++)
            SeedClub($"SlowClub{i}");
        // No SeedCachedMatches call — every candidate is a genuine cache
        // miss, forcing GetMatchCountAsync down the live-lookup path
        // (FakeWikidataLookupService's onCalled hook below) every time,
        // same as the incident's cold-cache scenario. None of them have any
        // configured match either, so every one is rejected on its own
        // terms too — the point of this test is that the deadline trips
        // first, not that a candidate would eventually have been rejected
        // anyway.
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var wikidataLookupService = new FakeWikidataLookupService(
            onCalled: () => clock.Advance(TimeSpan.FromSeconds(20)));
        var module = BuildModule(
            minValidAnswers: 5, maxAttempts: 500,
            maxDuration: TimeSpan.FromSeconds(30), timeProvider: clock,
            wikidataLookupService: wikidataLookupService);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("exceeding"));
        Assert.That(ex.Message, Does.Contain("00:00:30"), "should name the configured MaxDuration, not a raw attempt count");
    }

    // ADR-0023: MaxDuration must never interfere with an ordinary, fast
    // generation — only a genuinely slow/stuck one. Uses a finite-but-generous
    // MaxDuration (not BuildModule's 10-minute test default, which would mask
    // a bad comparison/units bug) and only cached matches, so the whole run
    // costs microseconds against the real system clock, well under the
    // deadline. Complements the abort test above rather than duplicating it —
    // that test proves the deadline trips when it should; this one proves it
    // stays quiet when it shouldn't trip.
    [Test]
    public async Task REQ101_GridGeneration_FastSuccessfulRun_WellUnderMaxDuration_SucceedsUnaffected()
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
        var module = BuildModule(minValidAnswers: 2, maxAttempts: 20, maxDuration: TimeSpan.FromSeconds(5));

        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(4),
            "an ordinary all-cache-hit run must succeed normally — MaxDuration must not abort a run that never gets close to it");
    }

    // ADR-0023's deadline check (`_timeProvider.GetUtcNow() >= deadline`) is
    // deliberately inclusive — landing exactly ON the deadline must still
    // abort, not be allowed one more attempt. Distinct from the test above:
    // that one advances the clock well past the deadline (40s against a 30s
    // budget) before the trip is observed; this one lands the clock on
    // exactly the deadline after a single attempt and proves the very next
    // check aborts before a second live lookup is ever attempted. If the
    // check were `>` instead of `>=`, this test would instead see a second
    // live lookup happen and, once the two-club pool is exhausted, a
    // "Ran out of candidates" GridGenerationException instead — a different
    // message this test's assertions would catch.
    [Test]
    public async Task REQ101_GridGeneration_AbortsWithGridGenerationException_WhenClockLandsExactlyOnDeadline()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("ClubA");
        SeedClub("ClubB");
        // Neither club has cached matches or a configured live match — both
        // are genuine cache misses forced through the live-lookup path, and
        // both would be rejected on their own terms too. The point is
        // whether a second attempt is even tried once the clock lands
        // exactly on the deadline after the first.
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var wikidataLookupService = new FakeWikidataLookupService(
            onCalled: () => clock.Advance(TimeSpan.FromSeconds(20)));
        var module = BuildModule(
            minValidAnswers: 5, maxAttempts: 500,
            maxDuration: TimeSpan.FromSeconds(20), timeProvider: clock,
            wikidataLookupService: wikidataLookupService);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("exceeding"));
        Assert.That(ex.Message, Does.Contain("found 0/1 valid headers in 1 attempts"),
            "must abort on the very next check after landing exactly on the deadline, before a second live lookup");
        Assert.That(
            wikidataLookupService.GetCallCount("France", "ClubA") + wikidataLookupService.GetCallCount("France", "ClubB"),
            Is.EqualTo(1), "only the first candidate's live lookup should ever run — the second must never be attempted");
    }

    // S-030: PickHeadersAsync's deadline check is shared code, not
    // duplicated per pairing type — but GetMatchCountAsync's live-lookup
    // dispatch (LookupLiveMatchesAsync) branches by category type, so this
    // confirms the deadline also trips when that dispatch routes through
    // LookupAndPersistClubClubAsync, not just the Country x Club branch the
    // test above exercises.
    [Test]
    public async Task REQ101_GridGeneration_ClubClubPairing_AbortsWithGridGenerationException_WhenMaxDurationExceeded()
    {
        var template = SeedTemplate(size: 1);
        // Zero countries seeded -> Country x Club is infeasible, forcing
        // Club x Club regardless of the injected Random (same technique the
        // other Club x Club tests in this file use).
        for (var i = 0; i < 4; i++)
            SeedClub($"SlowClub{i}");
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var wikidataLookupService = new FakeWikidataLookupService(
            onCalled: () => clock.Advance(TimeSpan.FromSeconds(20)));
        var module = BuildModule(
            minValidAnswers: 5, maxAttempts: 500,
            maxDuration: TimeSpan.FromSeconds(30), timeProvider: clock,
            wikidataLookupService: wikidataLookupService);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("exceeding"));
        Assert.That(ex.Message, Does.Contain("00:00:30"));
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
            "nationality", "France", "club", "Arsenal"), Is.EqualTo(3),
            "a live lookup persists immediately, same request, same as the real WikidataLookupService (ADR-0010) — " +
            "not left for the cache to somehow already have known about");
    }

    [Test]
    public void REQ101_GridGenerationOptions_DefaultsMinValidAnswersToFive()
    {
        var options = new GridGenerationOptions();

        Assert.That(options.MinValidAnswers, Is.EqualTo(5));
        Assert.That(options.MaxAttempts, Is.EqualTo(500), "S-014 only raised MinValidAnswers; MaxAttempts is unchanged");
        Assert.That(options.MaxDuration, Is.EqualTo(TimeSpan.FromSeconds(90)), "ADR-0023");
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

    // ---- REQ-107/S-030: Club x Club pairing --------------------------------

    [Test]
    public async Task REQ107_GenerateInstanceAsync_ClubClubGrid_ProducesExactlySizeSquaredCellsWithUniqueRowAndColumnValues()
    {
        var template = SeedTemplate(size: 3);
        // Zero countries seeded at all -> Country x Club is infeasible
        // (countryCount=0 < size=3), so SelectPairing deterministically
        // picks Club x Club regardless of the injected Random, once >= 2 *
        // size = 6 distinct clubs exist (REQ-102's no-shared-header rule
        // needs 2x, not just size, distinct clubs for Club x Club).
        var clubNames = Enumerable.Range(0, 6).Select(i => $"Club{i}").ToList();
        foreach (var clubName in clubNames)
            SeedClub(clubName);
        for (var i = 0; i < clubNames.Count; i++)
            for (var j = i + 1; j < clubNames.Count; j++)
                SeedCachedClubClubMatches(clubNames[i], clubNames[j], count: 2);
        var module = BuildModule(minValidAnswers: 2, maxAttempts: 50);

        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(9));
        Assert.That(instance.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Club && c.ColCategoryType == CategoryPairingRules.Club),
            "SelectPairing must have picked Club x Club, not Country x Club, given zero seeded countries");
        Assert.That(instance.Cells, Has.None.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Country && c.ColCategoryType == CategoryPairingRules.Country),
            "Country x Country must never be produced (REQ-107), regardless of pairing choice");

        var rowValues = instance.Cells.Select(c => c.RowCategoryValue).Distinct().ToList();
        var colValues = instance.Cells.Select(c => c.ColCategoryValue).Distinct().ToList();
        Assert.That(rowValues, Has.Count.EqualTo(3), "REQ-102: N unique row categories");
        Assert.That(colValues, Has.Count.EqualTo(3), "REQ-102: N unique column categories");
        Assert.That(rowValues.Intersect(colValues), Is.Empty,
            "REQ-102: no row category value may equal a column category value — the constraint Club x Club actually needs 2xSize clubs for");
    }

    [Test]
    public async Task REQ107_GenerateInstanceAsync_BothPairingsFeasible_CoinFlipsBetweenCountryClubAndClubClub()
    {
        // Unlike every other Club x Club test in this file, both pairings
        // are feasible here (1 country, 2 clubs) — SelectPairing's
        // random-coin-flip branch (both feasible) only fires in this shape;
        // every other test either pins FixedChoiceRandom(0)'s default
        // (Country x Club) or starves countries to force Club x Club
        // deterministically regardless of the random draw. This is the only
        // test that actually exercises the "both feasible, _random.Next(2)
        // resolves to Club x Club" branch — without it, a bug that always
        // resolved to Country x Club even when the draw should pick
        // Club x Club (e.g. a swapped ternary) would go uncaught.
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedCachedMatches("France", "Arsenal", 2);
        SeedCachedClubClubMatches("Arsenal", "Barcelona", 2);
        var module = BuildModule(minValidAnswers: 2, maxAttempts: 20, random: new FixedChoiceRandom(1));

        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Club && c.ColCategoryType == CategoryPairingRules.Club),
            "with both pairings feasible, FixedChoiceRandom(1) must steer SelectPairing to Club x Club, not the Country x Club default");
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

    // ---- REQ-203/210: guess correctness validation (ScoreSubmissionAsync) --
    // REQ-210's lock/attempt-cap checks and REQ-202's guess-change policy
    // already happened in Core.Scoring before ScoreSubmissionAsync is ever
    // called (GuessSubmissionServiceTests) — these tests exercise only the
    // name-resolution/correctness-checking half owned by this game module.

    [Test]
    public async Task REQ203_ScoreSubmissionAsync_CandidateSatisfiesBothCategories_ReturnsCorrectWithPlayerAnswerId()
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var player = await SeedPlayerAsync("Thierry Henry", "France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Thierry Henry"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ203_ScoreSubmissionAsync_NoCandidateWithThatName_ReturnsIncorrectWithNullPlayerAnswerId()
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        await SeedPlayerAsync("Thierry Henry", "France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Someone Else"));

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(result.PlayerAnswerId, Is.Null);
    }

    [Test]
    public async Task REQ203_ScoreSubmissionAsync_CandidateSatisfiesOnlyRowCategory_ReturnsIncorrect()
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        // Right nationality, wrong club — must satisfy BOTH the row and
        // column categories, not just one.
        await SeedPlayerAsync("Thierry Henry", "France", "Barcelona");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Thierry Henry"));

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ203_ScoreSubmissionAsync_OverridePresent_WinsOverConflictingCachedPlayerAttribute_EndToEnd()
    {
        // Cached (unverified) data says Barcelona, but an admin override for
        // the same field corrects it to Arsenal — the override must be what
        // guess-checking sees, exercised here through the full
        // ScoreSubmissionAsync path (unit-level coverage of the same rule
        // lives in XGArcade.Data.Tests/PlayerStoreRepositoryTests).
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var player = await SeedPlayerAsync("Thierry Henry", "France", "Barcelona");
        await _playerStoreRepository.AddOverrideAsync(new PlayerOverride
        {
            Id = Guid.NewGuid(),
            PlayerId = player.Id,
            Field = "club",
            Value = "Arsenal",
            Reason = "Manual correction",
            LockedByAdminId = Guid.NewGuid(),
            LockedAt = DateTime.UtcNow,
        });
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Thierry Henry"));

        Assert.That(result.IsCorrect, Is.True, "the override must be effective even though the cached PlayerAttribute alone would fail the club category");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    // ---- REQ-211: guess-time live verification (Tier 0 simplified) --------
    // Reproduces the reported bug: grid generation's cache-based validity
    // check (REQ-101/MinValidAnswers) only ever needs to prove a cell has
    // *some* cached matches, never to catalog every one — ADR-0010's
    // documented gap. A genuinely correct player (e.g. Messi for
    // Barcelona x Argentina) can have no PlayerAttribute data at all for
    // this specific cell and get wrongly marked incorrect, even though a
    // live Wikidata lookup would confirm the guess.

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        SeedCountry("Argentina");
        SeedClub("Barcelona");
        var (instanceId, cellId) = await SeedGridInstanceAsync("Argentina", "Barcelona");
        // Some other player already satisfies this cell in the cache — this
        // is what let grid generation accept the pairing in the first place
        // (REQ-101) — but the guessed player himself was never synced, so
        // nothing cached confirms or denies him.
        await SeedPlayerAsync("Javier Mascherano", "Argentina", "Barcelona");
        var messi = new Player { Id = Guid.NewGuid(), FullName = "Lionel Messi", WikidataQid = "Qmessi" };
        _wikidataLookupService.SetMatches("Argentina", "Barcelona", [messi]);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Lionel Messi"));

        Assert.That(result.IsCorrect, Is.True,
            "a live Wikidata lookup must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(messi.Id));
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_LiveLookupFallback_NeverTriggeredWhenCachedDataAlreadyAnswersTheGuess()
    {
        // The fallback must be narrow (ADR-0010) — a guess that already
        // resolves from cached data must never trigger a live call at all.
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var player = await SeedPlayerAsync("Thierry Henry", "France", "Arsenal");
        // Configured but must never be consulted, since the cache already
        // answers this guess correctly.
        _wikidataLookupService.SetMatches("France", "Arsenal", [new Player { Id = Guid.NewGuid(), FullName = "Someone Else", WikidataQid = "Qother" }]);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Thierry Henry"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_GenuinelyIncorrectGuess_LiveLookupFindsNoMatch_StaysIncorrect()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        // No matches configured on the fake at all — mirrors a genuine
        // Wikidata no-match, not merely an untried combination.
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Nicolas Anelka"));

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_GenuinelyIncorrectGuess_LiveLookupFindsNoMatch_OnlyCallsLiveLookupOnce()
    {
        // ADR-0018: the fallback is a single re-run, never a loop/recursion —
        // bounded by REQ-210's 2-attempts-per-cell cap, same as every other
        // guess-time cost. Even when the re-run still can't answer the
        // guess, LookupAndPersistAsync must be invoked exactly once for this
        // cell's country/club pair, not retried further within the same call.
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Nicolas Anelka"));

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_PlayerAlreadyCachedFromUnrelatedCell_LiveLookupFillsOnlyMissingCategory()
    {
        // The bug report's exact repro shape (ADR-0018): the guessed player
        // is not new to the store — they already have this cell's ROW
        // category (nationality) cached from an entirely unrelated
        // country/club pairing (e.g. a different club cell for the same
        // country) — but nothing yet confirms this cell's COLUMN category
        // (club). This must be distinguished from "player doesn't exist at
        // all yet": the live lookup's upsert (by WikidataQid) must find the
        // existing player row and add only the missing club attribute,
        // never create a duplicate Player.
        SeedCountry("Argentina");
        SeedClub("Barcelona");
        var (instanceId, cellId) = await SeedGridInstanceAsync("Argentina", "Barcelona");
        var messi = new Player { Id = Guid.NewGuid(), FullName = "Lionel Messi", WikidataQid = "Qmessi" };
        _dbContext.Players.Add(messi);
        // Cached from some other cell (e.g. Argentina x PSG) — confirms the
        // row category alone, nothing about this cell's club.
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = messi.Id, AttributeType = "nationality", AttributeValue = "Argentina" });
        await _dbContext.SaveChangesAsync();
        _wikidataLookupService.SetMatches("Argentina", "Barcelona", [messi]);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Lionel Messi"));

        Assert.That(result.IsCorrect, Is.True,
            "a live lookup must resolve a player who already exists with one category cached from an unrelated cell, " +
            "not just a player who is entirely new to the store");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(messi.Id));
        Assert.That(await _dbContext.Players.CountAsync(p => p.WikidataQid == "Qmessi"), Is.EqualTo(1),
            "the live lookup upserts by WikidataQid — it must never create a duplicate Player row for a player already known");
        Assert.That(await _playerStoreRepository.HasEffectiveAttributeAsync(messi.Id, "club", "Barcelona"), Is.True);
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_ClubClubCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        // S-030: RefreshCellFromLiveLookupAsync's Club x Club branch — same
        // reproduction shape as the Country x Club test above, but for a
        // cell whose row AND column are both category type "club".
        SeedClub("Barcelona");
        SeedClub("Paris Saint-Germain");
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "Barcelona", "Paris Saint-Germain",
            rowCategoryType: CategoryPairingRules.Club, colCategoryType: CategoryPairingRules.Club);
        // Some other player already satisfies this Club x Club cell in the
        // cache — this is what let grid generation accept the pairing in
        // the first place (REQ-101) — but the guessed player himself was
        // never synced, so nothing cached confirms or denies him.
        var otherPlayer = new Player { Id = Guid.NewGuid(), FullName = "Some Other Player", WikidataQid = "Qother-clubclub" };
        _dbContext.Players.Add(otherPlayer);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = otherPlayer.Id, AttributeType = "club", AttributeValue = "Barcelona" });
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = otherPlayer.Id, AttributeType = "club", AttributeValue = "Paris Saint-Germain" });
        await _dbContext.SaveChangesAsync();
        var neymar = new Player { Id = Guid.NewGuid(), FullName = "Neymar Jr", WikidataQid = "Qneymar" };
        _wikidataLookupService.SetClubClubMatches("Barcelona", "Paris Saint-Germain", [neymar]);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Neymar Jr"));

        Assert.That(result.IsCorrect, Is.True,
            "a live Wikidata Club x Club lookup must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(neymar.Id));
        Assert.That(_wikidataLookupService.GetClubClubCallCount("Barcelona", "Paris Saint-Germain"), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_CellCategoryTypeUnhandledByFallback_SkipsLiveLookup_DoesNotThrow()
    {
        // RefreshCellFromLiveLookupAsync's guard: Tier 0's live fallback
        // knows how to re-run Country(rows) x Club(cols) (S-007) and, as of
        // S-030, Club x Club — but the mirrored Club(rows) x Country(cols)
        // shape (never produced by GenerateInstanceAsync's SelectPairing,
        // but not otherwise impossible for a cell to have) isn't
        // special-cased either, and must gracefully skip the fallback (stay
        // incorrect) rather than throw.
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "SomeRow", "SomeCol", rowCategoryType: CategoryPairingRules.Club, colCategoryType: CategoryPairingRules.Country);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        ScoreResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Anyone")));

        Assert.That(result!.IsCorrect, Is.False);
        Assert.That(_wikidataLookupService.GetCallCount("SomeRow", "SomeCol"), Is.EqualTo(0),
            "the live lookup must never be called for a pairing the fallback doesn't know how to refresh");
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_RowCategoryValueNotInReferenceTable_SkipsLiveLookup_DoesNotThrow()
    {
        // RefreshCellFromLiveLookupAsync's guard: a RowCategoryValue with no
        // matching seeded CountryDefinition (shouldn't happen in practice,
        // since grid generation only ever picks from that table — REQ-109)
        // must still fail closed rather than throw.
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("Wakanda", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        ScoreResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Anyone")));

        Assert.That(result!.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_ColCategoryValueNotInReferenceTable_SkipsLiveLookup_DoesNotThrow()
    {
        // Same guard as above, for the column/club side.
        SeedCountry("France");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "PhantomClub");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        ScoreResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Anyone")));

        Assert.That(result!.IsCorrect, Is.False);
    }

    [Test]
    public void ScoreSubmissionAsync_UnknownInstanceId_ThrowsGuessScoringException()
    {
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        Assert.ThrowsAsync<GuessScoringException>(async () =>
            await module.ScoreSubmissionAsync(Guid.NewGuid(), Guid.NewGuid(), new GuessSubmission(Guid.NewGuid(), "Anyone")));
    }

    [Test]
    public async Task ScoreSubmissionAsync_UnknownCellId_ThrowsGuessScoringException()
    {
        var (instanceId, _) = await SeedGridInstanceAsync("France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        Assert.ThrowsAsync<GuessScoringException>(async () =>
            await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(Guid.NewGuid(), "Anyone")));
    }

    // ---- REQ-208: name normalization and matching --------------------------

    [TestCase("Kaká", "Kaka", TestName = "REQ208_ScoreSubmissionAsync_DiacriticsIgnored")]
    [TestCase("thierry henry", "Thierry Henry", TestName = "REQ208_ScoreSubmissionAsync_CaseIgnored")]
    [TestCase("Thierry   Henry", "Thierry Henry", TestName = "REQ208_ScoreSubmissionAsync_ExtraWhitespaceIgnored")]
    [TestCase("  Thierry Henry  ", "Thierry Henry", TestName = "REQ208_ScoreSubmissionAsync_LeadingAndTrailingWhitespaceIgnored")]
    public async Task REQ208_ScoreSubmissionAsync_NormalizedVariant_StillMatches(string submittedName, string storedFullName)
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var player = await SeedPlayerAsync(storedFullName, "France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, submittedName));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ208_ScoreSubmissionAsync_GenuinelyDifferentName_DoesNotMatch()
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        await SeedPlayerAsync("Thierry Henry", "France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Nicolas Anelka"));

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ208_ScoreSubmissionAsync_AliasOnlyMatch_DoesNotMatch_TierZeroScopeBoundary()
    {
        // Tier 0 explicitly defers the alias table for matching purposes
        // (MVP-SCOPE.md, "defer the alias table and fuzzy typo tolerance") —
        // guess-time matching queries only Player.NormalizedFullName
        // (GetPlayersByNormalizedFullNameAsync), never PlayerAlias. A guess
        // that only matches a recorded alias, with no exact FullName match,
        // is therefore NOT found in Tier 0, even though the alias itself
        // exists in the data. This documents that scope boundary rather than
        // asserting REQ-208's full "known aliases/stage names" criterion,
        // which is not yet implemented.
        var (instanceId, cellId) = await SeedGridInstanceAsync("Brazil", "AC Milan");
        var player = await SeedPlayerAsync("Ricardo Izecson dos Santos Leite", "Brazil", "AC Milan");
        await _playerStoreRepository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = player.Id, Alias = "Kaka", NormalizedAlias = "kaka" });
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Kaka"));

        Assert.That(result.IsCorrect, Is.False, "Tier 0 matches only Player.FullName, not PlayerAlias — see MVP-SCOPE.md");
    }

    // ---- REQ-209: disambiguating multiple players with a matching name -----
    // (Tier 0 simplified per MVP-SCOPE.md: no disambiguation prompt — any
    // fitting candidate is accepted, deterministically the lowest Id.)

    [Test]
    public async Task REQ209_ScoreSubmissionAsync_ExactlyOneCandidateSatisfiesBothCategories_AcceptedAutomatically()
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var fittingPlayer = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        // Same name, but doesn't satisfy the cell's categories — the
        // categories themselves must disambiguate.
        await SeedPlayerAsync("John Smith", "England", "Chelsea");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "John Smith"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(fittingPlayer.Id));
    }

    [Test]
    public async Task REQ209_ScoreSubmissionAsync_MultipleCandidatesSatisfyBothCategories_AcceptsDeterministicallyLowestId()
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var first = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        var second = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        var expected = new[] { first, second }.OrderBy(p => p.Id).First();
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "John Smith"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(expected.Id),
            "REQ-204's deterministic-pick rule: the lowest Id among fitting candidates is always chosen");
    }

    [Test]
    public async Task REQ209_ScoreSubmissionAsync_NoCandidateSatisfiesBothCategories_ReturnsIncorrect_RegardlessOfSameNamedPlayersElsewhere()
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        await SeedPlayerAsync("John Smith", "England", "Chelsea");
        await SeedPlayerAsync("John Smith", "Spain", "Barcelona");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "John Smith"));

        Assert.That(result.IsCorrect, Is.False);
    }
}

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
        IWikidataLookupService? wikidataLookupService = null,
        IPlayerStoreRepository? playerStoreRepository = null) =>
        new(_gridInstanceRepository, _categoryValueRepository, playerStoreRepository ?? _playerStoreRepository, wikidataLookupService ?? _wikidataLookupService,
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

    private CountryDefinition SeedCountry(string name, string? wikidataQid = "unset", bool usesCountryForSportProperty = false)
    {
        var country = new CountryDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            WikidataQid = wikidataQid == "unset" ? $"Qcountry-{name}" : wikidataQid,
            // REQ-114/ADR-0035.
            UsesCountryForSportProperty = usesCountryForSportProperty,
        };
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

    // S-031/REQ-108.
    private TrophyDefinition SeedTrophy(string name, string? wikidataQid = "unset")
    {
        var trophy = new TrophyDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid == "unset" ? $"Qtrophy-{name}" : wikidataQid };
        _dbContext.TrophyDefinitions.Add(trophy);
        _dbContext.SaveChanges();
        return trophy;
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

    // S-031: Trophy x Country counterpart to SeedCachedMatches — one side is
    // AttributeType "trophy", the other "nationality".
    private void SeedCachedTrophyCountryMatches(string trophyName, string countryName, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var player = new Player
            {
                Id = Guid.NewGuid(),
                FullName = $"{trophyName}-{countryName}-Player{i}",
                WikidataQid = $"Qplayer-{trophyName}-{countryName}-{i}",
            };
            _dbContext.Players.Add(player);
            _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "trophy", AttributeValue = trophyName });
            _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = countryName });
        }
        _dbContext.SaveChanges();
    }

    // S-031: Trophy x Club counterpart to SeedCachedMatches — one side is
    // AttributeType "trophy", the other "club".
    private void SeedCachedTrophyClubMatches(string trophyName, string clubName, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var player = new Player
            {
                Id = Guid.NewGuid(),
                FullName = $"{trophyName}-{clubName}-Player{i}",
                WikidataQid = $"Qplayer-{trophyName}-{clubName}-{i}",
            };
            _dbContext.Players.Add(player);
            _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "trophy", AttributeValue = trophyName });
            _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = clubName });
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
        // ADR-0029: a generation-time cache-miss is a routine sync, trusted
        // as ground truth — distinct from REQ-211's guess-time fallback,
        // which stays reviewable (see GridGameModuleTests' REQ211_* tests).
        Assert.That(_wikidataLookupService.GetLastOrigin("France", "Arsenal"), Is.EqualTo(WikidataLookupOrigin.Sync));
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

    // ---- REQ-108/S-031: Trophy category ------------------------------------
    // Production only ever seeds one trophy (Ballon d'Or, ReferenceDataSeeder)
    // — trophyCount(1) can never clear `size` for any realistic grid, so a
    // Trophy pairing structurally never gets selected in production (see
    // SelectPairing's own comment). Tests below inject a larger fake trophy
    // pool (SeedTrophy, 3+ values) specifically to prove the mechanism itself
    // works even though production data won't trigger it yet.

    [Test]
    public async Task REQ108_GenerateInstanceAsync_TrophyCountryPairing_ProducesGridUsingTrophyCategoryType()
    {
        // Zero clubs seeded -> every Club-involving pairing is infeasible.
        // Three trophies (>= size but < 2*size) makes Trophy x Trophy
        // infeasible too, leaving Country x Trophy as the only feasible
        // pairing — deterministic regardless of the injected Random.
        var template = SeedTemplate(size: 2);
        SeedCountry("France");
        SeedCountry("Spain");
        var trophyNames = Enumerable.Range(0, 3).Select(i => $"Trophy{i}").ToList();
        foreach (var trophyName in trophyNames)
            SeedTrophy(trophyName);
        foreach (var countryName in new[] { "France", "Spain" })
            foreach (var trophyName in trophyNames)
                SeedCachedTrophyCountryMatches(trophyName, countryName, count: 2);
        var module = BuildModule(minValidAnswers: 2, maxAttempts: 20);

        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(4));
        Assert.That(instance.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Country && c.ColCategoryType == CategoryPairingRules.Trophy),
            "SelectPairing must have picked Country x Trophy — Trophy always second, per the Country/Club-first precedent");
        var rowValues = instance.Cells.Select(c => c.RowCategoryValue).Distinct().ToList();
        var colValues = instance.Cells.Select(c => c.ColCategoryValue).Distinct().ToList();
        Assert.That(rowValues, Has.Count.EqualTo(2), "REQ-102: N unique row categories");
        Assert.That(colValues, Has.Count.EqualTo(2), "REQ-102: N unique column categories");
    }

    [Test]
    public async Task REQ108_GenerateInstanceAsync_TrophyClubPairing_ProducesGridUsingTrophyCategoryType()
    {
        // Zero countries seeded -> every Country-involving pairing is
        // infeasible. Three trophies (>= size but < 2*size) makes
        // Trophy x Trophy infeasible too, leaving Club x Trophy as the only
        // feasible pairing — deterministic regardless of the injected Random.
        var template = SeedTemplate(size: 2);
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var trophyNames = Enumerable.Range(0, 3).Select(i => $"Trophy{i}").ToList();
        foreach (var trophyName in trophyNames)
            SeedTrophy(trophyName);
        foreach (var clubName in new[] { "Arsenal", "Barcelona" })
            foreach (var trophyName in trophyNames)
                SeedCachedTrophyClubMatches(trophyName, clubName, count: 2);
        var module = BuildModule(minValidAnswers: 2, maxAttempts: 20);

        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(4));
        Assert.That(instance.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Club && c.ColCategoryType == CategoryPairingRules.Trophy),
            "SelectPairing must have picked Club x Trophy — Trophy always second, per the Country/Club-first precedent");
        var rowValues = instance.Cells.Select(c => c.RowCategoryValue).Distinct().ToList();
        var colValues = instance.Cells.Select(c => c.ColCategoryValue).Distinct().ToList();
        Assert.That(rowValues, Has.Count.EqualTo(2), "REQ-102: N unique row categories");
        Assert.That(colValues, Has.Count.EqualTo(2), "REQ-102: N unique column categories");
    }

    [Test]
    public async Task REQ108_SelectPairing_OnlyOneTrophySeeded_MatchingRealSeedData_NeverSelectsAnyTrophyPairing()
    {
        // The real ReferenceDataSeeder shape: exactly one trophy (Ballon
        // d'Or). With size >= 2, trophyCount(1) can never clear `size` for
        // any mixed pairing, nor `size * 2` for Trophy x Trophy — so every
        // Trophy pairing is infeasible and Country x Club is the only
        // choice, regardless of the injected Random. This documents S-031's
        // "structurally dormant in production" consequence as an asserted
        // behavior, not just a code comment.
        var template = SeedTemplate(size: 2);
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedTrophy("Ballon d'Or");
        SeedCachedMatches("France", "Arsenal", 2);
        SeedCachedMatches("France", "Barcelona", 2);
        SeedCachedMatches("Spain", "Arsenal", 2);
        SeedCachedMatches("Spain", "Barcelona", 2);
        var module = BuildModule(minValidAnswers: 2, maxAttempts: 20);

        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.None.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Trophy || c.ColCategoryType == CategoryPairingRules.Trophy),
            "with only one trophy seeded (matching real seed data), Trophy can never be selected for any realistic grid size");
    }

    [Test]
    public async Task REQ108_ScoreSubmissionAsync_TrophyCountryCell_CandidateSatisfiesBothCategories_ReturnsCorrect()
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "France", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Country, colCategoryType: CategoryPairingRules.Trophy);
        var player = new Player { Id = Guid.NewGuid(), FullName = "Zinedine Zidane", WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        _dbContext.Players.Add(player);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" });
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "trophy", AttributeValue = "Ballon d'Or" });
        await _dbContext.SaveChangesAsync();
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Zinedine Zidane"));

        Assert.That(result.IsCorrect, Is.True, "a PlayerAttribute record of type 'trophy' must satisfy a Trophy category cell");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ108_ScoreSubmissionAsync_TrophyClubCell_CandidateSatisfiesBothCategories_ReturnsCorrect()
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "Real Madrid", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Club, colCategoryType: CategoryPairingRules.Trophy);
        var player = new Player { Id = Guid.NewGuid(), FullName = "Luka Modric", WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        _dbContext.Players.Add(player);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Real Madrid" });
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "trophy", AttributeValue = "Ballon d'Or" });
        await _dbContext.SaveChangesAsync();
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Luka Modric"));

        Assert.That(result.IsCorrect, Is.True, "a PlayerAttribute record of type 'trophy' must satisfy a Trophy category cell");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ108_ScoreSubmissionAsync_TrophyCell_PlayerLacksTrophyAttribute_ReturnsIncorrect()
    {
        // Right nationality, but no "trophy"/"Ballon d'Or" PlayerAttribute —
        // must satisfy BOTH categories, not just the non-Trophy one.
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "France", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Country, colCategoryType: CategoryPairingRules.Trophy);
        var player = new Player { Id = Guid.NewGuid(), FullName = "Some Frenchman", WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        _dbContext.Players.Add(player);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" });
        await _dbContext.SaveChangesAsync();
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Some Frenchman"));

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ108_ScoreSubmissionAsync_TrophyOverride_WinsOverConflictingCachedPlayerAttribute()
    {
        // Mirrors REQ203_ScoreSubmissionAsync_OverridePresent_WinsOverConflictingCachedPlayerAttribute_EndToEnd
        // for the Trophy category — "a PlayerAttribute (or override) record
        // of type trophy" (REQ-108's acceptance text) explicitly includes
        // PlayerOverride, not just the raw cached attribute.
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "France", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Country, colCategoryType: CategoryPairingRules.Trophy);
        var player = new Player { Id = Guid.NewGuid(), FullName = "Zinedine Zidane", WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        _dbContext.Players.Add(player);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" });
        // Cached (unverified) data has no trophy attribute at all — an
        // admin override supplies it instead.
        await _dbContext.SaveChangesAsync();
        await _playerStoreRepository.AddOverrideAsync(new PlayerOverride
        {
            Id = Guid.NewGuid(),
            PlayerId = player.Id,
            Field = "trophy",
            Value = "Ballon d'Or",
            Reason = "Manual correction",
            LockedByAdminId = Guid.NewGuid(),
            LockedAt = DateTime.UtcNow,
        });
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Zinedine Zidane"));

        Assert.That(result.IsCorrect, Is.True, "the override must be effective even though nothing cached confirms the trophy category");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_TrophyCountryCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        SeedCountry("France");
        SeedTrophy("Ballon d'Or");
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "France", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Country, colCategoryType: CategoryPairingRules.Trophy);
        var zidane = new Player { Id = Guid.NewGuid(), FullName = "Zinedine Zidane", WikidataQid = "Qzidane" };
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "France", [zidane]);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Zinedine Zidane"));

        Assert.That(result.IsCorrect, Is.True,
            "a live Wikidata Trophy x Country lookup must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(zidane.Id));
        Assert.That(_wikidataLookupService.GetTrophyCountryCallCount("Ballon d'Or", "France"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetTrophyCountryLastOrigin("Ballon d'Or", "France"), Is.EqualTo(WikidataLookupOrigin.GuessTimeFallback));
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_TrophyClubCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        SeedClub("Real Madrid");
        SeedTrophy("Ballon d'Or");
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "Real Madrid", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Club, colCategoryType: CategoryPairingRules.Trophy);
        var modric = new Player { Id = Guid.NewGuid(), FullName = "Luka Modric", WikidataQid = "Qmodric" };
        _wikidataLookupService.SetTrophyClubMatches("Ballon d'Or", "Real Madrid", [modric]);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Luka Modric"));

        Assert.That(result.IsCorrect, Is.True,
            "a live Wikidata Trophy x Club lookup must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(modric.Id));
        Assert.That(_wikidataLookupService.GetTrophyClubCallCount("Ballon d'Or", "Real Madrid"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetTrophyClubLastOrigin("Ballon d'Or", "Real Madrid"), Is.EqualTo(WikidataLookupOrigin.GuessTimeFallback));
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_TrophyTrophyCell_UnhandledByFallback_SkipsLiveLookup_DoesNotThrow()
    {
        // Trophy x Trophy has no dedicated IWikidataLookupService method
        // (never generated in practice — see SelectPairing's own comment —
        // but not otherwise impossible for a cell to have) and must
        // gracefully skip the fallback (stay incorrect) rather than throw,
        // same guard as the existing Club(rows) x Country(cols) test above.
        SeedTrophy("Ballon d'Or");
        SeedTrophy("Golden Boot");
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "Ballon d'Or", "Golden Boot", rowCategoryType: CategoryPairingRules.Trophy, colCategoryType: CategoryPairingRules.Trophy);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        ScoreResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Anyone")));

        Assert.That(result!.IsCorrect, Is.False);
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
        // The GuessTimeFallback origin is still passed through distinctly
        // from a generation-time Sync (for logging/future
        // re-differentiation, ADR-0032) even though, as of ADR-0032, both
        // now persist the same Confidence — see
        // REQ211_LookupAndPersistAsync_GuessTimeFallback_PersistsAsVerified
        // (WikidataLookupServiceTests.cs) for the actual Confidence
        // assertion; this only confirms GridGameModule passes the right
        // origin through.
        Assert.That(_wikidataLookupService.GetLastOrigin("France", "Arsenal"), Is.EqualTo(WikidataLookupOrigin.GuessTimeFallback));
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
        // Same origin check as the Country x Club fallback test above.
        Assert.That(
            _wikidataLookupService.GetClubClubLastOrigin("Barcelona", "Paris Saint-Germain"),
            Is.EqualTo(WikidataLookupOrigin.GuessTimeFallback));
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

    // Thin call-counting wrapper around the real, InMemory-backed
    // IPlayerStoreRepository (never a hand-rolled reimplementation of its
    // behavior — every method just delegates) used only to verify REQ-208's
    // "exact match first, then alias, then fuzzy — fuzzy only runs when the
    // first two produced nothing" ordering: the alias/fuzzy repository
    // calls must never happen once an earlier stage already resolved a fit.
    private sealed class CallCountingPlayerStoreRepository(IPlayerStoreRepository inner) : IPlayerStoreRepository
    {
        public int GetPlayersByNormalizedAliasAsyncCallCount { get; private set; }
        public int GetPlayersWithEitherAttributeAsyncCallCount { get; private set; }

        public Task<Player?> GetPlayerByWikidataQidAsync(string wikidataQid, CancellationToken cancellationToken = default) =>
            inner.GetPlayerByWikidataQidAsync(wikidataQid, cancellationToken);

        public Task<Player?> GetPlayerByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetPlayerByIdAsync(id, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, Player>> GetPlayersByIdsAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            inner.GetPlayersByIdsAsync(ids, cancellationToken);

        public Task<Player> AddPlayerAsync(Player player, CancellationToken cancellationToken = default) =>
            inner.AddPlayerAsync(player, cancellationToken);

        public Task<IReadOnlyList<Player>> GetPlayersByNormalizedFullNameAsync(
            string normalizedFullName, CancellationToken cancellationToken = default) =>
            inner.GetPlayersByNormalizedFullNameAsync(normalizedFullName, cancellationToken);

        public Task<IReadOnlyList<Player>> GetPlayersByNormalizedAliasAsync(
            string normalizedAlias, CancellationToken cancellationToken = default)
        {
            GetPlayersByNormalizedAliasAsyncCallCount++;
            return inner.GetPlayersByNormalizedAliasAsync(normalizedAlias, cancellationToken);
        }

        public Task<IReadOnlyList<Player>> GetPlayersWithEitherAttributeAsync(
            string firstAttributeType, string firstAttributeValue,
            string secondAttributeType, string secondAttributeValue,
            CancellationToken cancellationToken = default)
        {
            GetPlayersWithEitherAttributeAsyncCallCount++;
            return inner.GetPlayersWithEitherAttributeAsync(
                firstAttributeType, firstAttributeValue, secondAttributeType, secondAttributeValue, cancellationToken);
        }

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAlias>>> GetPlayerAliasesByPlayerIdsAsync(
            IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default) =>
            inner.GetPlayerAliasesByPlayerIdsAsync(playerIds, cancellationToken);

        public Task AddPlayerDataAsync(PlayerData data, CancellationToken cancellationToken = default) =>
            inner.AddPlayerDataAsync(data, cancellationToken);

        public Task<IReadOnlyList<PlayerData>> GetUnverifiedPlayerDataAsync(CancellationToken cancellationToken = default) =>
            inner.GetUnverifiedPlayerDataAsync(cancellationToken);

        public Task<IReadOnlyList<PlayerDataApprovalOutcome>> ApprovePlayerDataAsync(
            IReadOnlyCollection<Guid> playerDataIds, Guid adminId, CancellationToken cancellationToken = default) =>
            inner.ApprovePlayerDataAsync(playerDataIds, adminId, cancellationToken);

        public Task<IReadOnlyList<PlayerDataRemovalOutcome>> RemovePlayerDataAsync(
            IReadOnlyCollection<Guid> playerDataIds, CancellationToken cancellationToken = default) =>
            inner.RemovePlayerDataAsync(playerDataIds, cancellationToken);

        public Task<IReadOnlyList<PlayerAttribute>> GetPlayerAttributesAsync(
            string attributeType, string attributeValue, CancellationToken cancellationToken = default) =>
            inner.GetPlayerAttributesAsync(attributeType, attributeValue, cancellationToken);

        public Task AddPlayerAttributeAsync(PlayerAttribute attribute, CancellationToken cancellationToken = default) =>
            inner.AddPlayerAttributeAsync(attribute, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAttribute>>> GetPlayerAttributesByPlayerIdsAsync(
            IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default) =>
            inner.GetPlayerAttributesByPlayerIdsAsync(playerIds, cancellationToken);

        public Task<int> CountPlayersWithBothAttributesAsync(
            string firstAttributeType, string firstAttributeValue,
            string secondAttributeType, string secondAttributeValue,
            CancellationToken cancellationToken = default) =>
            inner.CountPlayersWithBothAttributesAsync(firstAttributeType, firstAttributeValue, secondAttributeType, secondAttributeValue, cancellationToken);

        public Task<IReadOnlyList<PlayerAlias>> GetPlayerAliasesAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            inner.GetPlayerAliasesAsync(playerId, cancellationToken);

        public Task AddPlayerAliasAsync(PlayerAlias alias, CancellationToken cancellationToken = default) =>
            inner.AddPlayerAliasAsync(alias, cancellationToken);

        public Task<PlayerOverride?> GetOverrideAsync(Guid playerId, string field, CancellationToken cancellationToken = default) =>
            inner.GetOverrideAsync(playerId, field, cancellationToken);

        public Task<PlayerOverride?> GetOverrideByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetOverrideByIdAsync(id, cancellationToken);

        public Task AddOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default) =>
            inner.AddOverrideAsync(playerOverride, cancellationToken);

        public Task UpdateOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default) =>
            inner.UpdateOverrideAsync(playerOverride, cancellationToken);

        public Task<bool> DeleteOverrideAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.DeleteOverrideAsync(id, cancellationToken);

        public Task<bool> HasEffectiveAttributeAsync(
            Guid playerId, string attributeType, string attributeValue, CancellationToken cancellationToken = default) =>
            inner.HasEffectiveAttributeAsync(playerId, attributeType, attributeValue, cancellationToken);

        public Task<IReadOnlyList<Player>> GetPlayersMissingPhotoAsync(
            IReadOnlyCollection<Guid> excludingPlayerIds, int batchSize, CancellationToken cancellationToken = default) =>
            inner.GetPlayersMissingPhotoAsync(excludingPlayerIds, batchSize, cancellationToken);

        public Task UpdatePlayerPhotosAsync(
            IReadOnlyDictionary<Guid, string> photoUrlByPlayerId, CancellationToken cancellationToken = default) =>
            inner.UpdatePlayerPhotosAsync(photoUrlByPlayerId, cancellationToken);
    }

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
    public async Task REQ208_ScoreSubmissionAsync_AliasExactMatch_ScoresCorrect()
    {
        // Known aliases/stage names are matched via PlayerAlias, not just
        // the primary name field — a guess that only matches a recorded
        // alias, with no exact Player.FullName match, must still score
        // correct if that player fits the cell's categories.
        var (instanceId, cellId) = await SeedGridInstanceAsync("Brazil", "AC Milan");
        var player = await SeedPlayerAsync("Ricardo Izecson dos Santos Leite", "Brazil", "AC Milan");
        await _playerStoreRepository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = player.Id, Alias = "Kaka", NormalizedAlias = "kaka" });
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Kaka"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ208_ScoreSubmissionAsync_AliasMatch_RequiresCategoryFit_JustLikeAPrimaryNameMatch()
    {
        // An alias match is handled by exactly the same category-fit check
        // as a primary-name match (REQ-203/REQ-209) — an alias belonging to
        // a player who doesn't satisfy this cell's categories must not score
        // correct just because the name string matched.
        var (instanceId, cellId) = await SeedGridInstanceAsync("Brazil", "AC Milan");
        var player = await SeedPlayerAsync("Ricardo Izecson dos Santos Leite", "England", "Chelsea");
        await _playerStoreRepository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = player.Id, Alias = "Kaka", NormalizedAlias = "kaka" });
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Kaka"));

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ208_ScoreSubmissionAsync_ExactPrimaryNameMatch_AliasAndFuzzyStagesNeverConsulted()
    {
        // REQ-208's ordering: exact match first, then alias, then fuzzy —
        // the alias/fuzzy repository calls must never happen once the exact
        // primary-name stage already resolved a fit. A distinct player whose
        // name is one edit away from the guess (would fuzzy-match if the
        // fuzzy stage ran) is deliberately seeded to prove this isn't just
        // "no alias/fuzzy data exists to find."
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var exactPlayer = await SeedPlayerAsync("Henry", "France", "Arsenal");
        await SeedPlayerAsync("Henri", "France", "Arsenal"); // distance 1 from "henry" — would fuzzy-match if reached
        var spyRepository = new CallCountingPlayerStoreRepository(_playerStoreRepository);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5, playerStoreRepository: spyRepository);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Henry"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(exactPlayer.Id));
        Assert.That(spyRepository.GetPlayersByNormalizedAliasAsyncCallCount, Is.EqualTo(0),
            "the alias stage must never be consulted once the exact primary-name stage already resolved a fit");
        Assert.That(spyRepository.GetPlayersWithEitherAttributeAsyncCallCount, Is.EqualTo(0),
            "the fuzzy stage must never be consulted once the exact primary-name stage already resolved a fit");
    }

    [Test]
    public async Task REQ208_ScoreSubmissionAsync_AliasMatch_FuzzyStageNeverConsulted()
    {
        // Same ordering guarantee as above, one stage later: once the alias
        // stage resolves a fit, the fuzzy stage must never run either.
        var (instanceId, cellId) = await SeedGridInstanceAsync("Brazil", "AC Milan");
        var aliasPlayer = await SeedPlayerAsync("Ricardo Izecson dos Santos Leite", "Brazil", "AC Milan");
        await _playerStoreRepository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = aliasPlayer.Id, Alias = "Kaka", NormalizedAlias = "kaka" });
        var spyRepository = new CallCountingPlayerStoreRepository(_playerStoreRepository);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5, playerStoreRepository: spyRepository);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Kaka"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(spyRepository.GetPlayersByNormalizedAliasAsyncCallCount, Is.EqualTo(1),
            "the alias stage must be consulted once the exact primary-name stage found nothing");
        Assert.That(spyRepository.GetPlayersWithEitherAttributeAsyncCallCount, Is.EqualTo(0),
            "the fuzzy stage must never be consulted once the alias stage already resolved a fit");
    }

    [TestCase("Zidane", "Zidan", TestName = "REQ208_ScoreSubmissionAsync_FuzzyTypo_SingleDroppedLetter_MatchesViaPrimaryName")]
    [TestCase("Ronaldinho", "Ronaldinoh", TestName = "REQ208_ScoreSubmissionAsync_FuzzyTypo_TrailingTransposition_MatchesViaPrimaryName_LongerName")]
    [TestCase("Zinedine Zidane", "Zinedine Zidence", TestName = "REQ208_ScoreSubmissionAsync_FuzzyTypo_ExactlyAtThreshold_Matches")]
    public async Task REQ208_ScoreSubmissionAsync_FuzzyTypo_MatchesViaPrimaryName(string storedFullName, string submittedName)
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var player = await SeedPlayerAsync(storedFullName, "France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, submittedName));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ208_ScoreSubmissionAsync_FuzzyTypo_MatchesViaAlias()
    {
        // A typo of a known alias deserves the same tolerance as a typo of
        // the primary name — "Kaeka" is one edit away from the alias "Kaka",
        // not from the player's full legal name.
        var (instanceId, cellId) = await SeedGridInstanceAsync("Brazil", "AC Milan");
        var player = await SeedPlayerAsync("Ricardo Izecson dos Santos Leite", "Brazil", "AC Milan");
        await _playerStoreRepository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = player.Id, Alias = "Kaka", NormalizedAlias = "kaka" });
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Kaeka"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ208_ScoreSubmissionAsync_FuzzyMatch_CandidateMustStillSatisfyBothCategories_DoesNotMatch()
    {
        // The fuzzy pass's bounded candidate pool is "satisfies at least one
        // of the cell's two categories" (never a full-table scan) — but a
        // name being fuzzy-close is not enough on its own: the same
        // both-categories check as every other stage still applies
        // afterwards. This player satisfies the row category (France) only,
        // so a fuzzy name match must not score correct.
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        await SeedPlayerAsync("Zidane", "France", "Chelsea");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Zidan"));

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ208_ScoreSubmissionAsync_SimilarButDistinctPlayerName_DoesNotMatch()
    {
        // "Ronaldo" and "Rivaldo" are two different real players, seven
        // characters each, edit distance 2 apart — this codebase's chosen
        // tolerance for that length tier is 1, so this must NOT match. Guards
        // against an edit-distance threshold loose enough to make guessing
        // trivially easy by accepting a similarly-shaped but wrong name.
        var (instanceId, cellId) = await SeedGridInstanceAsync("Brazil", "Barcelona");
        await SeedPlayerAsync("Rivaldo", "Brazil", "Barcelona");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Ronaldo"));

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ208_ScoreSubmissionAsync_ShortNickname_NoFuzzyToleranceForDistanceOne_DoesNotMatch()
    {
        // Names of 4 normalized characters or fewer get zero fuzzy
        // tolerance — "Pele" and "Dele" (Dele Alli's own nickname) are one
        // edit apart but are two different real players; at this length any
        // fuzzy pass would already have been an exact/alias hit if it were
        // really the same name.
        var (instanceId, cellId) = await SeedGridInstanceAsync("Brazil", "Santos");
        await SeedPlayerAsync("Pele", "Brazil", "Santos");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Dele"));

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ208_ScoreSubmissionAsync_FuzzyTypo_DistanceExceedsThreshold_DoesNotMatch()
    {
        // One edit past this length tier's threshold (2) — "Zinedin
        // Zidence" is distance 3 from "Zinedine Zidane" — must not match,
        // confirming the threshold has a real ceiling rather than silently
        // accepting anything vaguely similar.
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Real Madrid");
        await SeedPlayerAsync("Zinedine Zidane", "France", "Real Madrid");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Zinedin Zidence"));

        Assert.That(result.IsCorrect, Is.False);
    }

    // ---- REQ-209: disambiguating multiple players with a matching name -----

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
        Assert.That(result.DisambiguationCandidates, Is.Null, "a single fitting candidate never needs a disambiguation prompt");
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
        Assert.That(result.DisambiguationCandidates, Is.Null,
            "no candidate satisfying both categories at all is a plain incorrect guess, not a disambiguation case");
    }

    [Test]
    public async Task REQ209_ScoreSubmissionAsync_MultipleCandidatesSatisfyBothCategories_ReturnsDisambiguationCandidates_NotAutoAccepted()
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var first = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        var second = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        // Each candidate also has an "other" club, distinct from the cell's
        // own two categories (France/Arsenal) — these are what should
        // surface as DistinguishingAttributes, never France/Arsenal again.
        await _playerStoreRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = first.Id, AttributeType = "club", AttributeValue = "Monaco" });
        await _playerStoreRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = second.Id, AttributeType = "club", AttributeValue = "Lyon" });
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "John Smith"));

        Assert.That(result.IsCorrect, Is.False, "an ambiguous guess is never auto-accepted on the player's behalf");
        Assert.That(result.PlayerAnswerId, Is.Null);
        Assert.That(result.DisambiguationCandidates, Is.Not.Null.And.Count.EqualTo(2));
        Assert.That(result.DisambiguationCandidates!.Select(c => c.PlayerId), Is.EquivalentTo(new[] { first.Id, second.Id }));
        var firstCandidate = result.DisambiguationCandidates!.Single(c => c.PlayerId == first.Id);
        var secondCandidate = result.DisambiguationCandidates!.Single(c => c.PlayerId == second.Id);
        Assert.That(firstCandidate.Name, Is.EqualTo("John Smith"));
        Assert.That(firstCandidate.DistinguishingAttributes, Is.EquivalentTo(new[] { "Monaco" }),
            "must show the candidate's OTHER attributes, never the cell's own France/Arsenal categories again");
        Assert.That(secondCandidate.DistinguishingAttributes, Is.EquivalentTo(new[] { "Lyon" }));
    }

    [Test]
    public async Task REQ209_ScoreSubmissionAsync_MultipleCandidatesWithNoOtherKnownAttributes_ReturnsEmptyDistinguishingAttributes_NotBlocked()
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var first = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        var second = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "John Smith"));

        Assert.That(result.DisambiguationCandidates, Is.Not.Null.And.Count.EqualTo(2));
        Assert.That(result.DisambiguationCandidates!.Select(c => c.PlayerId), Is.EquivalentTo(new[] { first.Id, second.Id }));
        Assert.That(result.DisambiguationCandidates!, Has.All.Matches<DisambiguationCandidate>(c => c.DistinguishingAttributes.Count == 0),
            "a candidate with no other known attributes must still appear, just with an empty list — never blocking the feature");
    }

    // ---- REQ-209/REQ-210: the ChosenPlayerId resubmission fast path -------

    [Test]
    public async Task REQ209_ScoreSubmissionAsync_ChosenPlayerIdMatchesAFittingCandidate_AcceptsIt()
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var first = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        var second = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(
            instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "John Smith", ChosenPlayerId: second.Id));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(second.Id));
        Assert.That(result.DisambiguationCandidates, Is.Null, "a resolved ChosenPlayerId submission is a real scored guess, not another prompt");
    }

    [Test]
    public async Task REQ209_ScoreSubmissionAsync_ChosenPlayerIdRealPlayerButNoLongerSatisfiesBothCategories_TreatedAsOrdinaryIncorrectGuess_DoesNotThrow()
    {
        // staleChoice is a real player matching the submitted name, but only
        // satisfies ONE of the cell's two categories (e.g. an admin
        // correction landed between the disambiguation prompt and this
        // resubmission) — never trust the client-supplied id blindly, always
        // re-verify server-side.
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var staleChoice = await SeedPlayerAsync("John Smith", "France", "Chelsea");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        ScoreResult? result = null;
        Assert.DoesNotThrowAsync(async () => result = await module.ScoreSubmissionAsync(
            instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "John Smith", ChosenPlayerId: staleChoice.Id)));

        Assert.That(result!.IsCorrect, Is.False);
        Assert.That(result.PlayerAnswerId, Is.Null);
        Assert.That(result.DisambiguationCandidates, Is.Null, "a failed ChosenPlayerId resubmission is a plain incorrect guess, not another prompt");
    }

    [Test]
    public async Task REQ209_ScoreSubmissionAsync_ChosenPlayerIdSuppliedButNothingMatchesAtAll_TreatedAsOrdinaryIncorrectGuess()
    {
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(
            instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Nobody At All", ChosenPlayerId: Guid.NewGuid()));

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(result.DisambiguationCandidates, Is.Null);
    }

    // ---- REQ-114/ADR-0035: national teams as distinct footballing entities

    [Test]
    public async Task REQ114_GenerateInstanceAsync_NationalTeamCountry_PairsWithClubsExactlyLikeAnyOtherCountry()
    {
        // No special-casing needed anywhere in grid generation's pairing
        // logic (SelectPairing/CategoryPairingRules) — a flagged country is
        // just another CountryDefinition row.
        var template = SeedTemplate(size: 1);
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedClub("Tottenham Hotspur");
        SeedCachedMatches("England", "Tottenham Hotspur", 3);
        var module = BuildModule(minValidAnswers: 3, maxAttempts: 5);

        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        Assert.That(instance.Cells[0].RowCategoryType, Is.EqualTo(CategoryPairingRules.Country));
        Assert.That(instance.Cells[0].RowCategoryValue, Is.EqualTo("England"));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("Tottenham Hotspur"));
    }

    [Test]
    public async Task REQ114_GenerateInstanceAsync_OrdinaryCountry_StillDispatchesWithFlagFalse()
    {
        // The existing P27 path (represented here by
        // UsesCountryForSportProperty = false reaching the lookup service)
        // must stay completely unaffected — this is generation's cache-miss
        // path (GetMatchCountAsync), not the guess-time fallback.
        var template = SeedTemplate(size: 1);
        SeedCountry("France"); // usesCountryForSportProperty defaults to false
        SeedClub("Arsenal");
        // No SeedCachedMatches call — forces the live-lookup path so
        // LookupAndPersistAsync is actually invoked and its flag captured.
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakeLivePlayers("France-Arsenal", 3));
        var module = BuildModule(minValidAnswers: 3, maxAttempts: 5);

        await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetLastUsesCountryForSportProperty("France", "Arsenal"), Is.False);
    }

    [Test]
    public async Task REQ114_GenerateInstanceAsync_NationalTeamCountry_LiveLookupDispatchedWithFlagTrue()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedClub("Tottenham Hotspur");
        // No SeedCachedMatches call — forces the live-lookup path
        // (GetMatchCountAsync's cache miss) so LookupAndPersistAsync is
        // actually invoked and its flag captured.
        _wikidataLookupService.SetMatches("England", "Tottenham Hotspur", BuildFakeLivePlayers("England-Spurs", 3));
        var module = BuildModule(minValidAnswers: 3, maxAttempts: 5);

        await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetLastUsesCountryForSportProperty("England", "Tottenham Hotspur"), Is.True,
            "CategoryCandidate must carry CountryDefinition.UsesCountryForSportProperty through to the live-lookup dispatch site");
    }

    [Test]
    public async Task REQ114_ScoreSubmissionAsync_NationalTeamCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        // REQ-211's guess-time fallback dispatching through the right query
        // path for a national-team cell — mirrors
        // REQ211_ScoreSubmissionAsync_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess
        // above, but the row category is a flagged national team.
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedClub("Tottenham Hotspur");
        var (instanceId, cellId) = await SeedGridInstanceAsync("England", "Tottenham Hotspur");
        // Some other player already satisfies this cell in the cache (what
        // let grid generation accept the pairing in the first place) — but
        // the guessed player himself was never synced.
        await SeedPlayerAsync("Some Other Spur", "England", "Tottenham Hotspur");
        var kane = new Player { Id = Guid.NewGuid(), FullName = "Harry Kane", WikidataQid = "Qkane" };
        _wikidataLookupService.SetMatches("England", "Tottenham Hotspur", [kane]);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Harry Kane"));

        Assert.That(result.IsCorrect, Is.True,
            "a live lookup for a national-team cell must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(kane.Id));
        Assert.That(_wikidataLookupService.GetLastUsesCountryForSportProperty("England", "Tottenham Hotspur"), Is.True,
            "the guess-time fallback (RefreshCellFromLiveLookupAsync -> ResolveCandidateAsync) must re-resolve the full " +
            "CountryDefinition row, including its UsesCountryForSportProperty flag, not just Name/WikidataQid");
    }

    [Test]
    public async Task REQ114_ScoreSubmissionAsync_OrdinaryCountryCell_LiveLookupFallback_StillDispatchesWithFlagFalse()
    {
        // The guess-time fallback's existing P27 path must stay completely
        // unaffected for every ordinary country.
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Nicolas Anelka"));

        Assert.That(_wikidataLookupService.GetLastUsesCountryForSportProperty("France", "Arsenal"), Is.False);
    }
}

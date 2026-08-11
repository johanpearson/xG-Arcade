using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid.Tests;

// REQ-101 (generate a valid grid), REQ-102 (configurable grid size),
// REQ-107 (category pairing constraint), REQ-108 (Trophy category),
// REQ-109 (category value reference tables), REQ-114 (national teams) —
// docs/requirements-document.md §4.1. S-119 (pure refactor, no behavior
// change): split out of GridGameModuleTests.cs alongside
// GridGenerationService itself. Every test here exercises
// IGridGenerationService directly against a freshly-constructed
// GridGenerationService (composed with its own real GridLiveLookupDispatcher
// — generation-time cache misses still need a working live-lookup fallback,
// same as before the split), rather than going through
// GridGameModule.GenerateInstanceAsync — the "fakes/mocks only construct the
// one class under test" convention S-106/S-107 established for the
// IPlayerStoreRepository split (ADR-0067). Follows this repo's
// no-mocking-framework pattern (docs/coding-guidelines.md "don't
// over-mock"): real, InMemory-backed repositories (same setup as
// XGArcade.DataSync.Tests/Wikidata/WikidataLookupServiceTests.cs) plus a
// small hand-rolled FakeWikidataLookupService for the live-lookup fallback.
public class GridGenerationServiceTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IGridInstanceRepository _gridInstanceRepository = null!;
    private ICategoryValueRepository _categoryValueRepository = null!;
    private IPlayerOverrideRepository _playerOverrideRepository = null!;
    private IPlayerDataQualityRepository _playerDataQualityRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private IPlayerAttributeRepository _playerAttributeRepository = null!;
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
        _playerOverrideRepository = new PlayerOverrideRepository(_dbContext);
        _playerDataQualityRepository = new PlayerDataQualityRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _playerAttributeRepository = new PlayerAttributeRepository(_dbContext);
        _wikidataLookupService = new FakeWikidataLookupService(_playerOverrideRepository, _playerRepository, _playerAttributeRepository);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    // S-030's SelectPairing coin-flips between Country x Club and Club x
    // Club whenever the seeded reference data can support either — every
    // pre-existing test in this file (written before Club x Club existed)
    // asserts a specific Country x Club outcome, so BuildService pins that
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
    private GridGenerationService BuildService(
        int minValidAnswers, int maxAttempts, Random? random = null,
        TimeSpan? maxDuration = null, TimeProvider? timeProvider = null,
        IWikidataLookupService? wikidataLookupService = null)
    {
        var dispatcher = new GridLiveLookupDispatcher(
            _categoryValueRepository, wikidataLookupService ?? _wikidataLookupService, _playerDataQualityRepository,
            NullLogger<GridLiveLookupDispatcher>.Instance);
        return new GridGenerationService(
            _gridInstanceRepository, _categoryValueRepository, _playerAttributeRepository, dispatcher,
            new GridGenerationOptions { MinValidAnswers = minValidAnswers, MaxAttempts = maxAttempts, MaxDuration = maxDuration ?? TimeSpan.FromMinutes(10) },
            NullLogger<GridGenerationService>.Instance,
            random ?? new FixedChoiceRandom(0),
            timeProvider);
    }

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

    // S-031/REQ-108. ADR-0061: isTeamTrophy threaded through (default false,
    // same "individual award" default ReferenceDataSeeder's Ballon d'Or row
    // uses) — most existing callers don't care which query shape a trophy
    // maps to, only the ADR-0061-specific tests below pass true.
    private TrophyDefinition SeedTrophy(string name, string? wikidataQid = "unset", bool isTeamTrophy = false)
    {
        var trophy = new TrophyDefinition
        {
            Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid == "unset" ? $"Qtrophy-{name}" : wikidataQid, IsTeamTrophy = isTeamTrophy,
        };
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

    // ---- REQ-101: generate a valid grid -----------------------------------

    [Test]
    public async Task REQ101_GridGeneration_DiscardsCellWithFewerThanMinimumAnswers()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        // Four candidates below MinValidAnswers, plus exactly one that meets
        // it. Whichever order the service's internal shuffle tries them in,
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
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

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
        var service = BuildService(minValidAnswers: 5, maxAttempts: 3);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

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
        var service = BuildService(
            minValidAnswers: 5, maxAttempts: 500,
            maxDuration: TimeSpan.FromSeconds(30), timeProvider: clock,
            wikidataLookupService: wikidataLookupService);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("exceeding"));
        Assert.That(ex.Message, Does.Contain("00:00:30"), "should name the configured MaxDuration, not a raw attempt count");
    }

    // ADR-0023: MaxDuration must never interfere with an ordinary, fast
    // generation — only a genuinely slow/stuck one. Uses a finite-but-generous
    // MaxDuration (not BuildService's 10-minute test default, which would mask
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
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20, maxDuration: TimeSpan.FromSeconds(5));

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

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
        var service = BuildService(
            minValidAnswers: 5, maxAttempts: 500,
            maxDuration: TimeSpan.FromSeconds(20), timeProvider: clock,
            wikidataLookupService: wikidataLookupService);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("exceeding"));
        Assert.That(ex.Message, Does.Contain("found 0/1 valid headers in 1 attempts"),
            "must abort on the very next check after landing exactly on the deadline, before a second live lookup");
        Assert.That(
            wikidataLookupService.GetCallCount("France", "ClubA") + wikidataLookupService.GetCallCount("France", "ClubB"),
            Is.EqualTo(1), "only the first candidate's live lookup should ever run — the second must never be attempted");
    }

    // S-030: PickHeadersAsync's deadline check is shared code, not
    // duplicated per pairing type — but GetMatchCountAsync's live-lookup
    // dispatch (IGridLiveLookupDispatcher.LookupMatchesAsync) branches by
    // category type, so this confirms the deadline also trips when that
    // dispatch routes through LookupAndPersistClubClubAsync, not just the
    // Country x Club branch the test above exercises.
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
        var service = BuildService(
            minValidAnswers: 5, maxAttempts: 500,
            maxDuration: TimeSpan.FromSeconds(30), timeProvider: clock,
            wikidataLookupService: wikidataLookupService);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

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
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("Arsenal"));
        Assert.That(await _playerAttributeRepository.CountPlayersWithBothAttributesAsync(
            "nationality", "France", "club", "Arsenal"), Is.EqualTo(3),
            "a live lookup persists immediately, same request, same as the real WikidataLookupService (ADR-0010) — " +
            "not left for the cache to somehow already have known about");
        // ADR-0029: a generation-time cache-miss is a routine sync, trusted
        // as ground truth — distinct from REQ-211's guess-time fallback,
        // which stays reviewable (see GridLiveLookupDispatcherTests).
        Assert.That(_wikidataLookupService.GetLastOrigin("France", "Arsenal"), Is.EqualTo(WikidataLookupOrigin.Sync));
        // REQ-110 (2026-07-28 "cache-warming-specific timeout" extension):
        // round generation's own live-lookup call site must keep passing (or
        // omitting, which defaults to) WikidataQueryTimeoutTier.Default —
        // only PlayerCacheWarmingService opts into the wider CacheWarming
        // budget (see PlayerCacheWarmingServiceTests' own coverage of that).
        // A regression guard: this test would fail if GridGenerationService
        // ever started passing CacheWarming here by accident.
        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.Default));
    }

    [Test]
    public void REQ101_GridGenerationOptions_DefaultsMinValidAnswersToFive()
    {
        var options = new GridGenerationOptions();

        Assert.That(options.MinValidAnswers, Is.EqualTo(5));
        Assert.That(options.MaxAttempts, Is.EqualTo(500), "S-014 only raised MinValidAnswers; MaxAttempts is unchanged");
        Assert.That(options.MaxDuration, Is.EqualTo(TimeSpan.FromSeconds(90)), "ADR-0023");
    }

    // GenerateInstanceAsync_UnknownTemplateId_ThrowsGridGenerationException
    // stayed in the slim GridGameModuleTests.cs — it doubles as the
    // adapter's own "GenerateInstanceAsync passthrough" proof (the
    // exception crosses the IGameModule boundary unchanged), so it isn't
    // duplicated here.

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
        var service = BuildService(minValidAnswers: 2, maxAttempts: 50);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

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
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

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
        var service = BuildService(minValidAnswers: 2, maxAttempts: 50);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

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
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20, random: new FixedChoiceRandom(1));

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Club && c.ColCategoryType == CategoryPairingRules.Club),
            "with both pairings feasible, FixedChoiceRandom(1) must steer SelectPairing to Club x Club, not the Country x Club default");
    }

    // ---- REQ-108/S-031: Trophy category ------------------------------------
    // Originally (S-031), production only ever seeded one trophy (Ballon
    // d'Or, ReferenceDataSeeder) — trophyCount(1) could never clear `size`
    // for any realistic grid size, so a Trophy pairing structurally never got
    // selected in production. The tests immediately below inject a larger
    // fake trophy pool (SeedTrophy, 3+ values) to prove the mechanism itself
    // works, independent of whether production data happened to trigger it
    // yet — that separation still matters even now that ADR-0061 grew the
    // real seeded pool to 3 (see the "---- ADR-0061" section further below
    // for tests against that specific, now-reachable real-seed-data shape).

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
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

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
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

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
    public async Task REQ108_SelectPairing_ExactlyOneTrophySeeded_TrophyPairingNeverSelected()
    {
        // Pure mechanism coverage (no longer "matching real seed data" —
        // see the ADR-0061 section below for tests against the actual,
        // now-3-trophy production shape): with only one trophy in the pool
        // and size >= 2, trophyCount(1) can never clear `size` for any mixed
        // pairing, nor `size * 2` for Trophy x Trophy — so every Trophy
        // pairing is infeasible and Country x Club is the only choice,
        // regardless of the injected Random.
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
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.None.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Trophy || c.ColCategoryType == CategoryPairingRules.Trophy),
            "with only one trophy in the pool, Trophy can never be selected for any realistic grid size");
    }

    // ---- ADR-0061: real (3-trophy) seeded-pool reachability ----------------
    // Before ADR-0061, REQ108_SelectPairing_ExactlyOneTrophySeeded_TrophyPairingNeverSelected
    // above documented that production's real seeded pool (1 trophy) could
    // never make a Trophy pairing reachable. ADR-0061 grew that pool to 3
    // (Ballon d'Or, FIFA World Cup, UEFA Champions League) — these tests
    // prove the REVERSED consequence: Country x Trophy/Club x Trophy are now
    // reachable for the default GridSize = 3, while Trophy x Trophy (needing
    // trophyCount >= size * 2 = 6) still isn't.

    [Test]
    public async Task REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_CountryTrophyPairingIsNowSelectable()
    {
        // The real ReferenceDataSeeder shape as of ADR-0061: exactly three
        // trophies, matching names/flags. Zero clubs seeded -> every
        // Club-involving pairing is infeasible; countryCount(3) and
        // trophyCount(3) both clear the default GridSize = 3, so
        // Country x Trophy is the only feasible pairing — deterministic
        // regardless of the injected Random.
        var template = SeedTemplate(size: 3);
        SeedCountry("France");
        SeedCountry("Spain");
        SeedCountry("Brazil");
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        var trophyNames = new[] { "Ballon d'Or", "FIFA World Cup", "UEFA Champions League" };
        foreach (var countryName in new[] { "France", "Spain", "Brazil" })
            foreach (var trophyName in trophyNames)
                SeedCachedTrophyCountryMatches(trophyName, countryName, count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Country && c.ColCategoryType == CategoryPairingRules.Trophy),
            "with the real (now 3-trophy) seeded pool, Country x Trophy must be selectable for a size-3 grid — this reverses S-031's original 'structurally dormant' consequence");
    }

    [Test]
    public async Task REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_ClubTrophyPairingIsNowSelectable()
    {
        // Mirror of the Country x Trophy test above — zero countries seeded
        // -> every Country-involving pairing is infeasible, leaving
        // Club x Trophy as the only feasible pairing.
        var template = SeedTemplate(size: 3);
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedClub("Real Madrid");
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        var trophyNames = new[] { "Ballon d'Or", "FIFA World Cup", "UEFA Champions League" };
        foreach (var clubName in new[] { "Arsenal", "Barcelona", "Real Madrid" })
            foreach (var trophyName in trophyNames)
                SeedCachedTrophyClubMatches(trophyName, clubName, count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Club && c.ColCategoryType == CategoryPairingRules.Trophy),
            "with the real (now 3-trophy) seeded pool, Club x Trophy must be selectable for a size-3 grid");
    }

    [Test]
    public void REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_TrophyTrophyPairingStillInfeasible()
    {
        // Trophy x Trophy needs trophyCount >= size * 2 = 6 — three trophies
        // still doesn't clear that, even though it now clears the plain
        // `>= size` bar Country x Trophy/Club x Trophy need. Zero countries
        // and zero clubs seeded, so no other pairing is feasible either —
        // GenerateInstanceAsync must abort with GridGenerationException
        // rather than silently produce a Trophy x Trophy grid.
        var template = SeedTemplate(size: 3);
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));
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
        var service = BuildService(minValidAnswers: 1, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

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
        // the country QID is null — proves the service never gets a match for
        // "NoDataClub" from this path, only from the (absent) cache.
        _wikidataLookupService.SetMatches("Ruritania", "NoDataClub", BuildFakeLivePlayers("NoDataClub", 5));
        var service = BuildService(minValidAnswers: 2, maxAttempts: 5);

        GameInstance? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result!.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells[0].RowCategoryValue, Is.EqualTo("Ruritania"));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("GoodClub"));
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
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

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
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

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
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetLastUsesCountryForSportProperty("England", "Tottenham Hotspur"), Is.True,
            "CategoryCandidate must carry CountryDefinition.UsesCountryForSportProperty through to the live-lookup dispatch site");
    }

    // ---- ADR-0061: team-competition trophy dispatch threading -------------
    // Mirrors the REQ-114 Country x Club coverage immediately above — proves
    // the Country x Trophy call site now threads BOTH
    // row.UsesCountryForSportProperty and col.IsTeamTrophy through to
    // WikidataLookupService (the "REQ-114/ADR-0035 scope note" gap this
    // story closed), and that Club x Trophy threads col.IsTeamTrophy.

    [Test]
    public async Task REQ108_GenerateInstanceAsync_NationalTeamCountryTrophyPairing_LiveLookupDispatchedWithUsesCountryForSportPropertyTrue()
    {
        // size=1 keeps this deterministic without needing a 3-trophy pool:
        // Country x Club is infeasible (zero clubs seeded), Trophy x Trophy
        // needs trophyCount >= 2, so Country x Trophy is the only feasible
        // pairing with one trophy seeded.
        var template = SeedTemplate(size: 1);
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedTrophy("Ballon d'Or");
        // No SeedCachedTrophyCountryMatches call — forces the live-lookup
        // path so LookupAndPersistTrophyCountryAsync is actually invoked and
        // its flags captured.
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "England", BuildFakeLivePlayers("BallonDor-England", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyCountryLastUsesCountryForSportProperty("Ballon d'Or", "England"), Is.True,
            "CategoryCandidate must carry CountryDefinition.UsesCountryForSportProperty through to the Trophy x Country live-lookup dispatch site, not silently fall back to P27 (ADR-0035/ADR-0061)");
    }

    [Test]
    public async Task REQ108_GenerateInstanceAsync_OrdinaryCountryTrophyPairing_StillDispatchesWithUsesCountryForSportPropertyFalse()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France"); // usesCountryForSportProperty defaults to false
        SeedTrophy("Ballon d'Or");
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "France", BuildFakeLivePlayers("BallonDor-France", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyCountryLastUsesCountryForSportProperty("Ballon d'Or", "France"), Is.False);
    }

    [Test]
    public async Task REQ108_GenerateInstanceAsync_TeamTrophyCountryPairing_LiveLookupDispatchedWithIsTeamTrophyTrue()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        _wikidataLookupService.SetTrophyCountryMatches("FIFA World Cup", "France", BuildFakeLivePlayers("WorldCup-France", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyCountryLastIsTeamTrophy("FIFA World Cup", "France"), Is.True,
            "CategoryCandidate must carry TrophyDefinition.IsTeamTrophy through to the Trophy x Country live-lookup dispatch site (ADR-0061)");
    }

    [Test]
    public async Task REQ108_GenerateInstanceAsync_IndividualAwardCountryPairing_StillDispatchesWithIsTeamTrophyFalse()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "France", BuildFakeLivePlayers("BallonDor-France", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyCountryLastIsTeamTrophy("Ballon d'Or", "France"), Is.False);
    }

    [Test]
    public async Task REQ108_GenerateInstanceAsync_TeamTrophyClubPairing_LiveLookupDispatchedWithIsTeamTrophyTrue()
    {
        var template = SeedTemplate(size: 1);
        SeedClub("Real Madrid");
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        _wikidataLookupService.SetTrophyClubMatches("UEFA Champions League", "Real Madrid", BuildFakeLivePlayers("ChampionsLeague-RealMadrid", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyClubLastIsTeamTrophy("UEFA Champions League", "Real Madrid"), Is.True,
            "CategoryCandidate must carry TrophyDefinition.IsTeamTrophy through to the Trophy x Club live-lookup dispatch site (ADR-0061)");
    }
}

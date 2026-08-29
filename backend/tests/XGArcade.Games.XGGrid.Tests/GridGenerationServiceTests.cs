using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Core.Games;
using XGArcade.Data;
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
//
// ADR-0089 (2026-08-29): GridGenerationService no longer picks one pairing
// type (e.g. Country x Club) for the whole instance via the now-removed
// SelectPairing/PoolFor — every row/column header independently draws from
// one combined Country+Club+Trophy pool. That means, unlike before this
// ADR, this test file can no longer assume "the row axis is always type X"
// just because only type X and type Y were seeded in some ratio — a header
// slot can land on ANY seeded candidate, of any type, with no guaranteed
// split. Every test below is deliberately built one of two ways to stay
// genuinely deterministic under that real randomness (there is no dotnet
// SDK in this sandbox to verify a Random-override trick against .NET's
// actual Random.Shuffle algorithm, so none of these tests rely on one):
//   (a) seed only ONE category type at all, removing type-selection
//       ambiguity entirely (a homogeneous pool has no "wrong" split), or
//   (b) seed few enough distinct candidates (usually exactly one of each
//       type involved) that there is no ambiguity about which candidate
//       could play which role, and assert on the resulting SET of header
//       values/types rather than hard-coding which one landed as the row
//       vs. the column.
// A dedicated test-writer pass owns new REQ-107-mixing-specific coverage
// beyond what's adapted here (see the backend-implementer handoff for this
// change).
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
            _categoryValueRepository, wikidataLookupService ?? _wikidataLookupService, _playerDataQualityRepository);
        return new GridGenerationService(
            _gridInstanceRepository, _categoryValueRepository, _playerAttributeRepository, dispatcher,
            new GridGenerationOptions { MinValidAnswers = minValidAnswers, MaxAttempts = maxAttempts, MaxDuration = maxDuration ?? TimeSpan.FromMinutes(10) },
            NullLogger<GridGenerationService>.Instance,
            random,
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
        // ADR-0089: pure Club x Club (no countries at all) rather than the
        // pre-ADR-0089 Country x Club shape — with header selection now
        // drawing from a combined pool, a single seeded country would
        // itself be just another candidate that could land on either axis
        // (see this file's own header comment). "GoodClub" is a universal
        // hub — every other club has a below-threshold match count against
        // it, and every OTHER pair (weak-vs-weak) also stays below
        // threshold — so no matter which single candidate the shuffle picks
        // as the row, the retry loop must reject every weak candidate
        // before finally accepting GoodClub, proving the discard/retry
        // logic works regardless of which axis GoodClub ends up on.
        SeedClub("WeakClub0");
        SeedClub("WeakClub1");
        SeedClub("WeakClub2");
        SeedClub("WeakClub3");
        SeedClub("GoodClub");
        var template = SeedTemplate(size: 1);
        SeedCachedClubClubMatches("GoodClub", "WeakClub0", 3);
        SeedCachedClubClubMatches("GoodClub", "WeakClub1", 3);
        SeedCachedClubClubMatches("GoodClub", "WeakClub2", 3);
        SeedCachedClubClubMatches("GoodClub", "WeakClub3", 3);
        SeedCachedClubClubMatches("WeakClub0", "WeakClub1", 0);
        SeedCachedClubClubMatches("WeakClub0", "WeakClub2", 1);
        SeedCachedClubClubMatches("WeakClub0", "WeakClub3", 1);
        SeedCachedClubClubMatches("WeakClub1", "WeakClub2", 0);
        SeedCachedClubClubMatches("WeakClub1", "WeakClub3", 2);
        SeedCachedClubClubMatches("WeakClub2", "WeakClub3", 0);
        var service = BuildService(minValidAnswers: 3, maxAttempts: 10);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        Assert.That(
            new[] { instance.Cells[0].RowCategoryValue, instance.Cells[0].ColCategoryValue },
            Does.Contain("GoodClub"),
            "whichever candidate the shuffle picked as the row, every weak-vs-weak candidate is below MinValidAnswers, " +
            "so only GoodClub can ever complete the grid");
    }

    [Test]
    public async Task REQ101_GridGeneration_AbortsWithGridGenerationException_WhenMaxAttemptsExhausted()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        // Five club candidates, none ever satisfying MinValidAnswers=5 (all
        // cached at 0) — with MaxAttempts=3, the loop must abort before
        // exhausting the candidate pool. Only France is a country, so
        // regardless of which single candidate the shuffle picks as the row,
        // every remaining candidate is a genuine cache-miss-or-zero-match —
        // this test only asserts on the exception, never on which axis
        // anything landed on.
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
        // same as the incident's cold-cache scenario, regardless of which
        // candidate the shuffle happens to pick as the row.
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
    // generation — only a genuinely slow/stuck one.
    //
    // ADR-0089: pure Club x Club — a fully-connected 4-club pool where every
    // pair meets MinValidAnswers, so the grid completes regardless of which
    // 2 of the 4 candidates the shuffle draws as rows vs. columns (see this
    // file's own header comment on why a mixed Country/Club pool can't
    // safely make that same "any split works" guarantee once the shared
    // pool of candidates is what's drawn from, not a fixed axis).
    [Test]
    public async Task REQ101_GridGeneration_FastSuccessfulRun_WellUnderMaxDuration_SucceedsUnaffected()
    {
        var template = SeedTemplate(size: 2);
        var clubNames = new[] { "ClubA", "ClubB", "ClubC", "ClubD" };
        foreach (var clubName in clubNames)
            SeedClub(clubName);
        for (var i = 0; i < clubNames.Length; i++)
            for (var j = i + 1; j < clubNames.Length; j++)
                SeedCachedClubClubMatches(clubNames[i], clubNames[j], count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20, maxDuration: TimeSpan.FromSeconds(5));

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(4),
            "an ordinary all-cache-hit run must succeed normally — MaxDuration must not abort a run that never gets close to it");
    }

    // ADR-0023's deadline check (`_timeProvider.GetUtcNow() >= deadline`) is
    // deliberately inclusive — landing exactly ON the deadline must still
    // abort, not be allowed one more attempt.
    //
    // ADR-0089: three clubs, no countries — with only one type present,
    // whichever single candidate lands as the row, the other two are both
    // genuine cache misses (no SeedCachedClubClubMatches call at all), so
    // the "exactly one live lookup runs, then the deadline aborts before a
    // second" assertion holds regardless of the split. Because which
    // specific pair gets tried can't be pinned down, the call-count
    // assertion sums every possible (clubX, clubY) ordering rather than
    // naming one pair, mirroring how the pre-ADR-0089 version of this test
    // already summed over "whichever of the two clubs got tried."
    [Test]
    public async Task REQ101_GridGeneration_AbortsWithGridGenerationException_WhenClockLandsExactlyOnDeadline()
    {
        var template = SeedTemplate(size: 1);
        SeedClub("ClubA");
        SeedClub("ClubB");
        SeedClub("ClubC");
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
        var totalClubClubCalls =
            wikidataLookupService.GetClubClubCallCount("ClubA", "ClubB") + wikidataLookupService.GetClubClubCallCount("ClubB", "ClubA") +
            wikidataLookupService.GetClubClubCallCount("ClubA", "ClubC") + wikidataLookupService.GetClubClubCallCount("ClubC", "ClubA") +
            wikidataLookupService.GetClubClubCallCount("ClubB", "ClubC") + wikidataLookupService.GetClubClubCallCount("ClubC", "ClubB");
        Assert.That(totalClubClubCalls, Is.EqualTo(1),
            "exactly one live lookup (whichever pair the shuffle picked) should ever run — a second must never be attempted");
    }

    // S-030: PickHeadersAsync's deadline check is shared code, not
    // duplicated per pairing type — but GetMatchCountAsync's live-lookup
    // dispatch (IGridLiveLookupDispatcher.LookupMatchesAsync) branches by
    // category type, so this confirms the deadline also trips when that
    // dispatch routes through LookupAndPersistClubClubAsync, not just the
    // Country x Club branch the test above exercises. Zero countries
    // seeded, so every candidate is a Club — no type-mixing ambiguity here.
    [Test]
    public async Task REQ101_GridGeneration_ClubClubPairing_AbortsWithGridGenerationException_WhenMaxDurationExceeded()
    {
        var template = SeedTemplate(size: 1);
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
        // data for this candidate. Only 2 total candidates exist, so
        // whichever one the shuffle picks as the row, the pairing under
        // test is always France x Arsenal — only the axis assignment is
        // unknown, which is why the cell assertion below checks the pair as
        // a set rather than a specific row/column.
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakeLivePlayers("Arsenal", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        Assert.That(
            new[] { instance.Cells[0].RowCategoryValue, instance.Cells[0].ColCategoryValue },
            Is.EquivalentTo(new[] { "France", "Arsenal" }));
        Assert.That(await _playerAttributeRepository.CountPlayersWithBothAttributesAsync(
            "nationality", "France", "club", "Arsenal"), Is.EqualTo(3),
            "a live lookup persists immediately, same request, same as the real WikidataLookupService (ADR-0010) — " +
            "not left for the cache to somehow already have known about");
        // ADR-0029: a generation-time cache-miss is a routine sync, trusted
        // as ground truth — distinct from REQ-211's guess-time fallback,
        // which stays reviewable (see GridLiveLookupDispatcherTests). Keyed
        // by domain name (country, club), not by GridGenerationService's
        // internal row/col labels, so this holds regardless of which axis
        // France/Arsenal ended up on (GridLiveLookupDispatcher normalizes
        // by CategoryType before calling into WikidataLookupService — see
        // ADR-0089).
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

    // ADR-0089: pure Club x Club, fully cross-matched — with header
    // selection now drawing every row AND column from one combined pool
    // (rather than a fixed Country-rows/Club-columns split), a mixed
    // Country+Club seed here would make the exact row/column split
    // (and therefore which cells even exist) genuinely random and, for a
    // size >= 2 grid, sometimes impossible to complete at all (a row/column
    // split with 2+ countries on one side and 1+ country on the other hits
    // REQ-107's ban with no valid substitute once the pool is exhausted —
    // see this file's own header comment). A single homogeneous type has no
    // such failure mode (Club x Club is never banned), so this keeps the
    // test's actual point — configurable size, N unique headers per axis,
    // no row/column overlap — genuinely deterministic regardless of shuffle
    // order.
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task REQ102_GenerateInstanceAsync_ProducesExactlySizeSquaredCellsWithUniqueRowAndColumnValues(int size)
    {
        var template = SeedTemplate(size);
        var clubNames = Enumerable.Range(0, size * 2).Select(i => $"Club{i}").ToList();
        foreach (var clubName in clubNames)
            SeedClub(clubName);
        for (var i = 0; i < clubNames.Count; i++)
            for (var j = i + 1; j < clubNames.Count; j++)
                SeedCachedClubClubMatches(clubNames[i], clubNames[j], count: 2);
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

    // ADR-0089: replaces the pre-ADR-0089
    // REQ107_GenerateInstanceAsync_NeverProducesCountryCountryPairing test,
    // whose Country+Club seed shape became flaky once header selection
    // started drawing every row AND column from one combined pool — a
    // row/column split with 2+ countries on one side and 1+ on the other
    // now hits the ban with no valid substitute left (see this file's own
    // header comment), so that seed shape can no longer honestly claim
    // "never" without actually controlling which candidates land on which
    // axis. This version instead FORCES the exact scenario REQ-107 exists
    // to prevent — a row and its only remaining column candidate are both
    // Country-typed — and proves the per-row check in
    // GridGenerationService.TryComputeMatchCountsAsync rejects it before
    // ever touching a match-count query (REQ-107's ordering requirement,
    // and the position ADR-0089 moved this check to). Two countries, no
    // clubs, size 1: whichever one the shuffle draws as the row, the other
    // is the sole column candidate — always a Country x Country attempt.
    [Test]
    public async Task REQ107_GenerateInstanceAsync_RejectsCountryCountryCandidate_ViaPerRowCheckBeforeMatchCountQuery()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedCountry("Spain");
        var service = BuildService(minValidAnswers: 1, maxAttempts: 10);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("Ran out of candidates"),
            "the sole column candidate must be rejected by the REQ-107 pairing check, not accepted, leaving the pool exhausted");
        Assert.That(
            _wikidataLookupService.GetCallCount("France", "Spain") + _wikidataLookupService.GetCallCount("Spain", "France"),
            Is.EqualTo(0),
            "REQ-107's Country x Country check must reject the candidate before any matching-count query " +
            "(cache check or live lookup) ever runs for it");
    }

    // ---- REQ-107/S-030: Club x Club pairing --------------------------------

    // Zero countries/trophies seeded — every candidate is a Club, so there
    // is no type-selection ambiguity to control for here (see this file's
    // own header comment); this test needed no changes for ADR-0089 beyond
    // no longer describing its determinism in terms of the removed
    // SelectPairing.
    [Test]
    public async Task REQ107_GenerateInstanceAsync_ClubClubGrid_ProducesExactlySizeSquaredCellsWithUniqueRowAndColumnValues()
    {
        var template = SeedTemplate(size: 3);
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
            "with zero countries/trophies seeded, every header must be a Club");
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

    // ---- REQ-108/S-031: Trophy category ------------------------------------

    // ADR-0089: size 1, exactly one Country and one Trophy candidate —
    // whichever the shuffle draws as the row, the pairing under test is
    // always Country x Trophy (just with an unknown axis assignment), so
    // the assertion checks the resulting type/value SETS rather than a
    // specific row or column.
    [Test]
    public async Task REQ108_GenerateInstanceAsync_TrophyCountryPairing_ProducesGridUsingTrophyCategoryType()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedTrophy("Ballon d'Or");
        SeedCachedTrophyCountryMatches("Ballon d'Or", "France", count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        var cell = instance.Cells[0];
        Assert.That(
            new[] { cell.RowCategoryType, cell.ColCategoryType },
            Is.EquivalentTo(new[] { CategoryPairingRules.Country, CategoryPairingRules.Trophy }));
        Assert.That(
            new[] { cell.RowCategoryValue, cell.ColCategoryValue },
            Is.EquivalentTo(new[] { "France", "Ballon d'Or" }));
    }

    // Mirror of the Country x Trophy test above — one Club, one Trophy.
    [Test]
    public async Task REQ108_GenerateInstanceAsync_TrophyClubPairing_ProducesGridUsingTrophyCategoryType()
    {
        var template = SeedTemplate(size: 1);
        SeedClub("Arsenal");
        SeedTrophy("Ballon d'Or");
        SeedCachedTrophyClubMatches("Ballon d'Or", "Arsenal", count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        var cell = instance.Cells[0];
        Assert.That(
            new[] { cell.RowCategoryType, cell.ColCategoryType },
            Is.EquivalentTo(new[] { CategoryPairingRules.Club, CategoryPairingRules.Trophy }));
        Assert.That(
            new[] { cell.RowCategoryValue, cell.ColCategoryValue },
            Is.EquivalentTo(new[] { "Arsenal", "Ballon d'Or" }));
    }

    // ---- ADR-0061: real (3-trophy) seeded-pool reachability ----------------
    // Before ADR-0089, this section proved SelectPairing's removed
    // feasibility formula (trophyCount >= size) made Country x Trophy/
    // Club x Trophy newly reachable once ReferenceDataSeeder grew from 1 to
    // 3 trophies. That formula no longer exists — a lone trophy candidate
    // can always be drawn as a header now, win or lose on its own match
    // data like anything else (there is no more per-type feasibility gate
    // to "unlock"). What's still worth proving is that a Country/Club x
    // Trophy grid can actually be produced and dispatched correctly against
    // the real 3-trophy shape — the tests below do that with size 1 (one
    // Country/Club candidate against the full 3-trophy pool) so the
    // candidate that isn't the row is guaranteed to always find at least
    // one matching Trophy partner, regardless of which specific candidate
    // the shuffle draws as the row (see each test's own comment for why).

    [Test]
    public async Task REQ108_GenerateInstanceAsync_ThreeTrophiesSeeded_CountryTrophyPairingSucceeds()
    {
        // Whichever the shuffle draws as the row — France, or one of the 3
        // trophies — the grid still completes: if France is the row, any of
        // the 3 trophies (all fully matched against France) can complete
        // it; if a trophy is the row, France is the only viable column
        // candidate (the other 2 trophies have no trophy-trophy match data
        // seeded and are rejected by MinValidAnswers, not by any pairing
        // ban), and it always finds it before the pool of 3 remaining
        // candidates is exhausted. Either way, "France" always ends up as
        // one of the two header values.
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        foreach (var trophyName in new[] { "Ballon d'Or", "FIFA World Cup", "UEFA Champions League" })
            SeedCachedTrophyCountryMatches(trophyName, "France", count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        var cell = instance.Cells[0];
        Assert.That(
            new[] { cell.RowCategoryType, cell.ColCategoryType },
            Is.EquivalentTo(new[] { CategoryPairingRules.Country, CategoryPairingRules.Trophy }),
            "with the real (now 3-trophy) seeded pool, a Country x Trophy grid must be producible");
        Assert.That(new[] { cell.RowCategoryValue, cell.ColCategoryValue }, Does.Contain("France"));
    }

    // Mirror of the Country x Trophy test above — one Club against the full
    // 3-trophy pool.
    [Test]
    public async Task REQ108_GenerateInstanceAsync_ThreeTrophiesSeeded_ClubTrophyPairingSucceeds()
    {
        var template = SeedTemplate(size: 1);
        SeedClub("Real Madrid");
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        foreach (var trophyName in new[] { "Ballon d'Or", "FIFA World Cup", "UEFA Champions League" })
            SeedCachedTrophyClubMatches(trophyName, "Real Madrid", count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        var cell = instance.Cells[0];
        Assert.That(
            new[] { cell.RowCategoryType, cell.ColCategoryType },
            Is.EquivalentTo(new[] { CategoryPairingRules.Club, CategoryPairingRules.Trophy }),
            "with the real (now 3-trophy) seeded pool, a Club x Trophy grid must be producible");
        Assert.That(new[] { cell.RowCategoryValue, cell.ColCategoryValue }, Does.Contain("Real Madrid"));
    }

    // Trophy x Trophy needs 2xSize distinct trophies (REQ-102's no-shared-
    // header rule) — three trophies still doesn't clear that for a size-3
    // grid. Zero countries/clubs seeded, so the combined pool is exactly 3
    // candidates == template.Size: every one of them is necessarily drawn
    // as a row (no randomness in this specific outcome — there is no other
    // candidate available to be anything else), which leaves the column
    // candidate pool empty after REQ-102's dedup filter. That trips
    // GridGenerationService's new combined-pool-size check on the column
    // side (ADR-0089) — the replacement for SelectPairing's removed
    // `trophyCount >= size * 2` feasibility formula.
    [Test]
    public void REQ108_GenerateInstanceAsync_ThreeTrophiesSeeded_TrophyTrophyPairingStillInfeasible()
    {
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
        // as a candidate, however good its match count, on either axis.
        SeedCachedMatches("France", "PhantomClub", 10);
        var service = BuildService(minValidAnswers: 1, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        var cell = instance!.Cells[0];
        Assert.That(new[] { cell.RowCategoryValue, cell.ColCategoryValue }, Is.EquivalentTo(new[] { "France", "Arsenal" }));
        Assert.That(new[] { cell.RowCategoryValue, cell.ColCategoryValue }, Does.Not.Contain("PhantomClub"));
    }

    [Test]
    public async Task REQ109_GenerateInstanceAsync_NullWikidataQid_DoesNotThrow_AndDiscardsThroughOrdinaryRetry()
    {
        var template = SeedTemplate(size: 1);
        // No resolved WikidataQid yet (REQ-109) — must not crash generation.
        SeedCountry("Ruritania", wikidataQid: null);
        SeedClub("NoDataClub");   // cache miss + null country QID -> live lookup skipped -> 0 matches -> discarded whenever tried against Ruritania
        SeedClub("GoodClub");     // cache hit against Ruritania -> accepted without ever needing a live lookup
        SeedCachedMatches("Ruritania", "GoodClub", 2);
        // GoodClub also has a cached Club x Club match against NoDataClub —
        // this is what keeps the grid completable no matter which single
        // candidate the shuffle draws as the row (see this test's own
        // reasoning below); it never interferes with the Ruritania checks
        // above, since it's a different AttributeType pair.
        SeedCachedClubClubMatches("NoDataClub", "GoodClub", 2);
        // Configured on the fake, but unreachable via the real contract since
        // Ruritania's QID is null — proves the service never gets a match
        // for "NoDataClub" from this path, only from the (absent) cache.
        _wikidataLookupService.SetMatches("Ruritania", "NoDataClub", BuildFakeLivePlayers("NoDataClub", 5));
        var service = BuildService(minValidAnswers: 2, maxAttempts: 5);

        GameInstance? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result!.Id);
        Assert.That(instance, Is.Not.Null);
        var cell = instance!.Cells[0];
        // Whichever of the 3 candidates the shuffle draws as the row,
        // "GoodClub" always ends up in the final pair: if Ruritania is the
        // row, NoDataClub fails (null QID) and GoodClub succeeds; if
        // NoDataClub is the row, Ruritania fails (null QID) but GoodClub
        // succeeds via the Club x Club cache; if GoodClub is the row, either
        // remaining candidate succeeds (both have real cached data against
        // it) and GoodClub is on the other axis regardless.
        Assert.That(new[] { cell.RowCategoryValue, cell.ColCategoryValue }, Does.Contain("GoodClub"));
    }

    // ---- REQ-114/ADR-0035: national teams as distinct footballing entities

    [Test]
    public async Task REQ114_GenerateInstanceAsync_NationalTeamCountry_PairsWithClubsExactlyLikeAnyOtherCountry()
    {
        // No special-casing needed anywhere in grid generation's pairing
        // logic (CategoryPairingRules) — a flagged country is just another
        // CountryDefinition row. Only 2 total candidates exist, so the
        // assertion checks the pair as a set rather than a specific axis.
        var template = SeedTemplate(size: 1);
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedClub("Tottenham Hotspur");
        SeedCachedMatches("England", "Tottenham Hotspur", 3);
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        var cell = instance.Cells[0];
        Assert.That(
            new[] { cell.RowCategoryType, cell.ColCategoryType },
            Is.EquivalentTo(new[] { CategoryPairingRules.Country, CategoryPairingRules.Club }));
        Assert.That(
            new[] { cell.RowCategoryValue, cell.ColCategoryValue },
            Is.EquivalentTo(new[] { "England", "Tottenham Hotspur" }));
    }

    [Test]
    public async Task REQ114_GenerateInstanceAsync_OrdinaryCountry_StillDispatchesWithFlagFalse()
    {
        // The existing P27 path (represented here by
        // UsesCountryForSportProperty = false reaching the lookup service)
        // must stay completely unaffected — this is generation's cache-miss
        // path (GetMatchCountAsync), not the guess-time fallback. Keyed by
        // domain name, not by row/col axis (GridLiveLookupDispatcher
        // normalizes by CategoryType — ADR-0089), so this holds regardless
        // of which candidate the shuffle drew as the row.
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
    // story closed), and that Club x Trophy threads col.IsTeamTrophy. Every
    // test in this section seeds exactly one candidate of each of the two
    // types involved, so there is no row/column ambiguity to control for,
    // and every assertion is keyed by domain name (via the Fake's own
    // dictionaries), not by GridGenerationService's internal row/col
    // labels — GridLiveLookupDispatcher normalizes by CategoryType
    // (ADR-0089) before ever calling into WikidataLookupService, so these
    // needed no changes for ADR-0089 beyond CategoryCandidate's own
    // constructor shape.

    [Test]
    public async Task REQ108_GenerateInstanceAsync_NationalTeamCountryTrophyPairing_LiveLookupDispatchedWithUsesCountryForSportPropertyTrue()
    {
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

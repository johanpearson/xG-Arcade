using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

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

    // Builds a set of distinct fake "live Wikidata match" players for
    // FakeWikidataLookupService.SetMatches — distinct from SeedCachedMatches
    // above, which writes directly to the DbContext to simulate data already
    // cached BEFORE a run starts. This helper's players are only persisted
    // if/when the fake's own LookupAndPersistAsync path runs (i.e. they
    // simulate what a live lookup would return this run).
    private static List<Player> BuildFakePlayers(string countryName, string clubName, int count)
    {
        var players = new List<Player>();
        for (var i = 0; i < count; i++)
        {
            players.Add(new Player
            {
                Id = Guid.NewGuid(),
                FullName = $"{countryName}-{clubName}-Player{i}",
                WikidataQid = $"Qlive-{countryName}-{clubName}-{i}",
            });
        }
        return players;
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

    // REQ-110 (2026-07-28 "persisted confirmed-low signal" extension): a
    // below-threshold pair looks identical to a never-checked pair from
    // CountPlayersWithBothAttributesAsync's return value alone — this test
    // now confirms the *other* half of that distinction: as long as no
    // prior run has confirmed this specific pair low (via
    // ConfirmedLowMatchPair), it is queried live, not skipped. This was
    // previously documented as an "accepted gap" that every below-threshold
    // pair is re-queried unconditionally — that blanket claim is now
    // superseded (see REQ110_WarmAsync_PreviouslyConfirmedLowPair_
    // SkippedWithoutLiveQuery below for the case this test does NOT cover).
    [Test]
    public async Task REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 2);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "the skip-if-confirmed-low path must not trigger for a pair no prior run has confirmed low");
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
    }

    // REQ-110 (2026-07-28 "technical-failure visibility" extension): a
    // live-queried pair whose lookup ends in a technical failure (timeout/
    // HTTP/parse error, simulated here via FailWithTechnicalFailure) must be
    // counted separately from PairsQueriedLive's existing meaning and named
    // in FailingPairs — distinct from a pair that queries successfully and
    // simply finds zero matches (BelowMinValidAnswers, seeded via the
    // second, unconfigured country/club pair below).
    [Test]
    public async Task REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse()
    {
        // A single shared club (Arsenal) — a single club has zero Club x
        // Club pairs to pair with itself, so this seeds exactly 2 Country x
        // Club pairs (France x Arsenal, Spain x Arsenal) and nothing else,
        // keeping this test's pair count exact and easy to reason about.
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        // France x Arsenal: every attempt within the run hits a technical
        // failure (MaxAttemptsPerPair = 2, both attempts fail).
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        // Spain x Arsenal: no failure configured, no matches configured —
        // a genuine "queried successfully, found nothing" response.
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(2), "PairsQueriedLive counts every live-queried pair, technical failure or not");
        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Is.EquivalentTo(new[] { "France x Arsenal" }));
        Assert.That(result.FailingPairs, Does.Not.Contain("Spain x Arsenal"),
            "a genuine zero-match success must never be listed as a failing pair");
    }

    // REQ-110 (2026-07-28 "cache-warming-specific timeout + same-run retry"
    // extension): a pair whose FIRST attempt hits a technical failure but
    // succeeds on the same-run retry must not be counted as a technical
    // failure at all, and the retry's real (successful) result is what gets
    // used/persisted.
    [Test]
    public async Task REQ110_WarmAsync_FirstAttemptTechnicalFailureSecondAttemptSucceeds_NotCountedAsTechnicalFailure()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(0));
        Assert.That(result.FailingPairs, Is.Empty);
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "the pair must actually have been retried once within the same run");
        // The retry's real (7-match, above threshold) result is what's used —
        // not treated as a below-threshold pair needing a confirmed-low marker.
        Assert.That(await _playerStoreRepository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"), Is.False);
    }

    // REQ-110 (2026-07-28): a pair that fails on BOTH attempts within a run
    // (MaxAttemptsPerPair = 2) must be counted as a technical failure exactly
    // once — not once per attempt.
    [Test]
    public async Task REQ110_WarmAsync_BothAttemptsFailWithinARun_CountedAsTechnicalFailureExactlyOnce()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Has.Count.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "both attempts must actually be made (the same-run retry), even though both fail");
    }

    // REQ-110 (2026-07-28 "persisted confirmed-low signal" extension): a
    // real (non-technical-failure) answer below MinValidAnswers must persist
    // a ConfirmedLowMatchPair marker for this pair — and, within the SAME
    // run that discovered it, PairsSkippedConfirmedLow stays 0 for it (it's
    // only skipped starting on a LATER run).
    [Test]
    public async Task REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "a pair confirmed low FOR THE FIRST TIME this run is still counted as queried live, not skipped, this run");
        Assert.That(await _playerStoreRepository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"), Is.True,
            "the confirmed-low marker must be persisted so a LATER run can skip it");
    }

    // REQ-110 (2026-07-28 "persisted confirmed-low signal" extension): a
    // pair previously confirmed low by an earlier run (ConfirmedLowMatchPair
    // already seeded) must be skipped on this run WITHOUT issuing any live
    // query at all.
    [Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        await _playerStoreRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }

    // REQ-110 (2026-07-28): the Club x Club loop's own confirmed-low skip
    // path — a separate code path from the Country x Club loop above
    // (PlayerCacheWarmingService's second `foreach`), needing its own
    // coverage rather than assuming symmetry.
    [Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the loop could check either (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — seeding both orders so this test doesn't
        // depend on which one the loop actually uses (same defensive
        // technique as this file's other order-independent assertions).
        await _playerStoreRepository.RecordConfirmedLowAsync("club", "Arsenal", "club", "Barcelona", matchCount: 0);
        await _playerStoreRepository.RecordConfirmedLowAsync("club", "Barcelona", "club", "Arsenal", matchCount: 0);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(0),
            "a Club x Club pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }

    // REQ-110 (2026-07-28 "cache-warming-specific timeout" extension): the
    // cache-warming path must ask for WikidataQueryTimeoutTier.CacheWarming
    // explicitly, not the Default tier round generation/guess-time fallback
    // use — see WikidataClientTests.cs for the timeout VALUE this tier
    // resolves to.
    [Test]
    public async Task REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }

    [Test]
    public async Task REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        var tier = _wikidataLookupService.GetClubClubLastTimeoutTier("Arsenal", "Barcelona")
            ?? _wikidataLookupService.GetClubClubLastTimeoutTier("Barcelona", "Arsenal");
        Assert.That(tier, Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
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

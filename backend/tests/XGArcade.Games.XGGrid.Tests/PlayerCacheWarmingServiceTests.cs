using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.Data.Seeding;
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
    // S-106/S-107 (pure refactor): PlayerCacheWarmingService's own
    // CountPlayersWithBothAttributesAsync lives on IPlayerAttributeRepository;
    // ConfirmedLowMatchPair/PairLookupFailure live on
    // IPlayerDataQualityRepository (see ADR-0067). _playerOverrideRepository
    // is only used to build FakeWikidataLookupService's own persistence path
    // below (HasEffectiveAttributeAsync), same as playerRepository.
    private IPlayerDataQualityRepository _playerDataQualityRepository = null!;
    private IPlayerOverrideRepository _playerOverrideRepository = null!;
    private IPlayerAttributeRepository _playerAttributeRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private FakeWikidataLookupService _wikidataLookupService = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _playerDataQualityRepository = new PlayerDataQualityRepository(_dbContext);
        _playerOverrideRepository = new PlayerOverrideRepository(_dbContext);
        _playerAttributeRepository = new PlayerAttributeRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _wikidataLookupService = new FakeWikidataLookupService(_playerOverrideRepository, _playerRepository, _playerAttributeRepository);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerCacheWarmingService BuildService(int minValidAnswers) =>
        new(_categoryValueRepository, _playerDataQualityRepository, _playerAttributeRepository, _wikidataLookupService,
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
        // France x Arsenal: the run's one attempt hits a technical failure
        // (no same-run retry as of the 2026-08-01 extension — see
        // REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry).
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

    // REQ-110 (2026-08-01 "persistent technical-failure tracking"
    // extension, ADR-0052): the same-run retry this class previously had is
    // now GONE — a pair queried exactly once per run, whether it succeeds or
    // fails. This test pins that down directly: a failing pair makes exactly
    // one live call per WarmAsync invocation, not two.
    [Test]
    public async Task REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Has.Count.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1),
            "the same-run retry was removed 2026-08-01 (ADR-0052) — a failing pair costs exactly one live call, not two");
    }

    // REQ-110 (2026-08-01 "persistent technical-failure tracking"
    // extension, ADR-0052): a single run-level failure is recorded but must
    // NOT yet trigger the skip — PersistentFailureThreshold is 2, so a
    // one-off transient blip must still get a real, live chance on the very
    // next run.
    [Test]
    public async Task REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        // First run: fails once. Second run: no failure configured, and a
        // real match set — proves the pair is still queried live, not
        // skipped, after only one prior failure.
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsSkippedPersistentFailure, Is.EqualTo(0),
            "one prior run's failure must not be enough to skip — PersistentFailureThreshold is 2 consecutive runs");
        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "both runs' single attempts must actually have been made");
    }

    // REQ-110 (2026-08-01): the core fix — a pair that fails on 2
    // CONSECUTIVE runs is skipped on the third, without any live query at
    // all. This is what stops a structurally-doomed pair (e.g. the
    // club-club combinatorial-blowup incident this extension responds to)
    // from being re-attempted, at full cost, on every future run forever.
    [Test]
    public async Task REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "the third run must not issue a live call at all once the pair is skipped as a persistent failure");
    }

    // REQ-110 (2026-08-01): the Club x Club loop's own persistent-failure
    // skip path — a separate code path from the Country x Club loop above,
    // needing its own coverage rather than assuming symmetry (same
    // precedent as this file's existing confirmed-low Club x Club test).
    [Test]
    public async Task REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Arsenal", "Barcelona", attempts: 2);
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Barcelona", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();
        await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
    }

    // REQ-110 (2026-08-01): a pair that fails once, then gets a real answer,
    // must have its failure marker cleared — checked directly against
    // IsPersistentTechnicalFailureAsync rather than by forcing a further
    // WarmAsync failure: once a pair gets ANY real answer (match or
    // confirmed-low), WarmAsync's own cachedCount/IsConfirmedLowAsync
    // checks mean it is never live-queried again for that exact pair, so
    // there is no way to observe a "later, unrelated failure" through
    // WarmAsync itself — the marker's clearing has to be verified at the
    // repository level instead. matches below MinValidAnswers (a genuine,
    // non-technical-failure below-threshold answer) is used for the
    // recovery so the pair doesn't instead become "already valid" and
    // short-circuit the very re-check this test needs to observe.
    [Test]
    public async Task REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.True,
            "one technical failure must already be recorded before the recovery run");

        var secondRun = await service.WarmAsync();

        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(0));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.False,
            "the marker must be cleared once the pair gets a real answer, even a below-threshold one — otherwise a pair that recovers would still count toward a future skip");
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
        Assert.That(await _playerDataQualityRepository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"), Is.True,
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
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
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
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Arsenal", "club", "Barcelona", matchCount: 0);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Barcelona", "club", "Arsenal", matchCount: 0);
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

    // ---- REQ-110's own "Test level" note (docs/requirements-document.md,
    // lines ~473-477): a regression test proving that running REQ-111's
    // stale-QID cleanup (named or --all-clubs) or REQ-112/S-038's
    // purge-player-pool against a pair previously marked confirmed-low,
    // followed by a cache-warming run, re-queries that pair LIVE rather than
    // trusting the stale confirmed-low marker. StaleClubAttributeCleanerTests.cs
    // and this file's own REQ110_WarmAsync_PreviouslyConfirmedLowPair_
    // SkippedWithoutLiveQuery test each cover one half in isolation (the
    // cleaner deletes the right ConfirmedLowMatchPair rows; WarmAsync skips a
    // pair while one still exists) — neither chains "invalidate, THEN warm"
    // against the same pair, so a future change to either invalidation tool
    // that forgets to clear ConfirmedLowMatchPair would slip through both
    // suites with zero signal. See ADR-0050's Consequences section, which
    // names this exact risk.

    [Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        // Seed the state a prior WarmAsync run would have left behind: a
        // real confirmed-low marker for this exact pair, mirroring
        // PlayerCacheWarmingService's own nationality-then-club ordering.
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 1);

        // REQ-111 named-mode cleanup — e.g. after Napoli's WikidataQid was
        // corrected — must clear the stale marker alongside the stale
        // PlayerAttribute/PlayerData rows (already covered in isolation by
        // StaleClubAttributeCleanerTests.cs's own REQ110_CleanAsync_
        // RemovesConfirmedLowMatchPair_OnACountryClubPairsClubSide).
        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        // Configure the fake so a live query for this pair would actually
        // return a real answer if (and only if) WarmAsync issues one.
        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "cleaning the stale confirmed-low marker must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        var napoli = SeedClub("Napoli");
        // CleanAllSeededClubsAsync resolves its club-name list from
        // ClubDefinition — SeedClub above already adds the ClubDefinition
        // row, so no separate seeding step is needed here (unlike
        // StaleClubAttributeCleanerTests.cs's SeedClubDefinitionAsync helper,
        // which exists there only because that file's other tests seed
        // players without a matching ClubDefinition row at all).
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Id == napoli.Id), Is.True);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        // REQ-111 --all-clubs mode — e.g. after the 2026-07-17 truthy-wdt:P54
        // incident tainted every seeded club's cached data at once — must
        // also clear the stale marker (isolation coverage:
        // StaleClubAttributeCleanerTests.cs's REQ110_CleanAllSeededClubsAsync_
        // RemovesConfirmedLowMatchPairsForEverySeededClub).
        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "--all-clubs cleanup must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        // purge-player-pool is a CLI verb inside Program.cs's top-level
        // statements (Npgsql-only — it builds its own DbContextOptions with
        // .UseNpgsql(...) and requires the exact confirmation-phrase
        // argument), so it can't be invoked directly from a unit test. Its
        // ConfirmedLowMatchPair-clearing logic itself is exactly one line:
        //   await purgeDbContext.ConfirmedLowMatchPairs.ExecuteDeleteAsync();
        // (see Program.cs's `if (args is ["purge-player-pool", ..])` block).
        // ExecuteDeleteAsync is a relational-provider bulk operation not
        // supported by the InMemory provider this test (and this whole file)
        // uses, so RemoveRange + SaveChangesAsync below is used as a faithful
        // proxy for it — both leave the table in the exact same end state
        // (zero ConfirmedLowMatchPair rows), which is the only thing this
        // regression test or WarmAsync's downstream behavior can observe.
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        var staleConfirmedLow = await _dbContext.ConfirmedLowMatchPairs.ToListAsync();
        _dbContext.ConfirmedLowMatchPairs.RemoveRange(staleConfirmedLow);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped ConfirmedLowMatchPair delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }

    // REQ-110 (2026-08-01 "persistent technical-failure tracking"
    // extension, ADR-0052): the same purge-player-pool regression as
    // REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_
    // ReQueriesPreviouslyConfirmedLowPairLive above, for PairLookupFailure —
    // purge-player-pool's unscoped delete (Program.cs:
    // `await purgeDbContext.PairLookupFailures.ExecuteDeleteAsync();`) must
    // also stop WarmAsync from trusting a persistent-failure marker left
    // over from before the purge. Same InMemory-provider RemoveRange proxy
    // for ExecuteDeleteAsync as the test above.
    [Test]
    public async Task REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");

        var staleLookupFailures = await _dbContext.PairLookupFailures.ToListAsync();
        _dbContext.PairLookupFailures.RemoveRange(staleLookupFailures);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped PairLookupFailure delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedPersistentFailure, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
}

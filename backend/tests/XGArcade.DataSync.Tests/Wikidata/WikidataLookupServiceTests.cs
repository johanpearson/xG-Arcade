using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;
using XGArcade.TestSupport;

namespace XGArcade.DataSync.Tests.Wikidata;

// REQ-103 (live-fetch fallback for missing data), Tier 0 half: mocked-HTTP
// tests per docs/backlog.md S-006's acceptance criteria — a hit persists
// players + aliases, re-running the same query creates zero duplicate
// Players, and timeout/no-match returns empty without throwing.
public class WikidataLookupServiceTests
{
    private static readonly CountryDefinition France = new() { Id = Guid.NewGuid(), Name = "France", WikidataQid = "Q142" };
    private static readonly ClubDefinition Arsenal = new() { Id = Guid.NewGuid(), Name = "Arsenal", WikidataQid = "Q9617" };
    // MVP-SCOPE.md seeded QID — a second, distinct cell (France x Barcelona)
    // for the "same player resolved by two different cells" upsert test.
    private static readonly ClubDefinition Barcelona = new() { Id = Guid.NewGuid(), Name = "Barcelona", WikidataQid = "Q7156" };
    // S-030: a third club, so Club x Club tests have two distinct clubs to
    // pair (Barcelona x RealMadrid) without reusing Arsenal/Barcelona's
    // existing Country x Club role above in a way that could blur which
    // scenario a given test is exercising.
    private static readonly ClubDefinition RealMadrid = new() { Id = Guid.NewGuid(), Name = "Real Madrid", WikidataQid = "Q8682" };
    // S-031/REQ-108: Ballon d'Or — same QID as ReferenceDataSeeder, NOT
    // independently verified against a live Wikidata page this session.
    private static readonly TrophyDefinition BallonDor = new() { Id = Guid.NewGuid(), Name = "Ballon d'Or", WikidataQid = "Q166177" };
    // REQ-114/ADR-0035: same QID as ReferenceDataSeeder, NOT independently
    // verified against a live Wikidata page this session.
    private static readonly CountryDefinition England = new()
    {
        Id = Guid.NewGuid(), Name = "England", WikidataQid = "Q21", UsesCountryForSportProperty = true,
    };
    // ADR-0061: same QID as ReferenceDataSeeder, NOT independently verified
    // against a live Wikidata page this session.
    private static readonly TrophyDefinition WorldCup = new()
    {
        Id = Guid.NewGuid(), Name = "FIFA World Cup", WikidataQid = "Q19317", IsTeamTrophy = true,
    };

    private const string SingleHenryMatchJson = """
        {
          "results": {
            "bindings": [
              { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "Titi" } }
            ]
          }
        }
        """;

    private const string TwoDistinctPlayersMatchJson = """
        {
          "results": {
            "bindings": [
              { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "Titi" } },
              { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q182804" }, "playerLabel": { "type": "literal", "value": "Nicolas Anelka" } }
            ]
          }
        }
        """;

    private const string NoMatchJson = """{ "results": { "bindings": [] } }""";

    // ---- ADR-0042/S-079: PlayerCareerStint fixtures ------------------------

    // A single P54 statement's P580/P582/P1350 qualifiers alongside the
    // usual player binding.
    private const string SingleHenryMatchWithCareerStintJson = """
        {
          "results": {
            "bindings": [
              {
                "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                "alias": { "type": "literal", "value": "Titi" },
                "startTime": { "type": "literal", "value": "2010-08-01T00:00:00Z" },
                "endTime": { "type": "literal", "value": "2015-06-30T00:00:00Z" },
                "numberOfMatches": { "type": "literal", "value": "100" }
              }
            ]
          }
        }
        """;

    // Same shape, no "numberOfMatches" binding (P1350 not present on this
    // statement).
    private const string SingleHenryMatchWithCareerStintNoAppearanceCountJson = """
        {
          "results": {
            "bindings": [
              {
                "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                "startTime": { "type": "literal", "value": "2010-08-01T00:00:00Z" },
                "endTime": { "type": "literal", "value": "2015-06-30T00:00:00Z" }
              }
            ]
          }
        }
        """;

    // Two distinct stints for the same player, returned in REVERSE
    // chronological order (the later stint's row comes first) — proves
    // SequenceOrder is resolved by date, not by response row order.
    private const string TwoCareerStintsOutOfResponseOrderJson = """
        {
          "results": {
            "bindings": [
              {
                "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                "startTime": { "type": "literal", "value": "2012-08-01T00:00:00Z" }, "endTime": { "type": "literal", "value": "2014-06-30T00:00:00Z" }, "numberOfMatches": { "type": "literal", "value": "40" }
              },
              {
                "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                "startTime": { "type": "literal", "value": "1999-08-01T00:00:00Z" }, "endTime": { "type": "literal", "value": "2007-06-30T00:00:00Z" }, "numberOfMatches": { "type": "literal", "value": "254" }
              }
            ]
          }
        }
        """;

    // A single, chronologically EARLIER stint for the same player (Q1519)
    // than SingleHenryMatchWithCareerStintJson's 2010-2015 — used to prove
    // the re-sequencing behavior when a later-discovered stint precedes an
    // already-persisted one.
    private const string SingleHenryEarlierCareerStintJson = """
        {
          "results": {
            "bindings": [
              {
                "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" },
                "startTime": { "type": "literal", "value": "1999-08-01T00:00:00Z" }, "endTime": { "type": "literal", "value": "2007-06-30T00:00:00Z" }, "numberOfMatches": { "type": "literal", "value": "254" }
              }
            ]
          }
        }
        """;

    // REQ-214: same single-match shape as SingleHenryMatchJson, plus a P18
    // photo binding.
    private const string SingleHenryMatchWithPhotoJson = """
        {
          "results": {
            "bindings": [
              { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "Titi" }, "photo": { "type": "uri", "value": "http://commons.wikimedia.org/wiki/Special:FilePath/Thierry%20Henry.jpg" } }
            ]
          }
        }
        """;

    // REQ-1207/S-082: same single-match shape as SingleHenryMatchJson, plus
    // P413 (position) and a P569 (dateOfBirth) binding to derive BirthYear
    // from. "positionLabel", not "position" (bug fix, 2026-08-02) — see
    // WikidataClient.BuildIntersectionQuery's own comment for why ?position
    // alone is a raw QID URI, never the human-readable string ParseBindings
    // actually reads.
    private const string SingleHenryMatchWithPositionAndBirthYearJson = """
        {
          "results": {
            "bindings": [
              { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "Titi" }, "positionLabel": { "type": "literal", "value": "forward" }, "dateOfBirth": { "type": "literal", "value": "1977-08-17T00:00:00Z" } }
            ]
          }
        }
        """;

    // REQ-1207: a DIFFERENT position/birth year for the SAME player
    // (Q1519) — used only by the set-once test below, to prove a later sync
    // never overwrites an already-persisted Player row's Position/BirthYear,
    // even when the later sync's own response disagrees with what's already
    // stored.
    private const string SingleHenryMatchWithDifferentPositionAndBirthYearJson = """
        {
          "results": {
            "bindings": [
              { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "Titi" }, "positionLabel": { "type": "literal", "value": "midfielder" }, "dateOfBirth": { "type": "literal", "value": "1980-01-01T00:00:00Z" } }
            ]
          }
        }
        """;

    private XGArcadeDbContext _dbContext = null!;
    private IPlayerStoreRepository _playerStore = null!;
    // S-106 (pure refactor): the four sibling repositories carrying the
    // methods split out of IPlayerStoreRepository that WikidataLookupService's
    // PersistMatchesAsync needs — _playerStore above is kept for
    // GetCareerStintsByPlayerIdsAsync/AddCareerStintsBatchAsync
    // (PersistCareerStintsAsync), which haven't moved.
    private IPlayerRepository _playerRepository = null!;
    private IPlayerAttributeRepository _playerAttributeRepository = null!;
    private IPlayerAliasRepository _playerAliasRepository = null!;
    private IPlayerDataRepository _playerDataRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerStore = new PlayerStoreRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _playerAttributeRepository = new PlayerAttributeRepository(_dbContext);
        _playerAliasRepository = new PlayerAliasRepository(_dbContext);
        _playerDataRepository = new PlayerDataRepository(_dbContext);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private IWikidataLookupService BuildService(string responseJson, TimeSpan? queryTimeout = null)
    {
        var httpClient = new HttpClient(FakeHttpMessageHandler.ReturningJson(responseJson))
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(httpClient, queryTimeout);
        return new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);
    }

    // REQ-114/ADR-0035: same as BuildService, but also hands back the
    // FakeHttpMessageHandler so a test can inspect the actual SPARQL query
    // sent (handler.LastRequest) — needed to assert which of the two query
    // paths (P27 vs. P1532) WikidataLookupService dispatched to.
    private (IWikidataLookupService Service, FakeHttpMessageHandler Handler) BuildServiceWithHandler(string responseJson)
    {
        var handler = FakeHttpMessageHandler.ReturningJson(responseJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://query.wikidata.org/") };
        var wikidataClient = new WikidataClient(httpClient);
        return (new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository), handler);
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_HitPersistsPlayersAndAliases()
    {
        var service = BuildService(SingleHenryMatchJson);

        var result = await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        Assert.That(result, Has.Count.EqualTo(1));
        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(player.FullName, Is.EqualTo("Thierry Henry"));

        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(attributes, Has.Count.EqualTo(2));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "nationality" && a.AttributeValue == "France"));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "club" && a.AttributeValue == "Arsenal"));

        var rawData = await _dbContext.PlayerData.Where(d => d.PlayerId == player.Id).ToListAsync();
        // Has.Count first: Has.All.Matches alone would pass vacuously if only
        // one of the two attribute writes (nationality, club) actually landed.
        Assert.That(rawData, Has.Count.EqualTo(2));
        // ADR-0032: both origins persist as "verified" now — see
        // REQ211_LookupAndPersistAsync_GuessTimeFallback_PersistsAsVerified
        // below for the (now identical) GuessTimeFallback case.
        Assert.That(rawData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "verified"));

        var aliases = await _dbContext.PlayerAliases.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(aliases, Has.Count.EqualTo(1));
        Assert.That(aliases[0].Alias, Is.EqualTo("Titi"));
    }

    // ADR-0032 (supersedes ADR-0029): the guess-time fallback (ADR-0018)
    // re-checks a single already-generated cell against a specific player's
    // guess, not the original vetted per-category intersection — ADR-0029
    // had kept this reviewable ("unverified"); ADR-0032 reverses that and
    // auto-verifies it the same as a Sync-origin call above.
    [Test]
    public async Task REQ211_LookupAndPersistAsync_GuessTimeFallback_PersistsAsVerified()
    {
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.GuessTimeFallback);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var rawData = await _dbContext.PlayerData.Where(d => d.PlayerId == player.Id).ToListAsync();
        Assert.That(rawData, Has.Count.EqualTo(2));
        Assert.That(rawData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "verified"));
    }

    [Test]
    public async Task REQ214_LookupAndPersistAsync_HitWithPhoto_PersistsPlayerPhotoUrl()
    {
        var service = BuildService(SingleHenryMatchWithPhotoJson);

        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(player.PhotoUrl, Is.EqualTo("http://commons.wikimedia.org/wiki/Special:FilePath/Thierry%20Henry.jpg"));
    }

    [Test]
    public async Task REQ214_LookupAndPersistAsync_HitWithoutPhoto_PlayerPhotoUrlIsNull()
    {
        // SingleHenryMatchJson has no "photo" binding — the normal,
        // error-free "no Wikidata photo" case.
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(player.PhotoUrl, Is.Null);
    }

    // ---- REQ-1207/S-082: Player.Position/BirthYear sourced from Wikidata --

    [Test]
    public async Task REQ1207_LookupAndPersistAsync_HitWithPositionAndDateOfBirth_PersistsPositionAndBirthYearOnNewPlayer()
    {
        var service = BuildService(SingleHenryMatchWithPositionAndBirthYearJson);

        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(player.Position, Is.EqualTo("forward"));
        Assert.That(player.BirthYear, Is.EqualTo(1977));
    }

    [Test]
    public async Task REQ1207_LookupAndPersistAsync_HitWithoutPosition_PlayerPositionIsNull()
    {
        // SingleHenryMatchJson has no "position" binding at all — the
        // normal, error-free "no Wikidata P413 statement" case.
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(player.Position, Is.Null);
    }

    [Test]
    public async Task REQ1207_LookupAndPersistAsync_ExistingPlayer_LaterSyncWithDifferentPositionAndBirthYear_LeavesOriginalValuesCompletelyUntouched()
    {
        // The set-once contract: Position/BirthYear are written only at
        // Player-row creation, mirroring PhotoUrl's own "never re-synced on
        // a later lookup" rule (PlayerStoreRepository.
        // GetOrCreatePlayersByWikidataQidAsync). Two genuinely separate
        // LookupAndPersistAsync calls for the SAME player (Q1519), the
        // second one's response disagreeing with the first — the second
        // call's Position/BirthYear must be silently ignored.
        var firstSyncService = BuildService(SingleHenryMatchWithPositionAndBirthYearJson);
        var secondSyncService = BuildService(SingleHenryMatchWithDifferentPositionAndBirthYearJson);

        await firstSyncService.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);
        await secondSyncService.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        Assert.That(await _dbContext.Players.CountAsync(p => p.WikidataQid == "Q1519"), Is.EqualTo(1), "still exactly one Player row, upserted, not duplicated");
        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(player.Position, Is.EqualTo("forward"), "the SECOND sync's 'midfielder' must never overwrite the value set at creation");
        Assert.That(player.BirthYear, Is.EqualTo(1977), "the SECOND sync's 1980 must never overwrite the value set at creation");
    }

    [Test]
    public async Task REQ1207_LookupAndPersistAsync_ExistingPlayerCreatedWithNullPositionAndBirthYear_LaterSyncWithRealValues_LeavesThemNull()
    {
        // The set-once rule applies regardless of whether the existing row's
        // current value is null or already set (REQ-1207's own text) — a
        // player first synced with no P413/dateOfBirth data stays null
        // forever unless a future dedicated backfill is built, the same way
        // PhotoUrl does.
        var firstSyncService = BuildService(SingleHenryMatchJson);
        var secondSyncService = BuildService(SingleHenryMatchWithPositionAndBirthYearJson);

        await firstSyncService.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);
        await secondSyncService.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(player.Position, Is.Null, "a null set at creation is never backfilled by a later sync");
        Assert.That(player.BirthYear, Is.Null, "a null set at creation is never backfilled by a later sync");
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_ReRunningSameQuery_CreatesZeroDuplicatePlayers()
    {
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);
        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var players = await _dbContext.Players.Where(p => p.WikidataQid == "Q1519").ToListAsync();
        Assert.That(players, Has.Count.EqualTo(1));

        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == players[0].Id).ToListAsync();
        Assert.That(attributes, Has.Count.EqualTo(2));

        var aliases = await _dbContext.PlayerAliases.Where(a => a.PlayerId == players[0].Id).ToListAsync();
        Assert.That(aliases, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_UpsertsExistingPlayerByWikidataQid_AcrossDifferentCells()
    {
        // Simulates the same player already cached from a previous,
        // different intersection query (e.g. Brazil x Barcelona) —
        // upserting by WikidataQid must reuse that Player row, never
        // insert a second one for the same real player.
        var existing = await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" });

        var service = BuildService(SingleHenryMatchJson);
        var result = await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(existing.Id));
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_SamePlayerFromTwoDifferentCells_UpsertsToOneRow()
    {
        // The stronger form of the upsert rule: not a pre-seeded row, but
        // two genuinely separate LookupAndPersistAsync calls for two
        // different cells (France x Arsenal, then France x Barcelona) whose
        // live Wikidata results happen to include the same real player
        // (Henry played for both). Must resolve to exactly one Player row
        // with the union of attributes, never two.
        var cell1Service = BuildService(SingleHenryMatchJson);
        var cell2Service = BuildService(SingleHenryMatchJson);

        await cell1Service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);
        var secondResult = await cell2Service.LookupAndPersistAsync(France, Barcelona, WikidataLookupOrigin.Sync);

        Assert.That(await _dbContext.Players.CountAsync(p => p.WikidataQid == "Q1519"), Is.EqualTo(1));
        Assert.That(secondResult, Has.Count.EqualTo(1));

        var playerId = secondResult[0].Id;
        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == playerId).ToListAsync();
        Assert.That(attributes, Has.Count.EqualTo(3));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "nationality" && a.AttributeValue == "France"));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "club" && a.AttributeValue == "Arsenal"));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "club" && a.AttributeValue == "Barcelona"));

        // Same alias ("Titi") returned by both cells — still only one row.
        var aliases = await _dbContext.PlayerAliases.Where(a => a.PlayerId == playerId).ToListAsync();
        Assert.That(aliases, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_TwoDistinctPlayersInOneQuery_PersistsEachSeparately()
    {
        // No LIMIT means a single cell's intersection query can legitimately
        // return many distinct players (implementation-document.md §6a) —
        // the persistence loop must not silently drop any of them.
        var service = BuildService(TwoDistinctPlayersMatchJson);

        var result = await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(2));

        var henry = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var anelka = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q182804");
        Assert.That(henry.FullName, Is.EqualTo("Thierry Henry"));
        Assert.That(anelka.FullName, Is.EqualTo("Nicolas Anelka"));

        var henryAttributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == henry.Id).ToListAsync();
        var anelkaAttributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == anelka.Id).ToListAsync();
        Assert.That(henryAttributes, Has.Count.EqualTo(2));
        Assert.That(anelkaAttributes, Has.Count.EqualTo(2));

        var henryAliases = await _dbContext.PlayerAliases.Where(a => a.PlayerId == henry.Id).ToListAsync();
        var anelkaAliases = await _dbContext.PlayerAliases.Where(a => a.PlayerId == anelka.Id).ToListAsync();
        Assert.That(henryAliases, Has.Count.EqualTo(1));
        Assert.That(anelkaAliases, Is.Empty);
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_WhenWikidataTimesOut_ReturnsEmptyWithoutThrowing()
    {
        var httpClient = new HttpClient(FakeHttpMessageHandler.NeverResponding())
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(httpClient, queryTimeout: TimeSpan.FromMilliseconds(50));
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        IReadOnlyList<Player>? result = null;
        Assert.DoesNotThrowAsync(async () => result = await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync));

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    // REQ-211 (2026-07-27 fix): the guess-time fallback's own opt-in —
    // unlike REQ103_LookupAndPersistAsync_WhenWikidataTimesOut_ReturnsEmptyWithoutThrowing
    // above (Sync origin, unaffected), a GuessTimeFallback-origin call must
    // THROW on a timeout instead of swallowing to [], so
    // GridGameModule.RefreshCellFromLiveLookupAsync can distinguish "we
    // don't know yet" from a genuine no-match.
    [Test]
    public void REQ211_LookupAndPersistAsync_GuessTimeFallback_WhenWikidataTimesOut_ThrowsWikidataQueryException()
    {
        var httpClient = new HttpClient(FakeHttpMessageHandler.NeverResponding())
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        // ADR-0046 follow-up: GuessTimeFallback uses guessTimeFallbackQueryTimeout,
        // not queryTimeout — must be set here too, or this test would wait
        // out the real (28s default) budget.
        var wikidataClient = new WikidataClient(
            httpClient, queryTimeout: TimeSpan.FromMilliseconds(50), guessTimeFallbackQueryTimeout: TimeSpan.FromMilliseconds(50));
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        Assert.ThrowsAsync<WikidataQueryException>(async () =>
            await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.GuessTimeFallback));
    }

    // S-030 mirror of the test above — the Club x Club dispatch path
    // (RefreshCellFromLiveLookupAsync's other Tier-0-handled pairing) needs
    // its own coverage rather than assuming symmetry with Country x Club.
    [Test]
    public void REQ211_LookupAndPersistClubClubAsync_GuessTimeFallback_WhenWikidataTimesOut_ThrowsWikidataQueryException()
    {
        var httpClient = new HttpClient(FakeHttpMessageHandler.NeverResponding())
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(
            httpClient, queryTimeout: TimeSpan.FromMilliseconds(50), guessTimeFallbackQueryTimeout: TimeSpan.FromMilliseconds(50));
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        Assert.ThrowsAsync<WikidataQueryException>(async () =>
            await service.LookupAndPersistClubClubAsync(Barcelona, RealMadrid, WikidataLookupOrigin.GuessTimeFallback));
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_WhenNoMatch_ReturnsEmptyWithoutThrowing()
    {
        var service = BuildService(NoMatchJson);

        var result = await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_UnresolvedCountryQid_SkipsWikidataAndReturnsEmpty()
    {
        // REQ-109: a null QID isn't an error — it just means Wikidata is
        // skipped for this value (the Tier 1 API-Football fallback doesn't
        // need a QID at all).
        var unresolvedCountry = new CountryDefinition { Id = Guid.NewGuid(), Name = "Ruritania", WikidataQid = null };
        var httpClient = new HttpClient(FakeHttpMessageHandler.ReturningJson(SingleHenryMatchJson))
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(httpClient);
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        var result = await service.LookupAndPersistAsync(unresolvedCountry, Arsenal, WikidataLookupOrigin.Sync);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_UnresolvedClubQid_SkipsWikidataAndReturnsEmpty()
    {
        // Mirror of the unresolved-country case above — the null check is
        // an OR across both values (REQ-109), so the club-only branch needs
        // its own coverage rather than assuming symmetry with country.
        var unresolvedClub = new ClubDefinition { Id = Guid.NewGuid(), Name = "Ruritania FC", WikidataQid = null };
        var httpClient = new HttpClient(FakeHttpMessageHandler.ReturningJson(SingleHenryMatchJson))
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(httpClient);
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        var result = await service.LookupAndPersistAsync(France, unresolvedClub, WikidataLookupOrigin.Sync);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    // ---- LookupAndPersistAsync: PlayerCareerStint persistence (ADR-0042/S-079) -
    // Only LookupAndPersistAsync (country/nationality x club) wires this up —
    // see that method's own comment and its scope note on the other three
    // Lookup*Async callers.

    [Test]
    public async Task REQ103_LookupAndPersistAsync_HitWithCareerStintQualifiers_PersistsOneOrderedPlayerCareerStintRow()
    {
        var service = BuildService(SingleHenryMatchWithCareerStintJson);

        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var stints = await _dbContext.PlayerCareerStints.Where(s => s.PlayerId == player.Id).ToListAsync();
        Assert.That(stints, Has.Count.EqualTo(1));
        Assert.That(stints[0].ClubName, Is.EqualTo("Arsenal"));
        Assert.That(stints[0].StartYear, Is.EqualTo(2010));
        Assert.That(stints[0].EndYear, Is.EqualTo(2015));
        Assert.That(stints[0].AppearanceCount, Is.EqualTo(100));
        Assert.That(stints[0].SequenceOrder, Is.EqualTo(0));

        // Confirm-by-inspection (S-079's accept criteria): PlayerAttribute's
        // existing "club"/"nationality" rows are unaffected — populated
        // alongside, never instead of, PlayerCareerStint.
        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(attributes, Has.Count.EqualTo(2));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "nationality" && a.AttributeValue == "France"));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "club" && a.AttributeValue == "Arsenal"));
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_CareerStintMissingP1350_AppearanceCountIsNullNeverZero()
    {
        var service = BuildService(SingleHenryMatchWithCareerStintNoAppearanceCountJson);

        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var stint = await _dbContext.PlayerCareerStints.SingleAsync(s => s.PlayerId == player.Id);
        Assert.That(stint.AppearanceCount, Is.Null);
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_HitWithNoCareerStintQualifiers_PersistsNoPlayerCareerStintRow()
    {
        // SingleHenryMatchJson has no startTime/endTime/numberOfMatches
        // bindings at all — the normal "this Wikidata statement has no
        // qualifiers" case, not an error.
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(await _dbContext.PlayerCareerStints.CountAsync(s => s.PlayerId == player.Id), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_SequenceOrderReflectsChronologicalOrder_RegardlessOfResponseRowOrder()
    {
        // TwoCareerStintsOutOfResponseOrderJson returns the LATER stint
        // (2012-2014) as the first binding row and the EARLIER stint
        // (1999-2007) second — SequenceOrder must still be resolved by date.
        var service = BuildService(TwoCareerStintsOutOfResponseOrderJson);

        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var stints = await _dbContext.PlayerCareerStints
            .Where(s => s.PlayerId == player.Id)
            .OrderBy(s => s.SequenceOrder)
            .ToListAsync();

        Assert.That(stints, Has.Count.EqualTo(2));
        Assert.That(stints[0].StartYear, Is.EqualTo(1999));
        Assert.That(stints[0].SequenceOrder, Is.EqualTo(0));
        Assert.That(stints[1].StartYear, Is.EqualTo(2012));
        Assert.That(stints[1].SequenceOrder, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_ReRunningSameQuery_CreatesZeroDuplicateCareerStintRows()
    {
        var service = BuildService(SingleHenryMatchWithCareerStintJson);

        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);
        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var stints = await _dbContext.PlayerCareerStints.Where(s => s.PlayerId == player.Id).ToListAsync();
        Assert.That(stints, Has.Count.EqualTo(1));
    }

    // Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203 follow-up,
    // ADR-0059): the cross-writer half of the fix. LookupAndPersistAsync
    // (xG Grid's country x club byproduct path, this test's first call)
    // always writes ClubDefinition.Name directly and needed no change.
    // PlayerCareerStintRefreshService (xG Path's full-career-fetch path,
    // this test's second call) used to write Wikidata's own raw
    // (suffix-normalized-only) ?clubLabel — which, for the SAME real club
    // (same WikidataQid), can legitimately differ from the seeded name by
    // more than a legal suffix (e.g. "Arsenal" vs. a hypothetical
    // "Arsenal Football Club" alternate label). Once
    // PlayerCareerStintRefreshService canonicalizes by QID, both writers
    // converge on the exact same ClubName for the exact same real stint,
    // so the second writer's dedup check (against what LookupAndPersistAsync
    // already persisted) recognizes it as already-known rather than
    // creating a second, differently-named row.
    [Test]
    public async Task REQ1203_TwoWriterPathsForSameRealStint_ConvergeOnIdenticalClubName_NoCrossWriterDuplicate()
    {
        var categoryValueRepository = new CategoryValueRepository(_dbContext);
        await categoryValueRepository.AddClubAsync(Arsenal); // Name="Arsenal", WikidataQid="Q9617".

        // Writer 1: xG Grid's own country x club byproduct lookup — always
        // persists ClubDefinition.Name ("Arsenal") directly.
        var lookupService = BuildService(SingleHenryMatchWithCareerStintJson);
        await lookupService.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");

        // Writer 2: xG Path's full-career fetch — same real stint (same
        // dates/appearance count), same underlying QID, but Wikidata's own
        // raw label differs from the seeded name.
        var fakeWikidataClient = new FakeWikidataClient();
        fakeWikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Arsenal Football Club", 2010, 2015, 100, ClubQid: "Q9617"));
        var refreshService = new PlayerCareerStintRefreshService(
            fakeWikidataClient, _playerStore, _playerRepository, categoryValueRepository,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayerCareerStintRefreshService>.Instance);

        await refreshService.RefreshCareerStintsAsync([player.Id]);

        var stints = await _dbContext.PlayerCareerStints.Where(s => s.PlayerId == player.Id).ToListAsync();
        Assert.That(stints, Has.Count.EqualTo(1),
            "the two writer paths must converge on the exact same ClubName for the same real stint, not create a second row");
        Assert.That(stints[0].ClubName, Is.EqualTo("Arsenal"));
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_PlayerGainsChronologicallyEarlierStintLater_ResequencesWholeSet()
    {
        // First cell (France x Arsenal) discovers the 2010-2015 stint;
        // second cell (France x Barcelona) later discovers a chronologically
        // EARLIER stint (1999-2007) for the same real player (Q1519). The
        // already-persisted Arsenal row's SequenceOrder must shift from 0 to
        // 1, not stay stuck at 0.
        var cell1Service = BuildService(SingleHenryMatchWithCareerStintJson);
        await cell1Service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var arsenalStintBefore = await _dbContext.PlayerCareerStints.SingleAsync(s => s.PlayerId == player.Id);
        Assert.That(arsenalStintBefore.SequenceOrder, Is.EqualTo(0));

        var cell2Service = BuildService(SingleHenryEarlierCareerStintJson);
        await cell2Service.LookupAndPersistAsync(France, Barcelona, WikidataLookupOrigin.Sync);

        var stints = await _dbContext.PlayerCareerStints
            .Where(s => s.PlayerId == player.Id)
            .OrderBy(s => s.SequenceOrder)
            .ToListAsync();

        Assert.That(stints, Has.Count.EqualTo(2));
        Assert.That(stints[0].ClubName, Is.EqualTo("Barcelona"));
        Assert.That(stints[0].StartYear, Is.EqualTo(1999));
        Assert.That(stints[0].SequenceOrder, Is.EqualTo(0));
        Assert.That(stints[1].ClubName, Is.EqualTo("Arsenal"));
        Assert.That(stints[1].StartYear, Is.EqualTo(2010));
        Assert.That(stints[1].SequenceOrder, Is.EqualTo(1),
            "the already-persisted Arsenal row must be re-sequenced, not left at its original SequenceOrder");
    }

    [Test]
    public async Task REQ103_LookupAndPersistClubClubAsync_DoesNotPersistPlayerCareerStint()
    {
        // Scope decision (ADR-0042/S-079): only the country/nationality x
        // club path persists career stints in this story.
        var service = BuildService(SingleHenryMatchWithCareerStintJson);

        await service.LookupAndPersistClubClubAsync(Barcelona, RealMadrid, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(await _dbContext.PlayerCareerStints.CountAsync(s => s.PlayerId == player.Id), Is.EqualTo(0));
    }

    // ---- LookupAndPersistAsync: national-team query path (REQ-114/ADR-0035) -
    // England/Scotland/Wales/Northern Ireland — a second query path
    // (Wikidata P1532, "country for sport"), dispatched from the SAME
    // LookupAndPersistAsync entry point every other country uses, based
    // purely on `CountryDefinition.UsesCountryForSportProperty`.

    [Test]
    public async Task REQ114_LookupAndPersistAsync_NationalTeamCountry_SentQuery_UsesP1532NotP27()
    {
        var (service, handler) = BuildServiceWithHandler(NoMatchJson);

        await service.LookupAndPersistAsync(England, Arsenal, WikidataLookupOrigin.Sync);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P1532 wd:Q21"));
        Assert.That(sentQuery, Does.Not.Contain("P27"),
            "a flagged country must query P1532 exclusively — P27 can't distinguish England from the rest of the United Kingdom");
    }

    [Test]
    public async Task REQ114_LookupAndPersistAsync_OrdinaryCountry_SentQuery_UsesP27NotP1532()
    {
        // The existing P27 path must stay completely unaffected by this
        // feature for every country that doesn't opt in — France has
        // UsesCountryForSportProperty = false (the default).
        var (service, handler) = BuildServiceWithHandler(NoMatchJson);

        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P27 wd:Q142"));
        Assert.That(sentQuery, Does.Not.Contain("P1532"));
    }

    [Test]
    public async Task REQ114_LookupAndPersistAsync_NationalTeamCountry_HitPersistsUnderNationalityAttributeType()
    {
        // A national-team value like "England" is just another value in the
        // same "nationality" AttributeType vocabulary as "United Kingdom" —
        // not a conceptually different attribute type, and no different
        // from the P27 path's persistence shape.
        var service = BuildService(SingleHenryMatchJson);

        var result = await service.LookupAndPersistAsync(England, Arsenal, WikidataLookupOrigin.Sync);

        Assert.That(result, Has.Count.EqualTo(1));
        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "nationality" && a.AttributeValue == "England"));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "club" && a.AttributeValue == "Arsenal"));
    }

    [Test]
    public async Task REQ114_LookupAndPersistAsync_NationalTeamCountry_UnresolvedQid_SkipsWikidataAndReturnsEmpty()
    {
        // Same REQ-109 "unresolved QID isn't an error" contract applies
        // regardless of which query path a resolved QID would have used.
        var unresolvedNationalTeam = new CountryDefinition
        {
            Id = Guid.NewGuid(), Name = "Ruritania National Team", WikidataQid = null, UsesCountryForSportProperty = true,
        };
        var service = BuildService(SingleHenryMatchJson);

        var result = await service.LookupAndPersistAsync(unresolvedNationalTeam, Arsenal, WikidataLookupOrigin.Sync);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    // ---- LookupAndPersistClubClubAsync (S-030) ------------------------------
    // Mirrors every LookupAndPersistAsync test above — same persistence code
    // path (PersistMatchesAsync), just both attribute type/value pairs are
    // AttributeType "club" instead of "nationality"+"club".

    [Test]
    public async Task REQ103_LookupAndPersistClubClubAsync_HitPersistsPlayersAndAliases()
    {
        var service = BuildService(SingleHenryMatchJson);

        var result = await service.LookupAndPersistClubClubAsync(Barcelona, RealMadrid, WikidataLookupOrigin.Sync);

        Assert.That(result, Has.Count.EqualTo(1));
        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(player.FullName, Is.EqualTo("Thierry Henry"));

        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(attributes, Has.Count.EqualTo(2));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "club" && a.AttributeValue == "Barcelona"));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "club" && a.AttributeValue == "Real Madrid"));

        var rawData = await _dbContext.PlayerData.Where(d => d.PlayerId == player.Id).ToListAsync();
        Assert.That(rawData, Has.Count.EqualTo(2));
        // ADR-0032: see the mirrored note on REQ103_LookupAndPersistAsync_HitPersistsPlayersAndAliases above.
        Assert.That(rawData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "verified"));

        var aliases = await _dbContext.PlayerAliases.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(aliases, Has.Count.EqualTo(1));
        Assert.That(aliases[0].Alias, Is.EqualTo("Titi"));
    }

    // Mirrors REQ211_LookupAndPersistAsync_GuessTimeFallback_PersistsAsVerified above.
    [Test]
    public async Task REQ211_LookupAndPersistClubClubAsync_GuessTimeFallback_PersistsAsVerified()
    {
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistClubClubAsync(Barcelona, RealMadrid, WikidataLookupOrigin.GuessTimeFallback);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var rawData = await _dbContext.PlayerData.Where(d => d.PlayerId == player.Id).ToListAsync();
        Assert.That(rawData, Has.Count.EqualTo(2));
        Assert.That(rawData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "verified"));
    }

    [Test]
    public async Task REQ103_LookupAndPersistClubClubAsync_ReRunningSameQuery_CreatesZeroDuplicatePlayers()
    {
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistClubClubAsync(Barcelona, RealMadrid, WikidataLookupOrigin.Sync);
        await service.LookupAndPersistClubClubAsync(Barcelona, RealMadrid, WikidataLookupOrigin.Sync);

        var players = await _dbContext.Players.Where(p => p.WikidataQid == "Q1519").ToListAsync();
        Assert.That(players, Has.Count.EqualTo(1));

        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == players[0].Id).ToListAsync();
        Assert.That(attributes, Has.Count.EqualTo(2));

        var aliases = await _dbContext.PlayerAliases.Where(a => a.PlayerId == players[0].Id).ToListAsync();
        Assert.That(aliases, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task REQ103_LookupAndPersistClubClubAsync_UpsertsExistingPlayerByWikidataQid()
    {
        // Simulates the same player already cached from a previous, different
        // intersection query (e.g. a Country x Club cell) — upserting by
        // WikidataQid must reuse that Player row, never insert a second one.
        var existing = await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" });

        var service = BuildService(SingleHenryMatchJson);
        var result = await service.LookupAndPersistClubClubAsync(Barcelona, RealMadrid, WikidataLookupOrigin.Sync);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(existing.Id));
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ103_LookupAndPersistClubClubAsync_UnresolvedClubAQid_SkipsWikidataAndReturnsEmpty()
    {
        var unresolvedClub = new ClubDefinition { Id = Guid.NewGuid(), Name = "Ruritania FC", WikidataQid = null };
        var httpClient = new HttpClient(FakeHttpMessageHandler.ReturningJson(SingleHenryMatchJson))
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(httpClient);
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        var result = await service.LookupAndPersistClubClubAsync(unresolvedClub, RealMadrid, WikidataLookupOrigin.Sync);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ103_LookupAndPersistClubClubAsync_UnresolvedClubBQid_SkipsWikidataAndReturnsEmpty()
    {
        // Mirror of the unresolved-clubA case above — the null check is an
        // OR across both values, so the clubB-only branch needs its own
        // coverage rather than assuming symmetry with clubA.
        var unresolvedClub = new ClubDefinition { Id = Guid.NewGuid(), Name = "Ruritania FC", WikidataQid = null };
        var httpClient = new HttpClient(FakeHttpMessageHandler.ReturningJson(SingleHenryMatchJson))
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(httpClient);
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        var result = await service.LookupAndPersistClubClubAsync(Barcelona, unresolvedClub, WikidataLookupOrigin.Sync);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ103_LookupAndPersistClubClubAsync_WhenNoMatch_ReturnsEmptyWithoutThrowing()
    {
        var service = BuildService(NoMatchJson);

        var result = await service.LookupAndPersistClubClubAsync(Barcelona, RealMadrid, WikidataLookupOrigin.Sync);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ103_LookupAndPersistClubClubAsync_WhenWikidataTimesOut_ReturnsEmptyWithoutThrowing()
    {
        var httpClient = new HttpClient(FakeHttpMessageHandler.NeverResponding())
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(httpClient, queryTimeout: TimeSpan.FromMilliseconds(50));
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        IReadOnlyList<Player>? result = null;
        Assert.DoesNotThrowAsync(async () => result = await service.LookupAndPersistClubClubAsync(Barcelona, RealMadrid, WikidataLookupOrigin.Sync));

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    // ---- LookupAndPersistTrophyCountryAsync (S-031/REQ-108) ----------------
    // Mirrors LookupAndPersistAsync's tests above — same persistence code
    // path (PersistMatchesAsync), just AttributeType "trophy"/"nationality"
    // instead of "nationality"/"club".

    [Test]
    public async Task REQ108_LookupAndPersistTrophyCountryAsync_HitPersistsPlayersAndAliases()
    {
        var service = BuildService(SingleHenryMatchJson);

        var result = await service.LookupAndPersistTrophyCountryAsync(BallonDor, France, WikidataLookupOrigin.Sync);

        Assert.That(result, Has.Count.EqualTo(1));
        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(player.FullName, Is.EqualTo("Thierry Henry"));

        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(attributes, Has.Count.EqualTo(2));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "trophy" && a.AttributeValue == "Ballon d'Or"));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "nationality" && a.AttributeValue == "France"));

        var rawData = await _dbContext.PlayerData.Where(d => d.PlayerId == player.Id).ToListAsync();
        Assert.That(rawData, Has.Count.EqualTo(2));
        Assert.That(rawData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "verified"));

        var aliases = await _dbContext.PlayerAliases.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(aliases, Has.Count.EqualTo(1));
        Assert.That(aliases[0].Alias, Is.EqualTo("Titi"));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyCountryAsync_GuessTimeFallback_PersistsAsVerified()
    {
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistTrophyCountryAsync(BallonDor, France, WikidataLookupOrigin.GuessTimeFallback);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var rawData = await _dbContext.PlayerData.Where(d => d.PlayerId == player.Id).ToListAsync();
        Assert.That(rawData, Has.Count.EqualTo(2));
        Assert.That(rawData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "verified"));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyCountryAsync_ReRunningSameQuery_CreatesZeroDuplicatePlayers()
    {
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistTrophyCountryAsync(BallonDor, France, WikidataLookupOrigin.Sync);
        await service.LookupAndPersistTrophyCountryAsync(BallonDor, France, WikidataLookupOrigin.Sync);

        var players = await _dbContext.Players.Where(p => p.WikidataQid == "Q1519").ToListAsync();
        Assert.That(players, Has.Count.EqualTo(1));

        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == players[0].Id).ToListAsync();
        Assert.That(attributes, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyCountryAsync_UnresolvedTrophyQid_SkipsWikidataAndReturnsEmpty()
    {
        var unresolvedTrophy = new TrophyDefinition { Id = Guid.NewGuid(), Name = "Mystery Award", WikidataQid = null };
        var httpClient = new HttpClient(FakeHttpMessageHandler.ReturningJson(SingleHenryMatchJson))
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(httpClient);
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        var result = await service.LookupAndPersistTrophyCountryAsync(unresolvedTrophy, France, WikidataLookupOrigin.Sync);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyCountryAsync_UnresolvedCountryQid_SkipsWikidataAndReturnsEmpty()
    {
        var unresolvedCountry = new CountryDefinition { Id = Guid.NewGuid(), Name = "Ruritania", WikidataQid = null };
        var httpClient = new HttpClient(FakeHttpMessageHandler.ReturningJson(SingleHenryMatchJson))
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(httpClient);
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        var result = await service.LookupAndPersistTrophyCountryAsync(BallonDor, unresolvedCountry, WikidataLookupOrigin.Sync);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyCountryAsync_WhenNoMatch_ReturnsEmptyWithoutThrowing()
    {
        var service = BuildService(NoMatchJson);

        var result = await service.LookupAndPersistTrophyCountryAsync(BallonDor, France, WikidataLookupOrigin.Sync);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyCountryAsync_WhenWikidataTimesOut_ReturnsEmptyWithoutThrowing()
    {
        var httpClient = new HttpClient(FakeHttpMessageHandler.NeverResponding())
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(httpClient, queryTimeout: TimeSpan.FromMilliseconds(50));
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        IReadOnlyList<Player>? result = null;
        Assert.DoesNotThrowAsync(async () => result = await service.LookupAndPersistTrophyCountryAsync(BallonDor, France, WikidataLookupOrigin.Sync));

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    // ---- LookupAndPersistTrophyClubAsync (S-031/REQ-108) -------------------
    // Mirrors LookupAndPersistClubClubAsync's tests above — same persistence
    // code path, just AttributeType "trophy"/"club".

    [Test]
    public async Task REQ108_LookupAndPersistTrophyClubAsync_HitPersistsPlayersAndAliases()
    {
        var service = BuildService(SingleHenryMatchJson);

        var result = await service.LookupAndPersistTrophyClubAsync(BallonDor, RealMadrid, WikidataLookupOrigin.Sync);

        Assert.That(result, Has.Count.EqualTo(1));
        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(player.FullName, Is.EqualTo("Thierry Henry"));

        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(attributes, Has.Count.EqualTo(2));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "trophy" && a.AttributeValue == "Ballon d'Or"));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "club" && a.AttributeValue == "Real Madrid"));

        var rawData = await _dbContext.PlayerData.Where(d => d.PlayerId == player.Id).ToListAsync();
        Assert.That(rawData, Has.Count.EqualTo(2));
        Assert.That(rawData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "verified"));

        var aliases = await _dbContext.PlayerAliases.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(aliases, Has.Count.EqualTo(1));
        Assert.That(aliases[0].Alias, Is.EqualTo("Titi"));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyClubAsync_GuessTimeFallback_PersistsAsVerified()
    {
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistTrophyClubAsync(BallonDor, RealMadrid, WikidataLookupOrigin.GuessTimeFallback);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var rawData = await _dbContext.PlayerData.Where(d => d.PlayerId == player.Id).ToListAsync();
        Assert.That(rawData, Has.Count.EqualTo(2));
        Assert.That(rawData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "verified"));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyClubAsync_ReRunningSameQuery_CreatesZeroDuplicatePlayers()
    {
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistTrophyClubAsync(BallonDor, RealMadrid, WikidataLookupOrigin.Sync);
        await service.LookupAndPersistTrophyClubAsync(BallonDor, RealMadrid, WikidataLookupOrigin.Sync);

        var players = await _dbContext.Players.Where(p => p.WikidataQid == "Q1519").ToListAsync();
        Assert.That(players, Has.Count.EqualTo(1));

        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == players[0].Id).ToListAsync();
        Assert.That(attributes, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyClubAsync_UnresolvedTrophyQid_SkipsWikidataAndReturnsEmpty()
    {
        var unresolvedTrophy = new TrophyDefinition { Id = Guid.NewGuid(), Name = "Mystery Award", WikidataQid = null };
        var httpClient = new HttpClient(FakeHttpMessageHandler.ReturningJson(SingleHenryMatchJson))
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(httpClient);
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        var result = await service.LookupAndPersistTrophyClubAsync(unresolvedTrophy, RealMadrid, WikidataLookupOrigin.Sync);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyClubAsync_UnresolvedClubQid_SkipsWikidataAndReturnsEmpty()
    {
        var unresolvedClub = new ClubDefinition { Id = Guid.NewGuid(), Name = "Ruritania FC", WikidataQid = null };
        var httpClient = new HttpClient(FakeHttpMessageHandler.ReturningJson(SingleHenryMatchJson))
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(httpClient);
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        var result = await service.LookupAndPersistTrophyClubAsync(BallonDor, unresolvedClub, WikidataLookupOrigin.Sync);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyClubAsync_WhenNoMatch_ReturnsEmptyWithoutThrowing()
    {
        var service = BuildService(NoMatchJson);

        var result = await service.LookupAndPersistTrophyClubAsync(BallonDor, RealMadrid, WikidataLookupOrigin.Sync);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyClubAsync_WhenWikidataTimesOut_ReturnsEmptyWithoutThrowing()
    {
        var httpClient = new HttpClient(FakeHttpMessageHandler.NeverResponding())
        {
            BaseAddress = new Uri("https://query.wikidata.org/"),
        };
        var wikidataClient = new WikidataClient(httpClient, queryTimeout: TimeSpan.FromMilliseconds(50));
        var service = new WikidataLookupService(
            wikidataClient, _playerStore, _playerRepository, _playerAttributeRepository, _playerAliasRepository, _playerDataRepository);

        IReadOnlyList<Player>? result = null;
        Assert.DoesNotThrowAsync(async () => result = await service.LookupAndPersistTrophyClubAsync(BallonDor, RealMadrid, WikidataLookupOrigin.Sync));

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    // ADR-0042/S-079 scope decision: unlike LookupAndPersistClubClubAsync/
    // LookupAndPersistTrophyCountryAsync (where match.CareerStints can never
    // be non-empty — their query shapes structurally never bind the shared
    // ?clubStatement variable the qualifier OPTIONALs key off), this query
    // DOES share that variable name (BuildTrophyClubIntersectionQuery), so
    // match.CareerStints CAN be genuinely non-empty here. Uses
    // SingleHenryMatchWithCareerStintJson (real P580/P582/P1350 bindings) —
    // not SingleHenryMatchJson — specifically so this proves the qualifier
    // data was available and still deliberately not written, rather than
    // passing vacuously because there was nothing to persist either way.
    [Test]
    public async Task REQ108_LookupAndPersistTrophyClubAsync_DoesNotPersistPlayerCareerStint_EvenWhenCareerStintQualifiersAreBound()
    {
        var service = BuildService(SingleHenryMatchWithCareerStintJson);

        await service.LookupAndPersistTrophyClubAsync(BallonDor, RealMadrid, WikidataLookupOrigin.Sync);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(await _dbContext.PlayerCareerStints.CountAsync(s => s.PlayerId == player.Id), Is.EqualTo(0));
    }

    // ---- ADR-0061: team-competition trophy dispatch -------------------------
    // LookupAndPersistTrophyCountryAsync/LookupAndPersistTrophyClubAsync now
    // branch on TrophyDefinition.IsTeamTrophy (and, for Country, ALSO on
    // CountryDefinition.UsesCountryForSportProperty) — these tests assert the
    // actual SPARQL query sent for every combination, the same
    // BuildServiceWithHandler technique REQ114_LookupAndPersistAsync_* uses
    // above for the Country x Club P27-vs-P1532 split.

    [Test]
    public async Task REQ108_LookupAndPersistTrophyCountryAsync_TeamTrophyOrdinaryCountry_SentQuery_UsesTeamCompetitionShapeWithP27()
    {
        var (service, handler) = BuildServiceWithHandler(NoMatchJson);

        await service.LookupAndPersistTrophyCountryAsync(WorldCup, France, WikidataLookupOrigin.Sync);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P27 wd:Q142"));
        Assert.That(sentQuery, Does.Contain("wdt:P1344 ?edition"));
        Assert.That(sentQuery, Does.Not.Contain("P166"), "a team trophy must never dispatch through the individual-award P166 query");
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyCountryAsync_TeamTrophyFlaggedCountry_SentQuery_UsesTeamCompetitionShapeWithP1532()
    {
        var (service, handler) = BuildServiceWithHandler(NoMatchJson);

        await service.LookupAndPersistTrophyCountryAsync(WorldCup, England, WikidataLookupOrigin.Sync);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        // Both the player-side and winner-side joins use P1532 for a
        // flagged country, so a bare count is the meaningful assertion here
        // rather than a single Contains.
        Assert.That(sentQuery.Split("wdt:P1532").Length - 1, Is.EqualTo(2),
            "a flagged country's team-trophy query must use P1532 on both the player side and the winner side");
        Assert.That(sentQuery, Does.Contain("wdt:P1344 ?edition"));
        Assert.That(sentQuery, Does.Not.Contain("P166"));
        Assert.That(sentQuery, Does.Not.Contain("P27"));
    }

    [Test]
    public async Task REQ114_LookupAndPersistTrophyCountryAsync_IndividualAwardFlaggedCountry_SentQuery_UsesP166AndP1532NotP27()
    {
        // The pre-existing S-031 P166 individual-award path must ALSO honor
        // UsesCountryForSportProperty, per ADR-0035's follow-up note
        // (resolved by ADR-0061) — this is the regression this fix closes.
        var (service, handler) = BuildServiceWithHandler(NoMatchJson);

        await service.LookupAndPersistTrophyCountryAsync(BallonDor, England, WikidataLookupOrigin.Sync);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P166 wd:Q166177"));
        Assert.That(sentQuery, Does.Contain("wdt:P1532 wd:Q21"));
        Assert.That(sentQuery, Does.Not.Contain("P27"),
            "a flagged country must never silently fall back to P27 for the individual-award path either");
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyCountryAsync_IndividualAwardOrdinaryCountry_SentQuery_StillUsesP166AndP27()
    {
        // Regression: the existing S-031 path for an ordinary (non-flagged)
        // country must stay completely unaffected by this feature.
        var (service, handler) = BuildServiceWithHandler(NoMatchJson);

        await service.LookupAndPersistTrophyCountryAsync(BallonDor, France, WikidataLookupOrigin.Sync);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P166 wd:Q166177"));
        Assert.That(sentQuery, Does.Contain("wdt:P27 wd:Q142"));
        Assert.That(sentQuery, Does.Not.Contain("P1532"));
        Assert.That(sentQuery, Does.Not.Contain("P1344"));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyCountryAsync_TeamTrophy_HitPersistsPlayersUnderTrophyAndNationalityAttributeTypes()
    {
        var service = BuildService(SingleHenryMatchJson);

        var result = await service.LookupAndPersistTrophyCountryAsync(WorldCup, France, WikidataLookupOrigin.Sync);

        Assert.That(result, Has.Count.EqualTo(1));
        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "trophy" && a.AttributeValue == "FIFA World Cup"));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "nationality" && a.AttributeValue == "France"));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyClubAsync_TeamTrophy_SentQuery_UsesTeamCompetitionShapeWithP54()
    {
        var (service, handler) = BuildServiceWithHandler(NoMatchJson);

        await service.LookupAndPersistTrophyClubAsync(WorldCup, RealMadrid, WikidataLookupOrigin.Sync);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("?player p:P54 ?clubStatement."));
        Assert.That(sentQuery, Does.Contain("wdt:P1344 ?edition"));
        Assert.That(sentQuery, Does.Contain("wd:Q8682")); // Real Madrid's QID, matched directly as the edition winner
        Assert.That(sentQuery, Does.Not.Contain("P166"), "a team trophy must never dispatch through the individual-award P166 query");
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyClubAsync_IndividualAward_SentQuery_StillUsesP166AndP54()
    {
        // Regression: the existing S-031 Trophy x Club path must stay
        // completely unaffected.
        var (service, handler) = BuildServiceWithHandler(NoMatchJson);

        await service.LookupAndPersistTrophyClubAsync(BallonDor, RealMadrid, WikidataLookupOrigin.Sync);

        var sentQuery = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.That(sentQuery, Does.Contain("wdt:P166 wd:Q166177"));
        Assert.That(sentQuery, Does.Contain("?player p:P54 ?clubStatement."));
        Assert.That(sentQuery, Does.Not.Contain("P1344"));
    }

    [Test]
    public async Task REQ108_LookupAndPersistTrophyClubAsync_TeamTrophy_HitPersistsPlayersUnderTrophyAndClubAttributeTypes()
    {
        var service = BuildService(SingleHenryMatchJson);

        var result = await service.LookupAndPersistTrophyClubAsync(WorldCup, RealMadrid, WikidataLookupOrigin.Sync);

        Assert.That(result, Has.Count.EqualTo(1));
        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "trophy" && a.AttributeValue == "FIFA World Cup"));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "club" && a.AttributeValue == "Real Madrid"));
    }
}

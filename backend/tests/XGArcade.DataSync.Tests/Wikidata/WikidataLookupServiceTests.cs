using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

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

    private XGArcadeDbContext _dbContext = null!;
    private IPlayerStoreRepository _playerStore = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerStore = new PlayerStoreRepository(_dbContext);
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
        return new WikidataLookupService(wikidataClient, _playerStore);
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
        // ADR-0029: WikidataLookupOrigin.Sync (a routine cache-miss/warming
        // query) persists as "verified" — a guess-time fallback would not,
        // see REQ211_LookupAndPersistAsync_GuessTimeFallback_PersistsAsUnverified below.
        Assert.That(rawData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "verified"));

        var aliases = await _dbContext.PlayerAliases.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(aliases, Has.Count.EqualTo(1));
        Assert.That(aliases[0].Alias, Is.EqualTo("Titi"));
    }

    // ADR-0029/REQ-211: the guess-time fallback (ADR-0018) re-checks a single
    // already-generated cell against a specific player's guess, not the
    // original vetted per-category intersection — kept reviewable rather
    // than auto-verified like a Sync-origin call above.
    [Test]
    public async Task REQ211_LookupAndPersistAsync_GuessTimeFallback_PersistsAsUnverified()
    {
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.GuessTimeFallback);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var rawData = await _dbContext.PlayerData.Where(d => d.PlayerId == player.Id).ToListAsync();
        Assert.That(rawData, Has.Count.EqualTo(2));
        Assert.That(rawData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "unverified"));
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
        var existing = await _playerStore.AddPlayerAsync(
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
        var service = new WikidataLookupService(wikidataClient, _playerStore);

        IReadOnlyList<Player>? result = null;
        Assert.DoesNotThrowAsync(async () => result = await service.LookupAndPersistAsync(France, Arsenal, WikidataLookupOrigin.Sync));

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
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
        var service = new WikidataLookupService(wikidataClient, _playerStore);

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
        var service = new WikidataLookupService(wikidataClient, _playerStore);

        var result = await service.LookupAndPersistAsync(France, unresolvedClub, WikidataLookupOrigin.Sync);

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
        // ADR-0029: see the mirrored note on REQ103_LookupAndPersistAsync_HitPersistsPlayersAndAliases above.
        Assert.That(rawData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "verified"));

        var aliases = await _dbContext.PlayerAliases.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(aliases, Has.Count.EqualTo(1));
        Assert.That(aliases[0].Alias, Is.EqualTo("Titi"));
    }

    // Mirrors REQ211_LookupAndPersistAsync_GuessTimeFallback_PersistsAsUnverified above.
    [Test]
    public async Task REQ211_LookupAndPersistClubClubAsync_GuessTimeFallback_PersistsAsUnverified()
    {
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistClubClubAsync(Barcelona, RealMadrid, WikidataLookupOrigin.GuessTimeFallback);

        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        var rawData = await _dbContext.PlayerData.Where(d => d.PlayerId == player.Id).ToListAsync();
        Assert.That(rawData, Has.Count.EqualTo(2));
        Assert.That(rawData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "unverified"));
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
        var existing = await _playerStore.AddPlayerAsync(
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
        var service = new WikidataLookupService(wikidataClient, _playerStore);

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
        var service = new WikidataLookupService(wikidataClient, _playerStore);

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
        var service = new WikidataLookupService(wikidataClient, _playerStore);

        IReadOnlyList<Player>? result = null;
        Assert.DoesNotThrowAsync(async () => result = await service.LookupAndPersistClubClubAsync(Barcelona, RealMadrid, WikidataLookupOrigin.Sync));

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }
}

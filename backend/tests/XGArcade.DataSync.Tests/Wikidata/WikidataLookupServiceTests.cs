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

    private const string SingleHenryMatchJson = """
        {
          "results": {
            "bindings": [
              { "player": { "type": "uri", "value": "http://www.wikidata.org/entity/Q1519" }, "playerLabel": { "type": "literal", "value": "Thierry Henry" }, "alias": { "type": "literal", "value": "Titi" } }
            ]
          }
        }
        """;

    private const string NoMatchJson = """{ "results": { "bindings": [] } }""";

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

        var result = await service.LookupAndPersistAsync(France, Arsenal);

        Assert.That(result, Has.Count.EqualTo(1));
        var player = await _dbContext.Players.SingleAsync(p => p.WikidataQid == "Q1519");
        Assert.That(player.FullName, Is.EqualTo("Thierry Henry"));

        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(attributes, Has.Count.EqualTo(2));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "nationality" && a.AttributeValue == "France"));
        Assert.That(attributes, Has.Some.Matches<PlayerAttribute>(a => a.AttributeType == "club" && a.AttributeValue == "Arsenal"));

        var rawData = await _dbContext.PlayerData.Where(d => d.PlayerId == player.Id).ToListAsync();
        Assert.That(rawData, Has.All.Matches<PlayerData>(d => d.Source == "wikidata" && d.Confidence == "unverified"));

        var aliases = await _dbContext.PlayerAliases.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(aliases, Has.Count.EqualTo(1));
        Assert.That(aliases[0].Alias, Is.EqualTo("Titi"));
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_ReRunningSameQuery_CreatesZeroDuplicatePlayers()
    {
        var service = BuildService(SingleHenryMatchJson);

        await service.LookupAndPersistAsync(France, Arsenal);
        await service.LookupAndPersistAsync(France, Arsenal);

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
        var result = await service.LookupAndPersistAsync(France, Arsenal);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(existing.Id));
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(1));
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
        Assert.DoesNotThrowAsync(async () => result = await service.LookupAndPersistAsync(France, Arsenal));

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ103_LookupAndPersistAsync_WhenNoMatch_ReturnsEmptyWithoutThrowing()
    {
        var service = BuildService(NoMatchJson);

        var result = await service.LookupAndPersistAsync(France, Arsenal);

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

        var result = await service.LookupAndPersistAsync(unresolvedCountry, Arsenal);

        Assert.That(result, Is.Empty);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(0));
    }
}

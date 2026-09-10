using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// REQ-1501 (xG Higher/Lower, S-232, ADR-0113): same real-InMemory-repository-
// plus-FakeWikidataClient pattern as PlayerInternationalStatsRefreshServiceTests
// (docs/coding-guidelines.md "don't over-mock").
public class PlayerTrophyStatsRefreshServiceTests
{
    private const string BallonDOrQid = "Q166177";
    private const string WorldCupQid = "Q19317";
    private const string ChampionsLeagueQid = "Q18756";

    private XGArcadeDbContext _dbContext = null!;
    private IPlayerRepository _playerRepository = null!;
    private IPlayerAttributeRepository _playerAttributeRepository = null!;
    private IPlayerDataRepository _playerDataRepository = null!;
    private ICategoryValueRepository _categoryValueRepository = null!;
    private FakeWikidataClient _wikidataClient = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerRepository = new PlayerRepository(_dbContext);
        _playerAttributeRepository = new PlayerAttributeRepository(_dbContext);
        _playerDataRepository = new PlayerDataRepository(_dbContext);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _wikidataClient = new FakeWikidataClient();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerTrophyStatsRefreshService BuildService() =>
        new(_wikidataClient, _playerRepository, _playerAttributeRepository, _playerDataRepository,
            _categoryValueRepository, NullLogger<PlayerTrophyStatsRefreshService>.Instance);

    private async Task<Player> SeedPlayerAsync(string wikidataQid) =>
        await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = $"Player {wikidataQid}", WikidataQid = wikidataQid });

    // Mirrors ReferenceDataSeeder.Trophies exactly (1 individual award, 2
    // team competitions) — a test that seeded only one trophy of each kind
    // would not exercise the "multiple trophies won" shape this service's
    // own dedup-by-name logic needs to handle correctly.
    private async Task SeedTrophiesAsync()
    {
        await _categoryValueRepository.AddTrophyAsync(new TrophyDefinition
        {
            Id = Guid.NewGuid(), Name = "Ballon d'Or", WikidataQid = BallonDOrQid, IsTeamTrophy = false,
        });
        await _categoryValueRepository.AddTrophyAsync(new TrophyDefinition
        {
            Id = Guid.NewGuid(), Name = "FIFA World Cup", WikidataQid = WorldCupQid, IsTeamTrophy = true,
        });
        await _categoryValueRepository.AddTrophyAsync(new TrophyDefinition
        {
            Id = Guid.NewGuid(), Name = "UEFA Champions League", WikidataQid = ChampionsLeagueQid, IsTeamTrophy = true,
        });
    }

    [Test]
    public async Task REQ1501_RefreshTrophyStatsAsync_WonIndividualTrophy_PersistsAttributeRow()
    {
        await SeedTrophiesAsync();
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetIndividualTrophies("Q1519", BallonDOrQid);

        await BuildService().RefreshTrophyStatsAsync([player.Id]);

        var attributes = (await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id]))[player.Id];
        Assert.That(attributes.Select(a => (a.AttributeType, a.AttributeValue)),
            Is.EquivalentTo(new[] { ("trophy", "Ballon d'Or") }));
    }

    [Test]
    public async Task REQ1501_RefreshTrophyStatsAsync_WonTeamTrophy_PersistsAttributeRow()
    {
        await SeedTrophiesAsync();
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetTeamTrophies("Q1519", WorldCupQid);

        await BuildService().RefreshTrophyStatsAsync([player.Id]);

        var attributes = (await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id]))[player.Id];
        Assert.That(attributes.Select(a => (a.AttributeType, a.AttributeValue)),
            Is.EquivalentTo(new[] { ("trophy", "FIFA World Cup") }));
    }

    [Test]
    public async Task REQ1501_RefreshTrophyStatsAsync_WonMultipleTrophies_PersistsOneRowPerTrophy()
    {
        await SeedTrophiesAsync();
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetIndividualTrophies("Q1519", BallonDOrQid);
        _wikidataClient.SetTeamTrophies("Q1519", WorldCupQid, ChampionsLeagueQid);

        await BuildService().RefreshTrophyStatsAsync([player.Id]);

        var attributes = (await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id]))[player.Id];
        Assert.That(attributes.Select(a => a.AttributeValue),
            Is.EquivalentTo(new[] { "Ballon d'Or", "FIFA World Cup", "UEFA Champions League" }));
    }

    [Test]
    public async Task REQ1501_RefreshTrophyStatsAsync_WonNothing_WritesCheckedMarker_NotAFabricatedAttribute()
    {
        await SeedTrophiesAsync();
        var player = await SeedPlayerAsync("Q1519"); // No SetIndividualTrophies/SetTeamTrophies call — won nothing.

        await BuildService().RefreshTrophyStatsAsync([player.Id]);

        Assert.That((await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id])).ContainsKey(player.Id), Is.False,
            "no PlayerAttribute row must be fabricated for a player who won none of the seeded trophies");

        var playerDataRows = await _dbContext.PlayerData.Where(pd => pd.PlayerId == player.Id).ToListAsync();
        Assert.That(playerDataRows.Select(pd => pd.Field), Is.EquivalentTo(new[] { PlayerData.TrophyStatsCheckedField }),
            "a successfully-queried player who won nothing must still get the checked marker so the backfill stops re-querying them forever");
        Assert.That(playerDataRows.Single().Value, Is.EqualTo(PlayerData.TrophyStatsCheckedValue));
    }

    [Test]
    public async Task REQ1501_RefreshTrophyStatsAsync_WonTrophy_AlsoWritesCheckedMarker()
    {
        await SeedTrophiesAsync();
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetIndividualTrophies("Q1519", BallonDOrQid);

        await BuildService().RefreshTrophyStatsAsync([player.Id]);

        var playerDataFields = (await _dbContext.PlayerData.Where(pd => pd.PlayerId == player.Id).ToListAsync())
            .Select(pd => pd.Field);
        Assert.That(playerDataFields, Is.EquivalentTo(new[] { "trophy", PlayerData.TrophyStatsCheckedField }),
            "a player whose data resolves gets both the real PlayerData row(s) AND the checked marker");
    }

    [Test]
    public async Task REQ1501_RefreshTrophyStatsAsync_PlayerAlreadyHasThatTrophyRow_FromByproductPath_IsNeverDuplicated()
    {
        await SeedTrophiesAsync();
        var player = await SeedPlayerAsync("Q1519");
        // Simulates WikidataLookupService's existing byproduct path, which
        // predates this marker entirely — a real, already-existing row for
        // exactly the same (player, trophy) pair this refresh would
        // otherwise also resolve.
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
        {
            PlayerId = player.Id, AttributeType = "trophy", AttributeValue = "Ballon d'Or",
        });
        _wikidataClient.SetIndividualTrophies("Q1519", BallonDOrQid);

        await BuildService().RefreshTrophyStatsAsync([player.Id]);

        var attributes = (await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id]))[player.Id];
        Assert.That(attributes.Count(a => a.AttributeType == "trophy" && a.AttributeValue == "Ballon d'Or"), Is.EqualTo(1),
            "must not duplicate a (player, trophy) row the byproduct path already wrote");
    }

    [Test]
    public async Task REQ1501_RefreshTrophyStatsAsync_PlayerAlreadyHasOneTrophyRow_StillAddsANewlyWonDifferentTrophy()
    {
        await SeedTrophiesAsync();
        var player = await SeedPlayerAsync("Q1519");
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
        {
            PlayerId = player.Id, AttributeType = "trophy", AttributeValue = "Ballon d'Or",
        });
        _wikidataClient.SetIndividualTrophies("Q1519", BallonDOrQid); // Same trophy as the existing row.
        _wikidataClient.SetTeamTrophies("Q1519", WorldCupQid); // A newly-resolved, different trophy.

        await BuildService().RefreshTrophyStatsAsync([player.Id]);

        var attributes = (await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id]))[player.Id];
        Assert.That(attributes.Select(a => a.AttributeValue), Is.EquivalentTo(new[] { "Ballon d'Or", "FIFA World Cup" }),
            "a player can hold more than one trophy — an existing row for one must not block a genuinely different, newly-resolved one");
    }

    [Test]
    public async Task REQ1501_RefreshTrophyStatsAsync_PlayerWithNoWikidataQid_IsNeverQueried()
    {
        await SeedTrophiesAsync();
        var player = await _playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = "No QID Player" });

        await BuildService().RefreshTrophyStatsAsync([player.Id]);

        Assert.That(_wikidataClient.QueriedIndividualTrophyPlayerBatches, Is.Empty);
        Assert.That(_wikidataClient.QueriedTeamTrophyPlayerBatches, Is.Empty);
    }

    [Test]
    public async Task REQ1501_RefreshTrophyStatsAsync_EmptyPlayerIdList_DoesNothing()
    {
        await SeedTrophiesAsync();

        await BuildService().RefreshTrophyStatsAsync([]);

        Assert.That(_wikidataClient.QueriedIndividualTrophyPlayerBatches, Is.Empty);
        Assert.That(_wikidataClient.QueriedTeamTrophyPlayerBatches, Is.Empty);
    }

    [Test]
    public async Task REQ1501_RefreshTrophyStatsAsync_IndividualTrophyQueryFails_DoesNotThrow_ByDefault_NoMarkerWritten()
    {
        await SeedTrophiesAsync();
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.FailNextIndividualTrophyBatches(1);

        Assert.DoesNotThrowAsync(async () => await BuildService().RefreshTrophyStatsAsync([player.Id]));

        var playerDataRows = await _dbContext.PlayerData.Where(pd => pd.PlayerId == player.Id).ToListAsync();
        Assert.That(playerDataRows, Is.Empty,
            "a batch whose Wikidata call itself failed must not mark any of its players as checked — they need to stay retryable");
    }

    [Test]
    public async Task REQ1501_RefreshTrophyStatsAsync_TeamTrophyQueryFails_DoesNotThrow_ByDefault_NoMarkerWritten()
    {
        await SeedTrophiesAsync();
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.FailNextTeamTrophyBatches(1);

        Assert.DoesNotThrowAsync(async () => await BuildService().RefreshTrophyStatsAsync([player.Id]));

        var playerDataRows = await _dbContext.PlayerData.Where(pd => pd.PlayerId == player.Id).ToListAsync();
        Assert.That(playerDataRows, Is.Empty,
            "either query failing must fail the whole batch attempt — a player is only ever 'checked' once BOTH sweeps have actually run");
    }

    [Test]
    public async Task REQ1501_RefreshTrophyStatsAsync_ThrowOnFailureTrue_WikidataQueryFails_PropagatesWikidataQueryException()
    {
        await SeedTrophiesAsync();
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.FailNextIndividualTrophyBatches(1);

        Assert.ThrowsAsync<WikidataQueryException>(
            async () => await BuildService().RefreshTrophyStatsAsync([player.Id], throwOnFailure: true),
            "throwOnFailure: true must let a Wikidata technical failure propagate instead of being logged and swallowed — used by PlayerTrophyStatsBackfillService");
    }

    [Test]
    public async Task REQ1501_RefreshTrophyStatsAsync_PlayerAlreadyHasCapsRowFromADifferentAttributeType_IsUnaffected()
    {
        // Sanity check that this service only ever reads/writes "trophy"
        // rows — an existing "international-caps" row for the same player
        // must never be mistaken for an already-processed trophy.
        await SeedTrophiesAsync();
        var player = await SeedPlayerAsync("Q1519");
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
        {
            PlayerId = player.Id, AttributeType = "international-caps", AttributeValue = "50",
        });
        _wikidataClient.SetIndividualTrophies("Q1519", BallonDOrQid);

        await BuildService().RefreshTrophyStatsAsync([player.Id]);

        var attributes = (await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id]))[player.Id];
        Assert.That(attributes.Select(a => (a.AttributeType, a.AttributeValue)),
            Is.EquivalentTo(new[] { ("international-caps", "50"), ("trophy", "Ballon d'Or") }));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// REQ-1501 (xG Higher/Lower, S-232, ADR-0113): `dotnet run --
// backfill-player-trophy-stats`'s own driving loop — see
// PlayerTrophyStatsBackfillService's own doc comment for the full "why a
// cursor-driven backfill, not a country/club discovery sweep" design choice.
// Uses the REAL PlayerTrophyStatsRefreshService (against InMemory
// repositories) rather than a fake of it, the same "don't over-mock, prefer
// a real collaborator when it's cheap" posture
// PlayerInternationalStatsBackfillServiceTests already establishes — the
// only genuinely faked dependency is FakeWikidataClient itself.
public class PlayerTrophyStatsBackfillServiceTests
{
    private const string BallonDOrQid = "Q166177";

    private XGArcadeDbContext _dbContext = null!;
    private IPlayerRepository _playerRepository = null!;
    private IPlayerAttributeRepository _playerAttributeRepository = null!;
    private IPlayerBackfillRepository _backfillRepository = null!;
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
        _backfillRepository = new PlayerBackfillRepository(_dbContext);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _wikidataClient = new FakeWikidataClient();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerTrophyStatsBackfillService BuildService()
    {
        var refreshService = new PlayerTrophyStatsRefreshService(
            _wikidataClient, _playerRepository, _playerAttributeRepository,
            new PlayerDataRepository(_dbContext), _categoryValueRepository,
            NullLogger<PlayerTrophyStatsRefreshService>.Instance);

        return new PlayerTrophyStatsBackfillService(
            _backfillRepository, refreshService, NullLogger<PlayerTrophyStatsBackfillService>.Instance);
    }

    private async Task<Player> SeedPlayerAsync(string wikidataQid) =>
        await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = $"Player {wikidataQid}", WikidataQid = wikidataQid });

    private async Task SeedTrophiesAsync() =>
        await _categoryValueRepository.AddTrophyAsync(new TrophyDefinition
        {
            Id = Guid.NewGuid(), Name = "Ballon d'Or", WikidataQid = BallonDOrQid, IsTeamTrophy = false,
        });

    [Test]
    public async Task REQ1501_BackfillAsync_MissingPlayer_GetsTrophyStatsPersisted()
    {
        await SeedTrophiesAsync();
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetIndividualTrophies("Q1519", BallonDOrQid);

        var result = await BuildService().BackfillAsync();

        var attributes = (await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id]))[player.Id];
        Assert.That(attributes.Single().AttributeValue, Is.EqualTo("Ballon d'Or"));
        Assert.That(result.PlayersAttempted, Is.EqualTo(1));
        Assert.That(result.BatchesProcessed, Is.EqualTo(1));
        Assert.That(result.BatchesFailed, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ1501_BackfillAsync_PlayerAlreadyHasTrophyRow_IsNeverSelectedAgain()
    {
        await SeedTrophiesAsync();
        var alreadyProcessed = await SeedPlayerAsync("Q1519");
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
        {
            PlayerId = alreadyProcessed.Id, AttributeType = "trophy", AttributeValue = "Ballon d'Or",
        });

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(0), "an already-fully-backfilled pool must produce zero batches");
        Assert.That(_wikidataClient.QueriedIndividualTrophyPlayerBatches, Is.Empty);
    }

    [Test]
    public async Task REQ1501_BackfillAsync_MultipleBatches_ProcessesEveryPlayer()
    {
        await SeedTrophiesAsync();
        var players = new List<Player>();
        for (var i = 0; i < 5; i++)
        {
            var player = await SeedPlayerAsync($"Q{i}");
            _wikidataClient.SetIndividualTrophies($"Q{i}", BallonDOrQid);
            players.Add(player);
        }

        var result = await BuildService().BackfillAsync();

        Assert.That(result.PlayersAttempted, Is.EqualTo(5));
        foreach (var player in players)
        {
            var attributes = (await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id]))[player.Id];
            Assert.That(attributes.Any(a => a.AttributeType == "trophy"), Is.True);
        }
    }

    [Test]
    public async Task REQ1501_BackfillAsync_BatchWikidataFailure_IsCountedAndSkipped_RunContinues()
    {
        await SeedTrophiesAsync();
        var failingPlayer = await SeedPlayerAsync("Q1519");
        _wikidataClient.FailNextIndividualTrophyBatches(1);

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesFailed, Is.EqualTo(1));
        Assert.That((await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([failingPlayer.Id])).ContainsKey(failingPlayer.Id), Is.False,
            "a failed batch must leave the player exactly as before — idempotent, safe to re-run");
    }

    [Test]
    public async Task REQ1501_BackfillAsync_NoMissingPlayers_ReturnsZeroBatches()
    {
        await SeedTrophiesAsync();

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(0));
        Assert.That(result.PlayersAttempted, Is.EqualTo(0));
        Assert.That(result.BatchesFailed, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ1501_BackfillAsync_PlayerWhoWonNothing_TerminatesRun_NotTreatedAsFailure()
    {
        await SeedTrophiesAsync();
        // No SetIndividualTrophies/SetTeamTrophies call — won nothing. The
        // loop must still terminate (attemptedPlayerIds excludes this player
        // on the next GetPlayersMissingTrophyStatsAsync call within the same
        // run) rather than looping forever, and this must not count as a
        // batch failure.
        await SeedPlayerAsync("Q1519");

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesFailed, Is.EqualTo(0));
        Assert.That(result.BatchesProcessed, Is.EqualTo(1));
    }

    // ADR-0113's whole reason for existing — proven here at the
    // backfill-cursor level, on top of PlayerTrophyStatsRefreshServiceTests'
    // own unit-level coverage of the marker write itself, the same shape
    // S-231/PR #367's own follow-up proved for caps/goals (this ADR exists
    // specifically so that fix didn't need rediscovering a second time).
    [Test]
    public async Task REQ1501_BackfillAsync_SecondRun_DoesNotReattemptPlayersWhoWonNothing()
    {
        await SeedTrophiesAsync();
        // Neither player configured with SetIndividualTrophies/SetTeamTrophies
        // — both genuinely won nothing.
        await SeedPlayerAsync("Q1519");
        await SeedPlayerAsync("Q42233");

        var firstRun = await BuildService().BackfillAsync();
        Assert.That(firstRun.PlayersAttempted, Is.EqualTo(2), "sanity check: the first run attempts both never-checked players");

        var secondRun = await BuildService().BackfillAsync();

        Assert.That(secondRun.PlayersAttempted, Is.EqualTo(0),
            "a player whose Wikidata batch succeeded but won nothing must be excluded from every future run, not re-attempted indefinitely");
        Assert.That(secondRun.BatchesProcessed, Is.EqualTo(0));
    }
}

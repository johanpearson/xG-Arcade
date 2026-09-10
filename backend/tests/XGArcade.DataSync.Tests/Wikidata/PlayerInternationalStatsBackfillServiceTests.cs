using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// REQ-1501/REQ-1506 (xG Higher/Lower, S-231, ADR-0112): `dotnet run --
// backfill-player-international-stats`'s own driving loop — see
// PlayerInternationalStatsBackfillService's own doc comment for the full
// "why a cursor-driven backfill, not a country/club discovery sweep"
// design choice. Uses the REAL PlayerInternationalStatsRefreshService
// (against InMemory repositories) rather than a fake of it, the same
// "don't over-mock, prefer a real collaborator when it's cheap" posture
// PlayerPositionBirthYearBackfillServiceTests already establishes for its
// own inline fetch+write — the only genuinely faked dependency is
// FakeWikidataClient itself.
public class PlayerInternationalStatsBackfillServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerRepository _playerRepository = null!;
    private IPlayerAttributeRepository _playerAttributeRepository = null!;
    private IPlayerBackfillRepository _backfillRepository = null!;
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
        _wikidataClient = new FakeWikidataClient();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerInternationalStatsBackfillService BuildService()
    {
        var refreshService = new PlayerInternationalStatsRefreshService(
            _wikidataClient, _playerRepository, _playerAttributeRepository,
            new PlayerDataRepository(_dbContext), NullLogger<PlayerInternationalStatsRefreshService>.Instance);

        return new PlayerInternationalStatsBackfillService(
            _backfillRepository, refreshService, NullLogger<PlayerInternationalStatsBackfillService>.Instance);
    }

    private async Task<Player> SeedPlayerAsync(string wikidataQid) =>
        await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = $"Player {wikidataQid}", WikidataQid = wikidataQid });

    [Test]
    public async Task BackfillAsync_MissingPlayer_GetsInternationalStatsPersisted()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetInternationalStats("Q1519", caps: 123, goals: 51);

        var result = await BuildService().BackfillAsync();

        var attributes = (await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id]))[player.Id];
        Assert.That(attributes.First(a => a.AttributeType == "international-caps").AttributeValue, Is.EqualTo("123"));
        Assert.That(result.PlayersAttempted, Is.EqualTo(1));
        Assert.That(result.BatchesProcessed, Is.EqualTo(1));
        Assert.That(result.BatchesFailed, Is.EqualTo(0));
    }

    [Test]
    public async Task BackfillAsync_PlayerAlreadyHasCapsRow_IsNeverSelectedAgain()
    {
        var alreadyProcessed = await SeedPlayerAsync("Q1519");
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
        {
            PlayerId = alreadyProcessed.Id, AttributeType = "international-caps", AttributeValue = "42",
        });

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(0), "an already-fully-backfilled pool must produce zero batches");
        Assert.That(_wikidataClient.QueriedInternationalStatsBatches, Is.Empty);
    }

    [Test]
    public async Task BackfillAsync_MultipleBatches_ProcessesEveryPlayer()
    {
        var players = new List<Player>();
        for (var i = 0; i < 5; i++)
        {
            var player = await SeedPlayerAsync($"Q{i}");
            _wikidataClient.SetInternationalStats($"Q{i}", caps: 10 + i);
            players.Add(player);
        }

        var result = await BuildService().BackfillAsync();

        Assert.That(result.PlayersAttempted, Is.EqualTo(5));
        foreach (var player in players)
        {
            var attributes = (await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id]))[player.Id];
            Assert.That(attributes.Any(a => a.AttributeType == "international-caps"), Is.True);
        }
    }

    [Test]
    public async Task BackfillAsync_BatchWikidataFailure_IsCountedAndSkipped_RunContinues()
    {
        var failingPlayer = await SeedPlayerAsync("Q1519");
        _wikidataClient.FailNextInternationalStatsBatches(1);

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesFailed, Is.EqualTo(1));
        Assert.That((await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([failingPlayer.Id])).ContainsKey(failingPlayer.Id), Is.False,
            "a failed batch must leave the player exactly as before — idempotent, safe to re-run");
    }

    [Test]
    public async Task BackfillAsync_NoMissingPlayers_ReturnsZeroBatches()
    {
        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(0));
        Assert.That(result.PlayersAttempted, Is.EqualTo(0));
        Assert.That(result.BatchesFailed, Is.EqualTo(0));
    }

    [Test]
    public async Task BackfillAsync_PlayerWithNoQualifyingStatement_TerminatesRun_NotTreatedAsFailure()
    {
        // No SetInternationalStats call — genuinely no qualifying P54
        // statement. The loop must still terminate (attemptedPlayerIds
        // excludes this player on the next GetPlayersMissingInternationalStatsAsync
        // call within the same run) rather than looping forever, and this
        // must not count as a batch failure.
        await SeedPlayerAsync("Q1519");

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesFailed, Is.EqualTo(0));
        Assert.That(result.BatchesProcessed, Is.EqualTo(1));
    }
}

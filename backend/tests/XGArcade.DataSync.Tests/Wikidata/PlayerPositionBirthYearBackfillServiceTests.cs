using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// REQ-1207 backfill (bug-bundle fix, 2026-08-02): same real-InMemory-
// repository-plus-FakeWikidataClient pattern as
// PlayerPhotoBackfillServiceTests (see that file's own doc comment for why:
// docs/coding-guidelines.md's "don't over-mock") — this test file mirrors
// that one's coverage exactly, adapted for Position/BirthYear's two-field
// "either is missing" shape instead of PhotoUrl's single field.
public class PlayerPositionBirthYearBackfillServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerStoreRepository _playerStoreRepository = null!;
    private FakeWikidataClient _wikidataClient = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerStoreRepository = new PlayerStoreRepository(_dbContext);
        _wikidataClient = new FakeWikidataClient();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerPositionBirthYearBackfillService BuildService() =>
        new(_playerStoreRepository, _wikidataClient, NullLogger<PlayerPositionBirthYearBackfillService>.Instance);

    private async Task<Player> SeedPlayerAsync(string wikidataQid, string? position = null, int? birthYear = null)
    {
        var player = new Player
        {
            Id = Guid.NewGuid(),
            FullName = $"Player {wikidataQid}",
            WikidataQid = wikidataQid,
            Position = position,
            BirthYear = birthYear,
        };
        await _playerStoreRepository.AddPlayerAsync(player);
        return player;
    }

    [Test]
    public async Task REQ1207_BackfillAsync_MissingBothFields_GetsBackfilledFromWikidata()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetPositionBirthYear("Q1519", "forward", 1977);

        var result = await BuildService().BackfillAsync();

        Assert.That(result.PlayersBackfilled, Is.EqualTo(1));
        var reloaded = await _playerStoreRepository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.EqualTo("forward"));
        Assert.That(reloaded.BirthYear, Is.EqualTo(1977));
    }

    [Test]
    public async Task REQ1207_BackfillAsync_MissingOnlyPosition_BackfillsPositionWithoutTouchingExistingBirthYear()
    {
        var player = await SeedPlayerAsync("Q1519", birthYear: 1977);
        _wikidataClient.SetPositionBirthYear("Q1519", "forward", 1977);

        var result = await BuildService().BackfillAsync();

        Assert.That(result.PlayersBackfilled, Is.EqualTo(1));
        var reloaded = await _playerStoreRepository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.EqualTo("forward"));
        Assert.That(reloaded.BirthYear, Is.EqualTo(1977));
    }

    [Test]
    public async Task REQ1207_BackfillAsync_MissingOnlyBirthYear_BackfillsBirthYearWithoutTouchingExistingPosition()
    {
        var player = await SeedPlayerAsync("Q1519", position: "midfielder");
        _wikidataClient.SetPositionBirthYear("Q1519", "midfielder", 1987);

        var result = await BuildService().BackfillAsync();

        Assert.That(result.PlayersBackfilled, Is.EqualTo(1));
        var reloaded = await _playerStoreRepository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.EqualTo("midfielder"), "an already-set field must never be overwritten, even with an identical value");
        Assert.That(reloaded.BirthYear, Is.EqualTo(1987));
    }

    [Test]
    public async Task REQ1207_BackfillAsync_PlayerAlreadyHasBothFields_IsNeverQueried()
    {
        await SeedPlayerAsync("Q1519", position: "forward", birthYear: 1977);

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(0));
        Assert.That(_wikidataClient.QueriedPositionBirthYearBatches, Is.Empty);
    }

    [Test]
    public async Task REQ1207_BackfillAsync_PlayerWithNoWikidataQid_IsNeverQueried()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "No QID Player" };
        await _playerStoreRepository.AddPlayerAsync(player);

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(0));
        Assert.That(_wikidataClient.QueriedPositionBirthYearBatches, Is.Empty);
        var reloaded = await _playerStoreRepository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.Null);
        Assert.That(reloaded.BirthYear, Is.Null);
    }

    [Test]
    public async Task REQ1207_BackfillAsync_PlayerWithNoP413OrP569Statement_StaysNullAndIsNotTreatedAsAFailure()
    {
        await SeedPlayerAsync("Q1519"); // No SetPositionBirthYear call — genuinely no data.

        var result = await BuildService().BackfillAsync();

        Assert.That(result.PlayersBackfilled, Is.EqualTo(0));
        Assert.That(result.BatchesFailed, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ1207_BackfillAsync_MultipleMissingFieldPlayers_AllBackfilledInOneBatch()
    {
        var players = new List<Player>();
        for (var i = 0; i < 10; i++)
        {
            var player = await SeedPlayerAsync($"Q{i}");
            players.Add(player);
            _wikidataClient.SetPositionBirthYear($"Q{i}", "defender", 1990 + i);
        }

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(1));
        Assert.That(result.PlayersBackfilled, Is.EqualTo(10));
        foreach (var player in players)
        {
            var reloaded = await _playerStoreRepository.GetPlayerByIdAsync(player.Id);
            Assert.That(reloaded!.Position, Is.EqualTo("defender"));
            Assert.That(reloaded.BirthYear, Is.EqualTo(1990 + int.Parse(player.WikidataQid![1..])));
        }
    }

    [Test]
    public async Task REQ1207_BackfillAsync_MoreMissingFieldPlayersThanBatchSize_QueriesInMultipleBatchesOfAtMostBatchSize()
    {
        const int playerCount = PlayerPositionBirthYearBackfillService.BatchSize + 50;
        for (var i = 0; i < playerCount; i++)
        {
            var qid = $"Q{i}";
            await SeedPlayerAsync(qid);
            _wikidataClient.SetPositionBirthYear(qid, "goalkeeper", 1995);
        }

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(2));
        Assert.That(result.PlayersBackfilled, Is.EqualTo(playerCount));
        Assert.That(_wikidataClient.QueriedPositionBirthYearBatches, Has.Count.EqualTo(2));
        Assert.That(_wikidataClient.QueriedPositionBirthYearBatches[0], Has.Count.EqualTo(PlayerPositionBirthYearBackfillService.BatchSize),
            "each batch must stay within the bounded-query batch size, never fetch everything in one VALUES clause");
        Assert.That(_wikidataClient.QueriedPositionBirthYearBatches[1], Has.Count.EqualTo(50));
    }

    [Test]
    public async Task REQ1207_BackfillAsync_ReRunAfterSuccessfulBackfill_TouchesNothing()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetPositionBirthYear("Q1519", "forward", 1977);
        await BuildService().BackfillAsync();

        // A fresh service instance, same shape as a second CLI invocation —
        // no in-memory state carries over between runs.
        var secondResult = await BuildService().BackfillAsync();

        Assert.That(secondResult.BatchesProcessed, Is.EqualTo(0));
        Assert.That(secondResult.PlayersBackfilled, Is.EqualTo(0));
        var reloaded = await _playerStoreRepository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.EqualTo("forward"));
        Assert.That(reloaded.BirthYear, Is.EqualTo(1977));
    }

    [Test]
    public async Task REQ1207_BackfillAsync_BatchFails_LogsAndContinuesToNextBatch_WithoutFailingTheRun()
    {
        // Two full batches: the first fails outright, the second succeeds —
        // asserts the documented log-and-continue judgment call (mirroring
        // PlayerPhotoBackfillService, not PlayerNameIndexImporter's
        // retry-then-fail-loud).
        const int playerCount = PlayerPositionBirthYearBackfillService.BatchSize * 2;
        for (var i = 0; i < playerCount; i++)
        {
            var qid = $"Q{i}";
            await SeedPlayerAsync(qid);
            _wikidataClient.SetPositionBirthYear(qid, "forward", 1990);
        }
        _wikidataClient.FailNextPositionBirthYearBatches(1);

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(2));
        Assert.That(result.BatchesFailed, Is.EqualTo(1));
        Assert.That(result.PlayersBackfilled, Is.EqualTo(PlayerPositionBirthYearBackfillService.BatchSize),
            "the failed batch's players stay un-backfilled this run, but the run itself must still complete and process the remaining batch");
    }

    [Test]
    public async Task REQ1207_BackfillAsync_BatchFails_FailedBatchesPlayersStillShowAsMissingData_ForANextRun()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetPositionBirthYear("Q1519", "forward", 1977);
        _wikidataClient.FailNextPositionBirthYearBatches(1);

        await BuildService().BackfillAsync();

        var reloaded = await _playerStoreRepository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.Null,
            "a failed batch must leave its players' fields untouched — a later re-run's GetPlayersMissingPositionOrBirthYearAsync will surface them again automatically");
        Assert.That(reloaded.BirthYear, Is.Null);
    }

    [Test]
    public async Task REQ1207_BackfillAsync_NoPlayersAtAll_ReturnsZeroedResultWithoutQueryingWikidata()
    {
        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(0));
        Assert.That(result.PlayersBackfilled, Is.EqualTo(0));
        Assert.That(result.BatchesFailed, Is.EqualTo(0));
        Assert.That(_wikidataClient.QueriedPositionBirthYearBatches, Is.Empty);
    }

    // Regression coverage mirroring PlayerPhotoBackfillServiceTests' own
    // malformed-QID regression test — same underlying WikidataQid.IsValid
    // pre-filter, exercised through this service instead.
    [Test]
    public async Task REQ1207_BackfillAsync_BatchContainsMalformedWikidataQid_SkipsThatPlayerButBackfillsTheRestWithoutThrowing()
    {
        var goodPlayer = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetPositionBirthYear("Q1519", "forward", 1977);
        var badPlayer = await SeedPlayerAsync("Qtest-99195db1-cbff-4491-8007-8d497b926a65");

        PlayerPositionBirthYearBackfillResult result = null!;
        Assert.DoesNotThrowAsync(async () => result = await BuildService().BackfillAsync());

        Assert.That(result.PlayersBackfilled, Is.EqualTo(1));
        Assert.That(result.BatchesFailed, Is.EqualTo(0),
            "a malformed QID on one player is a per-player skip, not a whole-batch failure");
        var reloadedGood = await _playerStoreRepository.GetPlayerByIdAsync(goodPlayer.Id);
        Assert.That(reloadedGood!.Position, Is.EqualTo("forward"));
        var reloadedBad = await _playerStoreRepository.GetPlayerByIdAsync(badPlayer.Id);
        Assert.That(reloadedBad!.Position, Is.Null);
        Assert.That(_wikidataClient.QueriedPositionBirthYearBatches, Has.Count.EqualTo(1));
        Assert.That(_wikidataClient.QueriedPositionBirthYearBatches[0], Does.Not.Contain(badPlayer.WikidataQid),
            "the malformed QID must be filtered out before the batch is sent to Wikidata, not just after");
    }

    [Test]
    public async Task REQ1207_BackfillAsync_EveryPlayerInBatchHasMalformedWikidataQid_CompletesWithoutThrowing()
    {
        var badPlayer = await SeedPlayerAsync("not-a-qid");

        PlayerPositionBirthYearBackfillResult result = null!;
        Assert.DoesNotThrowAsync(async () => result = await BuildService().BackfillAsync());

        Assert.That(result.BatchesProcessed, Is.EqualTo(1));
        Assert.That(result.PlayersBackfilled, Is.EqualTo(0));
        Assert.That(result.BatchesFailed, Is.EqualTo(0));
        var reloaded = await _playerStoreRepository.GetPlayerByIdAsync(badPlayer.Id);
        Assert.That(reloaded!.Position, Is.Null);
    }
}

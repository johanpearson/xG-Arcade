using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// REQ-214 backfill (S-045, docs/backlog.md): same real-InMemory-repository-
// plus-FakeWikidataClient pattern as PlayerCacheWarmingServiceTests (see
// that file's own doc comment for why: docs/coding-guidelines.md's
// "don't over-mock").
public class PlayerPhotoBackfillServiceTests
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

    private PlayerPhotoBackfillService BuildService() =>
        new(_playerStoreRepository, _wikidataClient, NullLogger<PlayerPhotoBackfillService>.Instance);

    private async Task<Player> SeedPlayerAsync(string wikidataQid, string? photoUrl = null)
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = $"Player {wikidataQid}", WikidataQid = wikidataQid, PhotoUrl = photoUrl };
        await _playerStoreRepository.AddPlayerAsync(player);
        return player;
    }

    [Test]
    public async Task REQ214_BackfillAsync_MissingPhotoPlayer_GetsBackfilledFromWikidata()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetPhoto("Q1519", "https://example.com/henry.jpg");

        var result = await BuildService().BackfillAsync();

        Assert.That(result.PlayersBackfilled, Is.EqualTo(1));
        var reloaded = await _playerStoreRepository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.PhotoUrl, Is.EqualTo("https://example.com/henry.jpg"));
    }

    [Test]
    public async Task REQ214_BackfillAsync_PlayerAlreadyHasPhoto_IsNeverQueried()
    {
        await SeedPlayerAsync("Q1519", photoUrl: "https://example.com/existing.jpg");

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(0));
        Assert.That(_wikidataClient.QueriedPhotoBatches, Is.Empty);
    }

    [Test]
    public async Task REQ214_BackfillAsync_PlayerWithNoWikidataQid_IsNeverQueried()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "No QID Player" };
        await _playerStoreRepository.AddPlayerAsync(player);

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(0));
        Assert.That(_wikidataClient.QueriedPhotoBatches, Is.Empty);
        Assert.That((await _playerStoreRepository.GetPlayerByIdAsync(player.Id))!.PhotoUrl, Is.Null);
    }

    [Test]
    public async Task REQ214_BackfillAsync_PlayerWithNoP18Statement_StaysNullAndIsNotTreatedAsAFailure()
    {
        await SeedPlayerAsync("Q1519"); // No SetPhoto call — genuinely no P18.

        var result = await BuildService().BackfillAsync();

        Assert.That(result.PlayersBackfilled, Is.EqualTo(0));
        Assert.That(result.BatchesFailed, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ214_BackfillAsync_MultipleMissingPhotoPlayers_AllBackfilledInOneBatch()
    {
        var players = new List<Player>();
        for (var i = 0; i < 10; i++)
        {
            var player = await SeedPlayerAsync($"Q{i}");
            players.Add(player);
            _wikidataClient.SetPhoto($"Q{i}", $"https://example.com/{i}.jpg");
        }

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(1));
        Assert.That(result.PlayersBackfilled, Is.EqualTo(10));
        foreach (var player in players)
        {
            var reloaded = await _playerStoreRepository.GetPlayerByIdAsync(player.Id);
            Assert.That(reloaded!.PhotoUrl, Is.EqualTo($"https://example.com/{player.WikidataQid![1..]}.jpg"));
        }
    }

    [Test]
    public async Task REQ214_BackfillAsync_MoreMissingPhotoPlayersThanBatchSize_QueriesInMultipleBatchesOfAtMostBatchSize()
    {
        const int playerCount = PlayerPhotoBackfillService.BatchSize + 50;
        for (var i = 0; i < playerCount; i++)
        {
            var qid = $"Q{i}";
            await SeedPlayerAsync(qid);
            _wikidataClient.SetPhoto(qid, $"https://example.com/{i}.jpg");
        }

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(2));
        Assert.That(result.PlayersBackfilled, Is.EqualTo(playerCount));
        Assert.That(_wikidataClient.QueriedPhotoBatches, Has.Count.EqualTo(2));
        Assert.That(_wikidataClient.QueriedPhotoBatches[0], Has.Count.EqualTo(PlayerPhotoBackfillService.BatchSize),
            "each batch must stay within the bounded-query batch size, never fetch everything in one VALUES clause");
        Assert.That(_wikidataClient.QueriedPhotoBatches[1], Has.Count.EqualTo(50));
    }

    [Test]
    public async Task REQ214_BackfillAsync_ReRunAfterSuccessfulBackfill_TouchesNothing()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetPhoto("Q1519", "https://example.com/henry.jpg");
        await BuildService().BackfillAsync();

        // A fresh service instance, same shape as a second CLI invocation —
        // no in-memory state carries over between runs.
        var secondResult = await BuildService().BackfillAsync();

        Assert.That(secondResult.BatchesProcessed, Is.EqualTo(0));
        Assert.That(secondResult.PlayersBackfilled, Is.EqualTo(0));
        Assert.That((await _playerStoreRepository.GetPlayerByIdAsync(player.Id))!.PhotoUrl, Is.EqualTo("https://example.com/henry.jpg"));
    }

    [Test]
    public async Task REQ214_BackfillAsync_BatchFails_LogsAndContinuesToNextBatch_WithoutFailingTheRun()
    {
        // Two full batches: the first fails outright, the second succeeds —
        // asserts the documented log-and-continue judgment call (not
        // PlayerNameIndexImporter's retry-then-fail-loud).
        const int playerCount = PlayerPhotoBackfillService.BatchSize * 2;
        for (var i = 0; i < playerCount; i++)
        {
            var qid = $"Q{i}";
            await SeedPlayerAsync(qid);
            _wikidataClient.SetPhoto(qid, $"https://example.com/{i}.jpg");
        }
        _wikidataClient.FailNextPhotoBatches(1);

        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(2));
        Assert.That(result.BatchesFailed, Is.EqualTo(1));
        Assert.That(result.PlayersBackfilled, Is.EqualTo(PlayerPhotoBackfillService.BatchSize),
            "the failed batch's players stay un-backfilled this run, but the run itself must still complete and process the remaining batch");
    }

    [Test]
    public async Task REQ214_BackfillAsync_BatchFails_FailedBatchesPlayersStillShowAsMissingPhoto_ForANextRun()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetPhoto("Q1519", "https://example.com/henry.jpg");
        _wikidataClient.FailNextPhotoBatches(1);

        await BuildService().BackfillAsync();

        Assert.That((await _playerStoreRepository.GetPlayerByIdAsync(player.Id))!.PhotoUrl, Is.Null,
            "a failed batch must leave its players' PhotoUrl untouched — a later re-run's GetPlayersMissingPhotoAsync will surface them again automatically");
    }

    [Test]
    public async Task REQ214_BackfillAsync_NoPlayersAtAll_ReturnsZeroedResultWithoutQueryingWikidata()
    {
        var result = await BuildService().BackfillAsync();

        Assert.That(result.BatchesProcessed, Is.EqualTo(0));
        Assert.That(result.PlayersBackfilled, Is.EqualTo(0));
        Assert.That(result.BatchesFailed, Is.EqualTo(0));
        Assert.That(_wikidataClient.QueriedPhotoBatches, Is.Empty);
    }

    // Regression test: a malformed Player.WikidataQid used to propagate a
    // raw ArgumentException out of WikidataClient.QueryPlayerPhotosByQidsAsync,
    // uncaught by BackfillAsync's `catch (WikidataQueryException)`, crashing
    // the whole `backfill-player-photos` process instead of being
    // log-and-continued like every other failure mode this service handles.
    // Reproduced against a real Postgres database seeded with this repo's own
    // `/internal/test-data` E2E fixture QIDs (shaped like
    // "Qtest-<guid>", which fail the real "^Q\d+$" QID pattern) — not a
    // hypothetical input.
    [Test]
    public async Task REQ214_BackfillAsync_BatchContainsMalformedWikidataQid_SkipsThatPlayerButBackfillsTheRestWithoutThrowing()
    {
        var goodPlayer = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetPhoto("Q1519", "https://example.com/henry.jpg");
        var badPlayer = await SeedPlayerAsync("Qtest-99195db1-cbff-4491-8007-8d497b926a65");

        PlayerPhotoBackfillResult result = null!;
        Assert.DoesNotThrowAsync(async () => result = await BuildService().BackfillAsync());

        Assert.That(result.PlayersBackfilled, Is.EqualTo(1));
        Assert.That(result.BatchesFailed, Is.EqualTo(0),
            "a malformed QID on one player is a per-player skip, not a whole-batch failure");
        Assert.That((await _playerStoreRepository.GetPlayerByIdAsync(goodPlayer.Id))!.PhotoUrl,
            Is.EqualTo("https://example.com/henry.jpg"));
        Assert.That((await _playerStoreRepository.GetPlayerByIdAsync(badPlayer.Id))!.PhotoUrl, Is.Null);
        Assert.That(_wikidataClient.QueriedPhotoBatches, Has.Count.EqualTo(1));
        Assert.That(_wikidataClient.QueriedPhotoBatches[0], Does.Not.Contain(badPlayer.WikidataQid),
            "the malformed QID must be filtered out before the batch is sent to Wikidata, not just after");
    }

    // Same bug, edge case: every player in the batch has a malformed QID —
    // the filtered batch sent to Wikidata is empty, which must still be
    // handled gracefully (QueryPlayerPhotosByQidsAsync's own empty-list
    // short-circuit) rather than crashing or looping forever.
    [Test]
    public async Task REQ214_BackfillAsync_EveryPlayerInBatchHasMalformedWikidataQid_CompletesWithoutThrowing()
    {
        var badPlayer = await SeedPlayerAsync("not-a-qid");

        PlayerPhotoBackfillResult result = null!;
        Assert.DoesNotThrowAsync(async () => result = await BuildService().BackfillAsync());

        Assert.That(result.BatchesProcessed, Is.EqualTo(1));
        Assert.That(result.PlayersBackfilled, Is.EqualTo(0));
        Assert.That(result.BatchesFailed, Is.EqualTo(0));
        Assert.That((await _playerStoreRepository.GetPlayerByIdAsync(badPlayer.Id))!.PhotoUrl, Is.Null);
    }
}

using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// Bug-bundle fix (2026-07-27): PlayerNameIndexWordBackfiller fixes
// PlayerNameIndex rows that predate PlayerNameIndexWord's introduction
// (ADR-0044, migration 20260726120000_AddPlayerNameIndexWord) — the scenario
// a row imported before that migration shipped would be in: zero
// PlayerNameIndexWord rows, silently failing any surname-only search.
public class PlayerNameIndexWordBackfillerTests
{
    private XGArcadeDbContext _dbContext = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    [Test]
    public async Task BackfillAsync_PlayerNameIndexRowWithNoWordRows_CreatesThem()
    {
        var playerId = Guid.NewGuid();
        // Simulates a pre-word-index import: a PlayerNameIndex row exists,
        // but (unlike a fresh UpsertManyAsync call) no PlayerNameIndexWord
        // rows were ever created for it.
        _dbContext.PlayerNameIndexEntries.Add(new PlayerNameIndex
        {
            PlayerId = playerId,
            PrimaryName = "Clarence Seedorf",
            NormalizedName = "clarence seedorf",
        });
        await _dbContext.SaveChangesAsync();

        await PlayerNameIndexWordBackfiller.BackfillAsync(_dbContext);

        var words = await _dbContext.PlayerNameIndexWords.Where(w => w.PlayerId == playerId).Select(w => w.Word).ToListAsync();
        Assert.That(words, Is.EquivalentTo(new[] { "clarence", "seedorf" }));
    }

    [Test]
    public async Task BackfillAsync_IsIdempotent_NoChangeWhenWordsAlreadyCorrect()
    {
        var playerId = Guid.NewGuid();
        _dbContext.PlayerNameIndexEntries.Add(new PlayerNameIndex
        {
            PlayerId = playerId,
            PrimaryName = "Clarence Seedorf",
            NormalizedName = "clarence seedorf",
        });
        _dbContext.PlayerNameIndexWords.AddRange(
            new PlayerNameIndexWord { PlayerId = playerId, Word = "clarence" },
            new PlayerNameIndexWord { PlayerId = playerId, Word = "seedorf" });
        await _dbContext.SaveChangesAsync();

        await PlayerNameIndexWordBackfiller.BackfillAsync(_dbContext);

        var words = await _dbContext.PlayerNameIndexWords.Where(w => w.PlayerId == playerId).Select(w => w.Word).ToListAsync();
        Assert.That(words, Is.EquivalentTo(new[] { "clarence", "seedorf" }));
    }

    [Test]
    public async Task BackfillAsync_NoPlayerNameIndexRows_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(async () => await PlayerNameIndexWordBackfiller.BackfillAsync(_dbContext));
    }

    [Test]
    public async Task BackfillAsync_MultipleRows_BackfillsEachIndependently()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        _dbContext.PlayerNameIndexEntries.AddRange(
            new PlayerNameIndex { PlayerId = playerA, PrimaryName = "Clarence Seedorf", NormalizedName = "clarence seedorf" },
            new PlayerNameIndex { PlayerId = playerB, PrimaryName = "Lionel Messi", NormalizedName = "lionel messi" });
        await _dbContext.SaveChangesAsync();

        await PlayerNameIndexWordBackfiller.BackfillAsync(_dbContext);

        var wordsA = await _dbContext.PlayerNameIndexWords.Where(w => w.PlayerId == playerA).Select(w => w.Word).ToListAsync();
        var wordsB = await _dbContext.PlayerNameIndexWords.Where(w => w.PlayerId == playerB).Select(w => w.Word).ToListAsync();
        Assert.That(wordsA, Is.EquivalentTo(new[] { "clarence", "seedorf" }));
        Assert.That(wordsB, Is.EquivalentTo(new[] { "lionel", "messi" }));
    }
}

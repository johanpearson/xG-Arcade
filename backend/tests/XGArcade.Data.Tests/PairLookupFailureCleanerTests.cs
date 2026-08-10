using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// 2026-08-01 live-incident follow-up to ADR-0052: PairLookupFailureCleaner
// clears PairLookupFailure rows stuck at or above
// PlayerCacheWarmingService.PersistentFailureThreshold, without touching any
// other table — see that class's own doc comment for why it's deliberately
// narrower (pair-scoped, not club-name-scoped) than StaleClubAttributeCleaner.
public class PairLookupFailureCleanerTests
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

    private async Task SeedPairLookupFailureAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        int consecutiveFailureCount)
    {
        _dbContext.PairLookupFailures.Add(new PairLookupFailure
        {
            FirstAttributeType = firstAttributeType,
            FirstAttributeValue = firstAttributeValue,
            SecondAttributeType = secondAttributeType,
            SecondAttributeValue = secondAttributeValue,
            ConsecutiveFailureCount = consecutiveFailureCount,
            LastFailedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task REQ110_ClearPersistentFailuresAsync_PairAtThreshold_IsRemoved()
    {
        await SeedPairLookupFailureAsync("club", "Napoli", "club", "Arsenal", consecutiveFailureCount: 2);

        var clearedPairNames = await PairLookupFailureCleaner.ClearPersistentFailuresAsync(_dbContext);

        Assert.That(clearedPairNames, Is.EquivalentTo(new[] { "Napoli x Arsenal" }));
        Assert.That(await _dbContext.PairLookupFailures.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ110_ClearPersistentFailuresAsync_PairAboveThreshold_IsRemoved()
    {
        await SeedPairLookupFailureAsync("club", "Napoli", "club", "Arsenal", consecutiveFailureCount: 5);

        var clearedPairNames = await PairLookupFailureCleaner.ClearPersistentFailuresAsync(_dbContext);

        Assert.That(clearedPairNames, Is.EquivalentTo(new[] { "Napoli x Arsenal" }));
        Assert.That(await _dbContext.PairLookupFailures.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ110_ClearPersistentFailuresAsync_PairBelowThreshold_IsLeftAlone()
    {
        await SeedPairLookupFailureAsync("club", "Napoli", "club", "Arsenal", consecutiveFailureCount: 1);

        var clearedPairNames = await PairLookupFailureCleaner.ClearPersistentFailuresAsync(_dbContext);

        Assert.That(clearedPairNames, Is.Empty,
            "a single-run failure is still a transient blip, not a structural one — PlayerCacheWarmingService's own next run should still get a real chance");
        Assert.That(await _dbContext.PairLookupFailures.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ110_ClearPersistentFailuresAsync_MixOfBoth_OnlyRemovesThoseAtOrAboveThreshold()
    {
        await SeedPairLookupFailureAsync("nationality", "France", "club", "Napoli", consecutiveFailureCount: 2);
        await SeedPairLookupFailureAsync("club", "Arsenal", "club", "AC Milan", consecutiveFailureCount: 3);
        await SeedPairLookupFailureAsync("nationality", "Spain", "club", "Sevilla", consecutiveFailureCount: 1);

        var clearedPairNames = await PairLookupFailureCleaner.ClearPersistentFailuresAsync(_dbContext);

        Assert.That(clearedPairNames, Is.EquivalentTo(new[] { "France x Napoli", "Arsenal x AC Milan" }));
        Assert.That(await _dbContext.PairLookupFailures.CountAsync(), Is.EqualTo(1));
        Assert.That((await _dbContext.PairLookupFailures.SingleAsync()).SecondAttributeValue, Is.EqualTo("Sevilla"));
    }

    [Test]
    public async Task REQ110_ClearPersistentFailuresAsync_EmptyTable_IsANoOp_DoesNotThrow()
    {
        var clearedPairNames = await PairLookupFailureCleaner.ClearPersistentFailuresAsync(_dbContext);

        Assert.That(clearedPairNames, Is.Empty);
        Assert.That(await _dbContext.PairLookupFailures.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ110_ClearPersistentFailuresAsync_IsSafeToRunAgain_WhenNothingIsLeftToClear()
    {
        await SeedPairLookupFailureAsync("club", "Napoli", "club", "Arsenal", consecutiveFailureCount: 2);
        await PairLookupFailureCleaner.ClearPersistentFailuresAsync(_dbContext);

        var secondRunClearedPairNames = await PairLookupFailureCleaner.ClearPersistentFailuresAsync(_dbContext);

        Assert.That(secondRunClearedPairNames, Is.Empty);
    }
}

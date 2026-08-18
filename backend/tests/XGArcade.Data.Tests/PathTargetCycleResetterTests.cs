using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// S-141: PathTargetCycleResetter wipes REQ-1208/ADR-0058's target-cycle
// bookkeeping (the PathTargetCycle singleton row + every PathCycleTargetUsage
// row) after S-137-140 narrowed xG Path's eligible player pool — see that
// class's own doc comment for why the usage bookkeeping doesn't self-correct
// the way ObservedPoolSize does.
public class PathTargetCycleResetterTests
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

    private static readonly Guid SingletonCycleId = new("00000000-0000-0000-0000-000000000001");

    private async Task SeedCycleStateAsync(int cycleNumber, int observedPoolSize, int usedInCycleCount)
    {
        _dbContext.PathTargetCycles.Add(new PathTargetCycle
        {
            Id = SingletonCycleId,
            CycleNumber = cycleNumber,
            ObservedPoolSize = observedPoolSize,
            UsedInCycleCount = usedInCycleCount,
            LastCycleCompletedAt = null,
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedUsageRowAsync(Guid playerId, int cycleNumber)
    {
        _dbContext.PathCycleTargetUsages.Add(new PathCycleTargetUsage
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            CycleNumber = cycleNumber,
        });
        await _dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task REQ1208_ResetAsync_CycleRowAndUsageRowsExist_RemovesBoth()
    {
        await SeedCycleStateAsync(cycleNumber: 3, observedPoolSize: 120, usedInCycleCount: 40);
        await SeedUsageRowAsync(Guid.NewGuid(), cycleNumber: 3);
        await SeedUsageRowAsync(Guid.NewGuid(), cycleNumber: 3);

        var result = await PathTargetCycleResetter.ResetAsync(_dbContext);

        Assert.That(result.RemovedUsageCount, Is.EqualTo(2));
        Assert.That(result.CycleRowExisted, Is.True);
        Assert.That(await _dbContext.PathTargetCycles.CountAsync(), Is.EqualTo(0));
        Assert.That(await _dbContext.PathCycleTargetUsages.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ1208_ResetAsync_RemovesUsageRowsFromEveryCycle_NotJustTheCurrentOne()
    {
        await SeedCycleStateAsync(cycleNumber: 2, observedPoolSize: 90, usedInCycleCount: 10);
        await SeedUsageRowAsync(Guid.NewGuid(), cycleNumber: 1);
        await SeedUsageRowAsync(Guid.NewGuid(), cycleNumber: 2);

        var result = await PathTargetCycleResetter.ResetAsync(_dbContext);

        Assert.That(result.RemovedUsageCount, Is.EqualTo(2),
            "a leftover cycle-1 usage row would otherwise collide with the fresh cycle 1 this reset restarts at");
        Assert.That(await _dbContext.PathCycleTargetUsages.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ1208_ResetAsync_NoCycleRowYet_IsANoOp_DoesNotThrow()
    {
        var result = await PathTargetCycleResetter.ResetAsync(_dbContext);

        Assert.That(result.RemovedUsageCount, Is.EqualTo(0));
        Assert.That(result.CycleRowExisted, Is.False,
            "no PathTargetCycle row is xG Path's own 'never generated a round yet' state — this must succeed, not error");
    }

    [Test]
    public async Task REQ1208_ResetAsync_IsSafeToRunAgain_WhenNothingIsLeftToReset()
    {
        await SeedCycleStateAsync(cycleNumber: 1, observedPoolSize: 50, usedInCycleCount: 5);
        await SeedUsageRowAsync(Guid.NewGuid(), cycleNumber: 1);
        await PathTargetCycleResetter.ResetAsync(_dbContext);

        var secondRunResult = await PathTargetCycleResetter.ResetAsync(_dbContext);

        Assert.That(secondRunResult.RemovedUsageCount, Is.EqualTo(0));
        Assert.That(secondRunResult.CycleRowExisted, Is.False);
    }
}

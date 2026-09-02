using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// Core.Social (COMP-16)/ADR-0103, S-208: MatchmakingOptInRepository's basic
// persistence round-trips. Schema + CRUD only — no pairing/expiry-sweep
// business logic (that's S-210).
public class MatchmakingOptInRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IMatchmakingOptInRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new MatchmakingOptInRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task AddOptInAsync_ThenGetOptInByIdAsync_PersistsAndRetrievesTheRow()
    {
        var userId = Guid.NewGuid();
        var optedInAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var expiresAt = optedInAt.AddHours(12);
        var optIn = new MatchmakingOptIn
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OptedInAt = optedInAt,
            ExpiresAt = expiresAt,
        };

        var added = await _repository.AddOptInAsync(optIn);

        Assert.That(added, Is.SameAs(optIn));
        var result = await _repository.GetOptInByIdAsync(optIn.Id);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.UserId, Is.EqualTo(userId));
        Assert.That(result.OptedInAt, Is.EqualTo(optedInAt));
        Assert.That(result.ExpiresAt, Is.EqualTo(expiresAt));
        Assert.That(result.Status, Is.EqualTo(MatchmakingOptInStatus.Waiting), "Status defaults to Waiting");
        Assert.That(result.ResultingMatchId, Is.Null);
    }

    [Test]
    public async Task GetOptInByIdAsync_UnknownId_ReturnsNull()
    {
        var result = await _repository.GetOptInByIdAsync(Guid.NewGuid());

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetWaitingOptInsAsync_ReturnsOnlyWaitingRows()
    {
        var waiting = new MatchmakingOptIn
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), OptedInAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(12),
        };
        var paired = new MatchmakingOptIn
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), OptedInAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(12),
            Status = MatchmakingOptInStatus.Paired, ResultingMatchId = Guid.NewGuid(),
        };
        var expired = new MatchmakingOptIn
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), OptedInAt = DateTime.UtcNow.AddHours(-13), ExpiresAt = DateTime.UtcNow.AddHours(-1),
            Status = MatchmakingOptInStatus.Expired,
        };
        await _repository.AddOptInAsync(waiting);
        await _repository.AddOptInAsync(paired);
        await _repository.AddOptInAsync(expired);

        var result = await _repository.GetWaitingOptInsAsync();

        Assert.That(result.Select(o => o.Id), Is.EquivalentTo(new[] { waiting.Id }));
    }

    [Test]
    public async Task UpdateOptInStatusAsync_SetsStatus_LeavesResultingMatchIdNull_WhenNotSupplied()
    {
        var optIn = new MatchmakingOptIn
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), OptedInAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(12),
        };
        await _repository.AddOptInAsync(optIn);

        await _repository.UpdateOptInStatusAsync(optIn.Id, MatchmakingOptInStatus.Expired);

        var result = await _repository.GetOptInByIdAsync(optIn.Id);
        Assert.That(result!.Status, Is.EqualTo(MatchmakingOptInStatus.Expired));
        Assert.That(result.ResultingMatchId, Is.Null);
    }

    [Test]
    public async Task UpdateOptInStatusAsync_FoldsInResultingMatchId_WhenSupplied()
    {
        var optIn = new MatchmakingOptIn
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), OptedInAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(12),
        };
        await _repository.AddOptInAsync(optIn);
        var resultingMatchId = Guid.NewGuid();

        await _repository.UpdateOptInStatusAsync(optIn.Id, MatchmakingOptInStatus.Paired, resultingMatchId);

        var result = await _repository.GetOptInByIdAsync(optIn.Id);
        Assert.That(result!.Status, Is.EqualTo(MatchmakingOptInStatus.Paired));
        Assert.That(result.ResultingMatchId, Is.EqualTo(resultingMatchId));
    }

    [Test]
    public void UpdateOptInStatusAsync_UnknownId_Throws()
    {
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.UpdateOptInStatusAsync(Guid.NewGuid(), MatchmakingOptInStatus.Expired));
    }
}

using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// Core.Social (COMP-16)/ADR-0103, S-208: ChallengeRepository's basic
// persistence round-trips. Schema + CRUD only — no accept/decline/
// existing-friendship-precondition business logic (that's S-210).
public class ChallengeRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IChallengeRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new ChallengeRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task AddChallengeAsync_ThenGetChallengeByIdAsync_PersistsAndRetrievesTheRow()
    {
        var challengerId = Guid.NewGuid();
        var challengedId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var challenge = new Challenge
        {
            Id = Guid.NewGuid(),
            ChallengerUserId = challengerId,
            ChallengedUserId = challengedId,
            CreatedAt = createdAt,
        };

        var added = await _repository.AddChallengeAsync(challenge);

        Assert.That(added, Is.SameAs(challenge));
        var result = await _repository.GetChallengeByIdAsync(challenge.Id);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ChallengerUserId, Is.EqualTo(challengerId));
        Assert.That(result.ChallengedUserId, Is.EqualTo(challengedId));
        Assert.That(result.Status, Is.EqualTo(ChallengeStatus.Pending), "Status defaults to Pending");
        Assert.That(result.ResultingMatchId, Is.Null);
    }

    [Test]
    public async Task GetChallengeByIdAsync_UnknownId_ReturnsNull()
    {
        var result = await _repository.GetChallengeByIdAsync(Guid.NewGuid());

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetPendingChallengesForUserAsync_ReturnsOnlyPendingChallengesForThatChallengedUser()
    {
        var challengedId = Guid.NewGuid();
        var otherChallengedId = Guid.NewGuid();
        var pending = new Challenge
        {
            Id = Guid.NewGuid(), ChallengerUserId = Guid.NewGuid(), ChallengedUserId = challengedId, CreatedAt = DateTime.UtcNow,
        };
        var resolved = new Challenge
        {
            Id = Guid.NewGuid(), ChallengerUserId = Guid.NewGuid(), ChallengedUserId = challengedId,
            Status = ChallengeStatus.Accepted, CreatedAt = DateTime.UtcNow, ResolvedAt = DateTime.UtcNow,
        };
        var otherUsers = new Challenge
        {
            Id = Guid.NewGuid(), ChallengerUserId = Guid.NewGuid(), ChallengedUserId = otherChallengedId, CreatedAt = DateTime.UtcNow,
        };
        await _repository.AddChallengeAsync(pending);
        await _repository.AddChallengeAsync(resolved);
        await _repository.AddChallengeAsync(otherUsers);

        var result = await _repository.GetPendingChallengesForUserAsync(challengedId);

        Assert.That(result.Select(c => c.Id), Is.EquivalentTo(new[] { pending.Id }));
    }

    [Test]
    public async Task UpdateChallengeStatusAsync_SetsStatusAndResolvedAt_LeavesResultingMatchIdNull_WhenNotSupplied()
    {
        var challenge = new Challenge
        {
            Id = Guid.NewGuid(), ChallengerUserId = Guid.NewGuid(), ChallengedUserId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
        };
        await _repository.AddChallengeAsync(challenge);
        var resolvedAt = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);

        await _repository.UpdateChallengeStatusAsync(challenge.Id, ChallengeStatus.Declined, resolvedAt);

        var result = await _repository.GetChallengeByIdAsync(challenge.Id);
        Assert.That(result!.Status, Is.EqualTo(ChallengeStatus.Declined));
        Assert.That(result.ResolvedAt, Is.EqualTo(resolvedAt));
        Assert.That(result.ResultingMatchId, Is.Null);
    }

    [Test]
    public async Task UpdateChallengeStatusAsync_FoldsInResultingMatchId_WhenSupplied()
    {
        var challenge = new Challenge
        {
            Id = Guid.NewGuid(), ChallengerUserId = Guid.NewGuid(), ChallengedUserId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
        };
        await _repository.AddChallengeAsync(challenge);
        var resultingMatchId = Guid.NewGuid();

        await _repository.UpdateChallengeStatusAsync(challenge.Id, ChallengeStatus.Accepted, DateTime.UtcNow, resultingMatchId);

        var result = await _repository.GetChallengeByIdAsync(challenge.Id);
        Assert.That(result!.Status, Is.EqualTo(ChallengeStatus.Accepted));
        Assert.That(result.ResultingMatchId, Is.EqualTo(resultingMatchId));
    }

    [Test]
    public void UpdateChallengeStatusAsync_UnknownId_Throws()
    {
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.UpdateChallengeStatusAsync(Guid.NewGuid(), ChallengeStatus.Declined, DateTime.UtcNow));
    }
}

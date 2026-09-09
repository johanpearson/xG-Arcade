using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// COMP-18 (Games.XGHigherLower)/ADR-0110: HigherLowerInstanceRepository's own
// persistence round-trip (AddInstanceAsync/GetInstanceByIdAsync, including
// its owned Comparators collection), plus REQ-1504/S-225's own
// GetAttemptAsync/SaveAttemptAsync/AnonymizeAttemptsByUserIdAsync methods.
// Same InMemory-backed DbContext pattern as PredictInstanceRepositoryTests.
public class HigherLowerInstanceRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IHigherLowerInstanceRepository _repository = null!;
    private IPlayerRepository _playerRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new HigherLowerInstanceRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task AddInstanceAsync_ThenGetInstanceByIdAsync_ReturnsInstanceWithComparators()
    {
        var baseline = await AddPlayerAsync("Baseline Player");
        var comparatorPlayer = await AddPlayerAsync("Comparator Player");
        var instanceId = Guid.NewGuid();
        var instance = new HigherLowerInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            StatCategory = "trophy",
            BaselinePlayerId = baseline.Id,
            BaselineValue = 3,
            Comparators =
            [
                new HigherLowerComparator
                {
                    Id = Guid.NewGuid(),
                    HigherLowerInstanceId = instanceId,
                    SequencePosition = 0,
                    PlayerId = comparatorPlayer.Id,
                    Value = 5,
                },
            ],
        };

        await _repository.AddInstanceAsync(instance);

        var found = await _repository.GetInstanceByIdAsync(instanceId);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.StatCategory, Is.EqualTo("trophy"));
        Assert.That(found.BaselinePlayerId, Is.EqualTo(baseline.Id));
        Assert.That(found.BaselineValue, Is.EqualTo(3));
        Assert.That(found.Comparators, Has.Count.EqualTo(1));
        Assert.That(found.Comparators[0].PlayerId, Is.EqualTo(comparatorPlayer.Id));
        Assert.That(found.Comparators[0].Value, Is.EqualTo(5));
        Assert.That(found.Comparators[0].SequencePosition, Is.EqualTo(0));
    }

    [Test]
    public async Task GetInstanceByIdAsync_ReturnsNull_WhenNoInstanceMatches()
    {
        var found = await _repository.GetInstanceByIdAsync(Guid.NewGuid());

        Assert.That(found, Is.Null);
    }

    private async Task<Player> AddPlayerAsync(string fullName)
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = fullName };
        await _playerRepository.AddPlayerAsync(player);
        return player;
    }

    // ---- REQ-1504/S-225: GetAttemptAsync/SaveAttemptAsync/AnonymizeAttemptsByUserIdAsync ----

    [Test]
    public async Task GetAttemptAsync_ReturnsNull_WhenNoAttemptExists()
    {
        var baseline = await AddPlayerAsync("Baseline Player");
        var instanceId = await AddMinimalInstanceAsync(baseline.Id);

        var found = await _repository.GetAttemptAsync(instanceId, Guid.NewGuid());

        Assert.That(found, Is.Null);
    }

    [Test]
    public async Task SaveAttemptAsync_NoExistingRow_CreatesNewAttempt()
    {
        var baseline = await AddPlayerAsync("Baseline Player");
        var instanceId = await AddMinimalInstanceAsync(baseline.Id);
        var userId = Guid.NewGuid();

        await _repository.SaveAttemptAsync(instanceId, userId, streakLength: 2, currentBaselinePlayerId: baseline.Id, currentBaselineValue: 7, hasEnded: false);

        var found = await _repository.GetAttemptAsync(instanceId, userId);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.HigherLowerInstanceId, Is.EqualTo(instanceId));
        Assert.That(found.UserId, Is.EqualTo(userId));
        Assert.That(found.StreakLength, Is.EqualTo(2));
        Assert.That(found.CurrentBaselinePlayerId, Is.EqualTo(baseline.Id));
        Assert.That(found.CurrentBaselineValue, Is.EqualTo(7));
        Assert.That(found.HasEnded, Is.False);
    }

    [Test]
    public async Task SaveAttemptAsync_ExistingRow_UpdatesInPlace_NeverInsertsASecondRow()
    {
        var baseline = await AddPlayerAsync("Baseline Player");
        var comparator = await AddPlayerAsync("Comparator Player");
        var instanceId = await AddMinimalInstanceAsync(baseline.Id);
        var userId = Guid.NewGuid();

        await _repository.SaveAttemptAsync(instanceId, userId, streakLength: 1, currentBaselinePlayerId: baseline.Id, currentBaselineValue: 3, hasEnded: false);
        await _repository.SaveAttemptAsync(instanceId, userId, streakLength: 2, currentBaselinePlayerId: comparator.Id, currentBaselineValue: 9, hasEnded: true);

        var found = await _repository.GetAttemptAsync(instanceId, userId);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.StreakLength, Is.EqualTo(2));
        Assert.That(found.CurrentBaselinePlayerId, Is.EqualTo(comparator.Id));
        Assert.That(found.CurrentBaselineValue, Is.EqualTo(9));
        Assert.That(found.HasEnded, Is.True);
        Assert.That(await _dbContext.HigherLowerAttempts.CountAsync(a => a.HigherLowerInstanceId == instanceId && a.UserId == userId), Is.EqualTo(1));
    }

    [Test]
    public async Task AnonymizeAttemptsByUserIdAsync_SetsUserIdNull_ForMatchingAttempts_LeavesOtherUsersUntouched()
    {
        var baseline = await AddPlayerAsync("Baseline Player");
        var instanceId = await AddMinimalInstanceAsync(baseline.Id);
        var targetUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        await _repository.SaveAttemptAsync(instanceId, targetUserId, streakLength: 1, currentBaselinePlayerId: baseline.Id, currentBaselineValue: 3, hasEnded: false);
        await _repository.SaveAttemptAsync(instanceId, otherUserId, streakLength: 2, currentBaselinePlayerId: baseline.Id, currentBaselineValue: 5, hasEnded: false);

        await _repository.AnonymizeAttemptsByUserIdAsync(targetUserId);

        Assert.That(await _repository.GetAttemptAsync(instanceId, targetUserId), Is.Null,
            "the anonymized row no longer matches a lookup by the deleted user's id");
        var otherAttempt = await _repository.GetAttemptAsync(instanceId, otherUserId);
        Assert.That(otherAttempt, Is.Not.Null);
        Assert.That(otherAttempt!.StreakLength, Is.EqualTo(2), "another user's attempt must be untouched");

        var anonymizedRow = await _dbContext.HigherLowerAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.HigherLowerInstanceId == instanceId && a.StreakLength == 1);
        Assert.That(anonymizedRow, Is.Not.Null, "the row itself must survive anonymization, only UserId is cleared");
        Assert.That(anonymizedRow!.UserId, Is.Null);
    }

    // Minimal instance (no comparators) — enough for the attempt-focused
    // tests above, which don't exercise Comparators' own content at all.
    private async Task<Guid> AddMinimalInstanceAsync(Guid baselinePlayerId)
    {
        var instanceId = Guid.NewGuid();
        await _repository.AddInstanceAsync(new HigherLowerInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            StatCategory = "trophy",
            BaselinePlayerId = baselinePlayerId,
            BaselineValue = 1,
            Comparators = [],
        });
        return instanceId;
    }
}

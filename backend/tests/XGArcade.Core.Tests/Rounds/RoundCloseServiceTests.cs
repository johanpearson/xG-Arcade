using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Core.Rounds;
using XGArcade.Core.Scoring;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Tests.Rounds;

// REQ-205/REQ-806 (docs/requirements-document.md §4.3/§4.9): the EndTime-
// pull-forward half built in S-008. Locking Guess.FinalUniquenessScore/
// FinalPoints is REQ-205's other half, built in S-011 — see
// RoundCloseServiceScoringTests for those cases.
public class RoundCloseServiceTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IRoundRepository _roundRepository = null!;
    private IGuessRepository _guessRepository = null!;
    private RoundCloseService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _roundRepository = new RoundRepository(_dbContext);
        _guessRepository = new GuessRepository(_dbContext);
        var gameModuleResolver = new GameModuleResolver([new FakeGameModule("xg-grid")]);
        _service = new RoundCloseService(
            _roundRepository,
            new ScoreLockingService(_guessRepository, _roundRepository, gameModuleResolver));
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private async Task<Round> SeedRoundAsync(DateTime startTime, DateTime endTime)
    {
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = "xg-grid",
            GameInstanceId = Guid.NewGuid(),
            StartTime = startTime,
            EndTime = endTime,
            AllowGuessChange = true,
        };
        _dbContext.Rounds.Add(round);
        await _dbContext.SaveChangesAsync();
        return round;
    }

    [Test]
    public async Task REQ205_CloseRoundAsync_PullsEndTimeForwardToClosedAt_WhenRoundStillActive()
    {
        var now = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        var round = await SeedRoundAsync(startTime: now.AddDays(-1), endTime: now.AddDays(2));

        var closed = await _service.CloseRoundAsync(round.Id, now);

        Assert.That(closed, Is.Not.Null);
        Assert.That(closed!.EndTime, Is.EqualTo(now));

        var persisted = await _roundRepository.GetByIdAsync(round.Id);
        Assert.That(persisted!.EndTime, Is.EqualTo(now));
    }

    [Test]
    public async Task REQ205_CloseRoundAsync_LeavesEndTimeUnchanged_WhenRoundAlreadyEndedBeforeClosedAt()
    {
        var originalEndTime = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        var round = await SeedRoundAsync(startTime: originalEndTime.AddDays(-3), endTime: originalEndTime);
        var muchLater = originalEndTime.AddDays(5);

        var closed = await _service.CloseRoundAsync(round.Id, muchLater);

        Assert.That(closed, Is.Not.Null);
        Assert.That(closed!.EndTime, Is.EqualTo(originalEndTime),
            "force-close must never push EndTime later than what was already scheduled");
    }

    [Test]
    public async Task REQ806_CloseRoundAsync_ReturnsNull_ForUnknownRoundId()
    {
        var result = await _service.CloseRoundAsync(Guid.NewGuid(), DateTime.UtcNow);

        Assert.That(result, Is.Null);
    }

    // ---- REQ-408: Round.ClosedAt -------------------------------------------

    [Test]
    public async Task REQ408_CloseRoundAsync_FirstClose_SetsClosedAtToTheGivenClosedAtValue()
    {
        var now = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var round = await SeedRoundAsync(startTime: now.AddDays(-2), endTime: now.AddDays(2));

        var closed = await _service.CloseRoundAsync(round.Id, now);

        Assert.That(closed!.ClosedAt, Is.EqualTo(now));
        var persisted = await _roundRepository.GetByIdAsync(round.Id);
        Assert.That(persisted!.ClosedAt, Is.EqualTo(now));
    }

    [Test]
    public async Task REQ408_CloseRoundAsync_CalledTwice_FirstCloseWins_SecondCallNeverOverwritesClosedAt()
    {
        var firstClosedAt = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var secondClosedAt = firstClosedAt.AddDays(3);
        var round = await SeedRoundAsync(startTime: firstClosedAt.AddDays(-2), endTime: firstClosedAt.AddDays(5));

        await _service.CloseRoundAsync(round.Id, firstClosedAt);
        var secondCloseResult = await _service.CloseRoundAsync(round.Id, secondClosedAt);

        Assert.That(secondCloseResult!.ClosedAt, Is.EqualTo(firstClosedAt),
            "first close wins, same idempotent pattern as the EndTime pull-forward");
    }

    // Regression test: quality-architect's pre-merge review of the REQ-408
    // diff found ClosedAt was being persisted BEFORE LockRoundScoresAsync ran
    // to completion — a window where LeaderboardService.GetClosedRoundLeaderboardAsync
    // (which gates purely on ClosedAt being non-null) could see a round as
    // closed/complete while scores were still unlocked, or would stay stuck
    // that way forever if locking failed partway through. CloseRoundAsync
    // must never persist ClosedAt unless LockRoundScoresAsync completed
    // without throwing.
    [Test]
    public async Task REQ408_CloseRoundAsync_LockRoundScoresAsyncThrows_ClosedAtIsNeverPersisted()
    {
        var now = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var round = await SeedRoundAsync(startTime: now.AddDays(-2), endTime: now.AddDays(2));
        var fakeScoreLockingService = new FakeScoreLockingService
        {
            ThrowOnLock = new InvalidOperationException("simulated partial locking failure"),
        };
        var service = new RoundCloseService(_roundRepository, fakeScoreLockingService);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await service.CloseRoundAsync(round.Id, now));

        var persisted = await _roundRepository.GetByIdAsync(round.Id);
        Assert.That(persisted!.ClosedAt, Is.Null,
            "a round must never look closed/complete to readers while LockRoundScoresAsync hasn't succeeded");
    }

    // Companion to the failure case above: once locking DOES succeed (even
    // on a retry after a prior failed attempt), ClosedAt must still get set —
    // the fix must close the unlocked-scores window without breaking the
    // ordinary success path.
    [Test]
    public async Task REQ408_CloseRoundAsync_LockRoundScoresAsyncSucceedsAfterPriorFailure_ClosedAtIsSetOnRetry()
    {
        var now = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var round = await SeedRoundAsync(startTime: now.AddDays(-2), endTime: now.AddDays(2));
        var fakeScoreLockingService = new FakeScoreLockingService
        {
            ThrowOnLock = new InvalidOperationException("simulated partial locking failure"),
        };
        var service = new RoundCloseService(_roundRepository, fakeScoreLockingService);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await service.CloseRoundAsync(round.Id, now));

        fakeScoreLockingService.ThrowOnLock = null;
        var closed = await service.CloseRoundAsync(round.Id, now);

        Assert.That(closed!.ClosedAt, Is.EqualTo(now));
        var persisted = await _roundRepository.GetByIdAsync(round.Id);
        Assert.That(persisted!.ClosedAt, Is.EqualTo(now));
    }
}

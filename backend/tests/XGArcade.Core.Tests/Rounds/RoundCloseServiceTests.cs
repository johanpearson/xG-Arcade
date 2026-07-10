using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Rounds;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Tests.Rounds;

// REQ-205/REQ-806 (docs/requirements-document.md §4.3/§4.9): S-008's Tier 0
// scope is only the "close" half — making a round's EndTime reflect
// immediate closure. Locking Guess.final_uniqueness_score/final_points is
// S-011's job, once Guess/Core.Scoring exist (docs/backlog.md).
public class RoundCloseServiceTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IRoundRepository _roundRepository = null!;
    private RoundCloseService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _roundRepository = new RoundRepository(_dbContext);
        _service = new RoundCloseService(_roundRepository);
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
}

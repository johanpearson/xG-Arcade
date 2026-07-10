using XGArcade.Core.Rounds;
using XGArcade.Data.Entities;

namespace XGArcade.Core.Tests.Rounds;

// REQ-302 (docs/requirements-document.md §4.3): a Round's status is always
// calculated live from its start/end time.
public class RoundStatusExtensionsTests
{
    private static Round BuildRound(DateTime startTime, DateTime endTime) =>
        new()
        {
            Id = Guid.NewGuid(),
            GameKey = "xg-grid",
            GameInstanceId = Guid.NewGuid(),
            StartTime = startTime,
            EndTime = endTime,
            AllowGuessChange = true,
        };

    [Test]
    public void REQ302_GetStatus_ReturnsUpcoming_BeforeStartTime()
    {
        var round = BuildRound(startTime: new DateTime(2026, 7, 15), endTime: new DateTime(2026, 7, 18));

        var status = round.GetStatus(now: new DateTime(2026, 7, 14));

        Assert.That(status, Is.EqualTo(RoundStatus.Upcoming));
    }

    [Test]
    public void REQ302_GetStatus_ReturnsActive_BetweenStartAndEndTimeInclusive()
    {
        var round = BuildRound(startTime: new DateTime(2026, 7, 15), endTime: new DateTime(2026, 7, 18));

        Assert.That(round.GetStatus(now: round.StartTime), Is.EqualTo(RoundStatus.Active), "exactly at StartTime");
        Assert.That(round.GetStatus(now: new DateTime(2026, 7, 16)), Is.EqualTo(RoundStatus.Active), "strictly between");
        Assert.That(round.GetStatus(now: round.EndTime), Is.EqualTo(RoundStatus.Active), "exactly at EndTime");
    }

    [Test]
    public void REQ302_GetStatus_ReturnsClosed_AfterEndTime()
    {
        var round = BuildRound(startTime: new DateTime(2026, 7, 15), endTime: new DateTime(2026, 7, 18));

        var status = round.GetStatus(now: new DateTime(2026, 7, 19));

        Assert.That(status, Is.EqualTo(RoundStatus.Closed));
    }
}

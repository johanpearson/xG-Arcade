namespace XGArcade.DataSync.Tests;

// REQ-1301: minimal hand-rolled TimeProvider fake so
// FootballDataClient.GetUpcomingGameweekFixturesAsync's "is this matchday
// actually upcoming" check is deterministic rather than tied to real
// wall-clock time — same style/shape as
// XGArcade.Games.XGPredict.Tests.ManualTimeProvider (duplicated here rather
// than shared since each test project owns its own fakes, this codebase's
// no-mocking-framework convention).
internal class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

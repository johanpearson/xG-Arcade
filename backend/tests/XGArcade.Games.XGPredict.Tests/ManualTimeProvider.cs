namespace XGArcade.Games.XGPredict.Tests;

// REQ-1303: minimal hand-rolled TimeProvider fake so ScoreSubmissionAsync's
// round-lock check (`now >= lockInstant`) is deterministic rather than tied
// to real wall-clock time — same style/shape as
// XGArcade.Games.XGPath.Tests.ManualTimeProvider (duplicated here rather
// than shared since each test project owns its own fakes, this codebase's
// no-mocking-framework convention).
internal class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

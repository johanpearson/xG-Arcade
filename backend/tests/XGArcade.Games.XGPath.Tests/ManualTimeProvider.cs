namespace XGArcade.Games.XGPath.Tests;

// REQ-1208/ADR-0058: minimal hand-rolled TimeProvider fake so
// XGPathGameModule's LastCycleCompletedAt stamp (written on a cycle
// rollover) is deterministic rather than tied to real wall-clock time — same
// style/shape as XGArcade.Games.XGGrid.Tests.ManualTimeProvider (this
// project's sibling test project), duplicated here rather than shared since
// each test project owns its own fakes (this codebase's no-mocking-framework
// convention). Time here only advances when a test explicitly calls Advance;
// unlike XGArcade.Core.Tests.Rounds.FixedTimeProvider, no test in this file
// currently needs to advance it mid-test, but the shape is kept identical to
// the Grid precedent for consistency and so a later test that DOES need to
// advance time doesn't need a different fake.
internal class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

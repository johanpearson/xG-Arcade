namespace XGArcade.Games.XGGrid.Tests;

// ADR-0023: minimal hand-rolled TimeProvider fake (same style as
// XGArcade.Core.Tests.Rounds.FixedTimeProvider) so PickHeadersAsync's
// MaxDuration deadline-abort branch can be exercised deterministically —
// unlike FixedTimeProvider, time here only moves when a test explicitly
// calls Advance, e.g. from a FakeWikidataLookupService onCalled hook
// simulating a live lookup's real-world latency.
internal class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

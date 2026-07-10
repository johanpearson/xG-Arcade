namespace XGArcade.Core.Tests.Rounds;

// Minimal hand-rolled TimeProvider fake so REQ-301/REQ-205's boundary
// conditions (exactly at StartTime/EndTime) are deterministic, not
// tolerant-of-a-few-milliseconds-of-real-wall-clock-drift.
internal class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

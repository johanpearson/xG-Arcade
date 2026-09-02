namespace XGArcade.Api.Tests;

// Minimal hand-rolled TimeProvider fake, mirroring
// XGArcade.Core.Tests.Rounds.FixedTimeProvider's exact shape — duplicated
// here rather than shared across the two test projects/assemblies (neither
// references the other, and there's no shared test-infrastructure project
// in this repo yet; flag to quality-architect if a third copy ever shows
// up, per CLAUDE.md's rule-of-three guidance). Used by
// MatchmakingSweepServiceTests (REQ-1403) to make the 12-hour pairing
// window's boundary deterministic across two sweep calls at different
// simulated times, not tolerant-of-real-wall-clock-drift.
internal class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

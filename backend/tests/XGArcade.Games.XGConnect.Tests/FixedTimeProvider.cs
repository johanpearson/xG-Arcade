namespace XGArcade.Games.XGConnect.Tests;

// Minimal hand-rolled TimeProvider fake, mirroring
// XGArcade.Core.Tests.Rounds.FixedTimeProvider's / XGArcade.Api.Tests's own
// exact shape — duplicated here rather than shared across test
// projects/assemblies (neither references the other, and there's no shared
// test-infrastructure project in this repo yet). This is the THIRD copy of
// this exact class (Core.Tests, Api.Tests, now Games.XGConnect.Tests) —
// flagging to quality-architect per the rule-of-three guidance those other
// two copies' own doc comments call for, rather than silently adding a
// fourth-in-waiting. Used by ConnectTargetPickServiceTests (REQ-1404) so
// SubmitTargetPickAsync's persisted SelectedAt timestamps are deterministic
// and distinguishable across two submissions at different simulated times,
// not tolerant-of-real-wall-clock-drift.
internal class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

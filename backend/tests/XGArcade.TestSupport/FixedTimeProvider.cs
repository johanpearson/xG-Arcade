namespace XGArcade.TestSupport;

// Minimal hand-rolled TimeProvider fake for deterministic clock control in
// tests. Promoted here (S-211/REQ-1404) once a third verbatim copy
// (XGArcade.Core.Tests.Rounds, XGArcade.Api.Tests, then
// XGArcade.Games.XGConnect.Tests) was about to land — the same
// rule-of-three trigger docs/coding-guidelines.md's code health budget
// section describes, and the same shared-home XGArcade.TestSupport was
// created for when FakeHttpMessageHandler hit the same situation. No
// mocking framework (docs/coding-guidelines.md); this project is a plain
// class library referenced by test projects' .csproj files, not an NUnit
// test project itself.
public class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

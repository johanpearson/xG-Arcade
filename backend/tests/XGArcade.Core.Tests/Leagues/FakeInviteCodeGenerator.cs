using XGArcade.Core.Leagues;

namespace XGArcade.Core.Tests.Leagues;

// Hand-rolled Fake (no mocking framework, per coding-guidelines.md) —
// returns a fixed queue of codes in the order given, so a test can force
// LeagueService.CreateCustomLeagueAsync's collision-retry loop down a
// specific path, which a real random generator (InviteCodeGenerator) can't
// be made to do on demand.
internal class FakeInviteCodeGenerator(params string[] codes) : IInviteCodeGenerator
{
    private int _index;

    public string Generate()
    {
        if (_index >= codes.Length)
            throw new InvalidOperationException("FakeInviteCodeGenerator ran out of configured codes.");

        return codes[_index++];
    }
}

using XGArcade.Core.Games;
using XGArcade.Core.Tests.Rounds;

namespace XGArcade.Core.Tests.Games;

// Backs REQ-301: Core.Rounds resolves a Round's GameKey to exactly one
// IGameModule implementation (IGameModule's own doc comment, ADR-0003).
public class GameModuleResolverTests
{
    [Test]
    public void Resolve_ReturnsTheModule_ForItsOwnGameKey()
    {
        var gridModule = new FakeGameModule("xg-grid");
        var resolver = new GameModuleResolver([gridModule]);

        var resolved = resolver.Resolve("xg-grid");

        Assert.That(resolved, Is.SameAs(gridModule));
    }

    [Test]
    public void Resolve_PicksTheMatchingModule_AmongSeveralRegistered()
    {
        var gridModule = new FakeGameModule("xg-grid");
        var otherModule = new FakeGameModule("some-other-game");
        var resolver = new GameModuleResolver([otherModule, gridModule]);

        var resolved = resolver.Resolve("xg-grid");

        Assert.That(resolved, Is.SameAs(gridModule));
    }

    [Test]
    public void Resolve_ThrowsInvalidOperationException_ForUnregisteredGameKey()
    {
        var resolver = new GameModuleResolver([new FakeGameModule("xg-grid")]);

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve("unknown-game"));

        Assert.That(ex!.Message, Does.Contain("unknown-game"));
    }
}

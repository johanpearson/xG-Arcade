using XGArcade.Core.Scoring;

namespace XGArcade.Core.Tests.Scoring;

// ADR-0040: Core.Scoring resolves one IScoringStrategy per Round.GameKey,
// the same resolution shape IGameModuleResolver already establishes for
// game logic — see GameModuleResolverTests for the mirrored coverage.
public class ScoringStrategyResolverTests
{
    [Test]
    public void Resolve_ReturnsTheRegisteredUniquenessScoringStrategy_ForXgGrid()
    {
        var strategy = new UniquenessScoringStrategy { GameKey = "xg-grid" };
        var resolver = new ScoringStrategyResolver([strategy]);

        var resolved = resolver.Resolve("xg-grid");

        Assert.That(resolved, Is.SameAs(strategy));
        Assert.That(resolved, Is.InstanceOf<UniquenessScoringStrategy>());
    }

    [Test]
    public void Resolve_PicksTheMatchingStrategy_AmongSeveralRegistered()
    {
        var gridStrategy = new FakeScoringStrategy("xg-grid");
        var otherStrategy = new FakeScoringStrategy("some-other-game");
        var resolver = new ScoringStrategyResolver([otherStrategy, gridStrategy]);

        var resolved = resolver.Resolve("xg-grid");

        Assert.That(resolved, Is.SameAs(gridStrategy));
    }

    [Test]
    public void Resolve_ThrowsInvalidOperationException_ForUnregisteredGameKey()
    {
        var resolver = new ScoringStrategyResolver([new FakeScoringStrategy("xg-grid")]);

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve("unknown-game"));

        Assert.That(ex!.Message, Does.Contain("unknown-game"));
    }
}

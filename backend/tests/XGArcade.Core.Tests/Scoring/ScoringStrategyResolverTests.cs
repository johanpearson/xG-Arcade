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

    // REQ-1206/S-083/ADR-0040: proves the resolver picks
    // ClueEfficiencyScoringStrategy (not UniquenessScoringStrategy) for
    // "xg-path" — mirrors Resolve_ReturnsTheRegisteredUniquenessScoringStrategy_ForXgGrid
    // above, but with both real strategies registered together, so this
    // doesn't just prove "the only registered strategy comes back" but
    // that GameKey matching actually discriminates between the two.
    [Test]
    public void REQ1206_Resolve_ReturnsTheRegisteredClueEfficiencyScoringStrategy_ForXgPath()
    {
        var gridStrategy = new UniquenessScoringStrategy { GameKey = "xg-grid" };
        var pathStrategy = new ClueEfficiencyScoringStrategy { GameKey = "xg-path" };
        var resolver = new ScoringStrategyResolver([gridStrategy, pathStrategy]);

        var resolved = resolver.Resolve("xg-path");

        Assert.That(resolved, Is.SameAs(pathStrategy));
        Assert.That(resolved, Is.InstanceOf<ClueEfficiencyScoringStrategy>());
        Assert.That(resolved, Is.Not.InstanceOf<UniquenessScoringStrategy>());
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

using XGArcade.Core.Rounds;

namespace XGArcade.Core.Tests.Rounds;

// S-084/REQ-1202: RoundSchedulingOptionsResolver mirrors ScoringStrategyResolver's
// exact resolution shape (see Scoring/ScoringStrategyResolverTests.cs) for
// RoundSchedulingOptions keyed by Round.GameKey instead of scoring strategies —
// this is what lets a second GameKey (xg-path) carry its own RoundDuration,
// resolved independently of xg-grid's, instead of a single directly-injected
// singleton that could only ever serve one GameKey.
public class RoundSchedulingOptionsResolverTests
{
    [Test]
    public void Resolve_ReturnsTheRegisteredOptions_ForAKnownGameKey()
    {
        var options = new RoundSchedulingOptions { GameKey = "xg-grid", RoundDuration = TimeSpan.FromDays(3) };
        var resolver = new RoundSchedulingOptionsResolver([options]);

        var resolved = resolver.Resolve("xg-grid");

        Assert.That(resolved, Is.SameAs(options));
    }

    // REQ-1202's crux: two GameKeys, each with a genuinely distinct
    // RoundDuration, registered together — proves GameKey matching actually
    // discriminates between the two rather than "the only registered options
    // happen to come back" (which a single-registration test can't rule out).
    [Test]
    public void REQ1202_Resolve_ResolvesEachGameKeysOwnRoundDuration_IndependentlyOfTheOther()
    {
        var gridOptions = new RoundSchedulingOptions { GameKey = "xg-grid", RoundDuration = TimeSpan.FromHours(48) };
        var pathOptions = new RoundSchedulingOptions { GameKey = "xg-path", RoundDuration = TimeSpan.FromHours(30) };
        var resolver = new RoundSchedulingOptionsResolver([gridOptions, pathOptions]);

        var resolvedGrid = resolver.Resolve("xg-grid");
        var resolvedPath = resolver.Resolve("xg-path");

        Assert.That(resolvedGrid, Is.SameAs(gridOptions));
        Assert.That(resolvedGrid.RoundDuration, Is.EqualTo(TimeSpan.FromHours(48)));
        Assert.That(resolvedPath, Is.SameAs(pathOptions));
        Assert.That(resolvedPath.RoundDuration, Is.EqualTo(TimeSpan.FromHours(30)));
        Assert.That(resolvedPath, Is.Not.SameAs(resolvedGrid));
    }

    // This story (wiring "xg-predict" into round scheduling, ADR-0051's
    // 2026-08-30 amendment): a third GameKey, each with its own distinct
    // RoundDuration, registered alongside the two above — proves GameKey
    // matching still discriminates correctly once a third registration
    // exists, same shape as REQ1202_Resolve_ResolvesEachGameKeysOwnRoundDuration_IndependentlyOfTheOther
    // above.
    //
    // ADR-0102 (S-204): "xg-predict"'s RoundDuration is now a dead fallback
    // for actual round-generation timing — XGPredictGameModule always
    // supplies its own SuggestedStartTime/SuggestedEndTime, which
    // RoundGenerationService prefers over chain-math unconditionally. This
    // test is unaffected and still asserts something real: the resolver
    // itself must still resolve "xg-predict" correctly (registration
    // completeness/type-safety), regardless of whether RoundGenerationService
    // ends up reading the resolved value for that GameKey.
    [Test]
    public void REQ1301_Resolve_ResolvesEachOfThreeGameKeysOwnRoundDuration_IndependentlyOfTheOthers()
    {
        var gridOptions = new RoundSchedulingOptions { GameKey = "xg-grid", RoundDuration = TimeSpan.FromHours(48) };
        var pathOptions = new RoundSchedulingOptions { GameKey = "xg-path", RoundDuration = TimeSpan.FromHours(30) };
        var predictOptions = new RoundSchedulingOptions { GameKey = "xg-predict", RoundDuration = TimeSpan.FromHours(48) };
        var resolver = new RoundSchedulingOptionsResolver([gridOptions, pathOptions, predictOptions]);

        var resolvedGrid = resolver.Resolve("xg-grid");
        var resolvedPath = resolver.Resolve("xg-path");
        var resolvedPredict = resolver.Resolve("xg-predict");

        Assert.That(resolvedGrid, Is.SameAs(gridOptions));
        Assert.That(resolvedPath, Is.SameAs(pathOptions));
        Assert.That(resolvedPredict, Is.SameAs(predictOptions));
        Assert.That(resolvedPredict.RoundDuration, Is.EqualTo(TimeSpan.FromHours(48)));
        Assert.That(resolvedPredict, Is.Not.SameAs(resolvedGrid));
        Assert.That(resolvedPredict, Is.Not.SameAs(resolvedPath));
    }

    // This story (wiring "xg-higher-lower" into round scheduling, REQ-1505/
    // S-226): a fourth GameKey, each with its own distinct RoundDuration,
    // registered alongside the three above — proves GameKey matching still
    // discriminates correctly once a fourth registration exists, same
    // shape as REQ1301_Resolve_ResolvesEachOfThreeGameKeysOwnRoundDuration_IndependentlyOfTheOthers
    // above. Unlike "xg-predict"'s own RoundDuration (ADR-0102's dead
    // fallback), "xg-higher-lower"'s RoundDuration IS the real round-timing
    // path (chain-math, same as xg-grid/xg-path) — this test doesn't need
    // to know that either way; it only proves registration/resolution
    // completeness, same as the other three GameKeys' own tests above.
    [Test]
    public void REQ1505_Resolve_ResolvesEachOfFourGameKeysOwnRoundDuration_IndependentlyOfTheOthers()
    {
        var gridOptions = new RoundSchedulingOptions { GameKey = "xg-grid", RoundDuration = TimeSpan.FromHours(48) };
        var pathOptions = new RoundSchedulingOptions { GameKey = "xg-path", RoundDuration = TimeSpan.FromHours(30) };
        var predictOptions = new RoundSchedulingOptions { GameKey = "xg-predict", RoundDuration = TimeSpan.FromHours(48) };
        var higherLowerOptions = new RoundSchedulingOptions { GameKey = "xg-higher-lower", RoundDuration = TimeSpan.FromHours(36) };
        var resolver = new RoundSchedulingOptionsResolver([gridOptions, pathOptions, predictOptions, higherLowerOptions]);

        var resolvedGrid = resolver.Resolve("xg-grid");
        var resolvedPath = resolver.Resolve("xg-path");
        var resolvedPredict = resolver.Resolve("xg-predict");
        var resolvedHigherLower = resolver.Resolve("xg-higher-lower");

        Assert.That(resolvedGrid, Is.SameAs(gridOptions));
        Assert.That(resolvedPath, Is.SameAs(pathOptions));
        Assert.That(resolvedPredict, Is.SameAs(predictOptions));
        Assert.That(resolvedHigherLower, Is.SameAs(higherLowerOptions));
        Assert.That(resolvedHigherLower.RoundDuration, Is.EqualTo(TimeSpan.FromHours(36)));
        Assert.That(resolvedHigherLower, Is.Not.SameAs(resolvedGrid));
        Assert.That(resolvedHigherLower, Is.Not.SameAs(resolvedPath));
        Assert.That(resolvedHigherLower, Is.Not.SameAs(resolvedPredict));
    }

    [Test]
    public void Resolve_PicksTheMatchingOptions_AmongSeveralRegistered()
    {
        var gridOptions = new RoundSchedulingOptions { GameKey = "xg-grid", RoundDuration = TimeSpan.FromDays(2) };
        var otherOptions = new RoundSchedulingOptions { GameKey = "some-other-game", RoundDuration = TimeSpan.FromDays(9) };
        var resolver = new RoundSchedulingOptionsResolver([otherOptions, gridOptions]);

        var resolved = resolver.Resolve("xg-grid");

        Assert.That(resolved, Is.SameAs(gridOptions));
    }

    [Test]
    public void Resolve_ThrowsInvalidOperationException_ForUnregisteredGameKey()
    {
        var resolver = new RoundSchedulingOptionsResolver([new RoundSchedulingOptions { GameKey = "xg-grid", RoundDuration = TimeSpan.FromDays(3) }]);

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve("unknown-game"));

        Assert.That(ex!.Message, Does.Contain("unknown-game"));
    }
}

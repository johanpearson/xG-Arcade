using XGArcade.Core.Games;

namespace XGArcade.Games.XGHigherLower.Tests;

// COMP-18/ADR-0110: structural scaffold only — see XGHigherLowerGameModule's
// own doc comment and docs/requirements-document.md §4.16 (REQ-1501-1505)
// for what remains to be implemented. These tests lock in the current,
// deliberately explicit "not yet implemented" boundary (NotImplementedException
// for the Round-generation-shaped methods this game WILL eventually
// implement, since it fits the Round model per ADR-0110) versus the
// permanently-inapplicable methods (NotSupportedException/null, mirroring
// XGPathGameModule's/XGPredictGameModule's own established precedent) — so
// a future story doesn't accidentally silently return fake data instead of
// real REQ-1501-1505 behavior, and so PurgeUserDataAsync's no-op stays a
// deliberate, tested choice (it must never throw — see its own doc comment
// for why: AccountDeletionService calls it for every registered IGameModule
// on every deleted user).
public class XGHigherLowerGameModuleTests
{
    private XGHigherLowerGameModule _module = null!;

    [SetUp]
    public void SetUp()
    {
        _module = new XGHigherLowerGameModule();
    }

    [Test]
    public void GameKey_ReturnsXgHigherLower()
    {
        Assert.That(_module.GameKey, Is.EqualTo("xg-higher-lower"));
        Assert.That(_module.GameKey, Is.EqualTo(XGHigherLowerGameModule.XGHigherLowerGameKey));
    }

    [Test]
    public void GenerateInstanceAsync_ThrowsNotImplementedException()
    {
        Assert.ThrowsAsync<NotImplementedException>(
            () => _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));
    }

    [Test]
    public void ScoreSubmissionAsync_ThrowsNotImplementedException()
    {
        Assert.ThrowsAsync<NotImplementedException>(
            () => _module.ScoreSubmissionAsync(Guid.NewGuid(), Guid.NewGuid(), new object()));
    }

    [Test]
    public void GetCellIdsAsync_ThrowsNotImplementedException()
    {
        Assert.ThrowsAsync<NotImplementedException>(
            () => _module.GetCellIdsAsync(Guid.NewGuid()));
    }

    [Test]
    public void GetMaxAttemptsForCellAsync_ThrowsNotImplementedException()
    {
        Assert.ThrowsAsync<NotImplementedException>(
            () => _module.GetMaxAttemptsForCellAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Test]
    public void GetCellCategoryTypesAsync_ThrowsNotSupportedException()
    {
        Assert.ThrowsAsync<NotSupportedException>(
            () => _module.GetCellCategoryTypesAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Test]
    public async Task ResolveWrongGuessPlayerAsync_ReturnsNull()
    {
        var result = await _module.ResolveWrongGuessPlayerAsync(Guid.NewGuid(), "Some Player");

        Assert.That(result, Is.Null);
    }

    // ---- REQ-710: PurgeUserDataAsync -----------------------------------

    [Test]
    public void REQ710_PurgeUserDataAsync_CompletesWithoutThrowing()
    {
        // No per-user data model exists for this game yet — a genuine no-op
        // (see the method's own doc comment for why this must never throw,
        // even though every other round-generation-shaped method above
        // deliberately still does).
        Assert.DoesNotThrowAsync(() => _module.PurgeUserDataAsync(Guid.NewGuid()));
    }
}

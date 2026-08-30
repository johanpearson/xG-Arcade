using XGArcade.Core.Games;
using XGArcade.Games.XGPredict;

namespace XGArcade.Games.XGPredict.Tests;

// COMP-15 scaffold-only tests: confirms the IGameModule boundary compiles
// and behaves the way a not-yet-built module should — GameKey resolves,
// the two REQ-1301/1302-owned methods loudly refuse to fabricate a real
// result, and the two permanently-not-applicable-to-this-game methods
// (REQ-215/REQ-216) behave the same way XGPathGameModule's equivalent
// methods already do. Real gameplay tests (round generation, prediction
// submission/scoring) belong to the follow-up backend stories that
// implement REQ-1301-1305, not this scaffolding session.
public class XGPredictGameModuleTests
{
    private readonly XGPredictGameModule _module = new();

    [Test]
    public void GameKey_ReturnsXgPredict()
    {
        Assert.That(_module.GameKey, Is.EqualTo("xg-predict"));
        Assert.That(_module.GameKey, Is.EqualTo(XGPredictGameModule.XGPredictGameKey));
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
    public void REQ215_GetCellCategoryTypesAsync_ThrowsNotSupportedException_XGPredictHasNoCategoryConcept()
    {
        Assert.ThrowsAsync<NotSupportedException>(
            () => _module.GetCellCategoryTypesAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Test]
    public async Task REQ216_ResolveWrongGuessPlayerAsync_ReturnsNull_XGPredictIsOutOfScope()
    {
        var result = await _module.ResolveWrongGuessPlayerAsync(Guid.NewGuid(), "any name");

        Assert.That(result, Is.Null);
    }
}

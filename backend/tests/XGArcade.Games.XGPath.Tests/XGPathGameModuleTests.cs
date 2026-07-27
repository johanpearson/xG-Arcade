using XGArcade.Core.Games;

namespace XGArcade.Games.XGPath.Tests;

// S-080 scaffold verification only — no REQ acceptance criteria are being
// satisfied yet, so these are plainly named rather than REQ-prefixed. They
// exist to prove the module boundary compiles and is discoverable, and that
// every stub fails loudly (NotImplementedException) instead of quietly
// returning fake data. Real behavior tests land alongside S-081+.
public class XGPathGameModuleTests
{
    private readonly XGPathGameModule _module = new();

    [Test]
    public void GameKey_IsXgPath()
    {
        Assert.That(_module.GameKey, Is.EqualTo("xg-path"));
        Assert.That(XGPathGameModule.XGPathGameKey, Is.EqualTo("xg-path"));
    }

    [Test]
    public void GenerateInstanceAsync_ThrowsNotImplemented()
    {
        var config = new RoundConfig { TemplateId = Guid.NewGuid() };

        Assert.ThrowsAsync<NotImplementedException>(async () => await _module.GenerateInstanceAsync(config));
    }

    [Test]
    public void ScoreSubmissionAsync_ThrowsNotImplemented()
    {
        Assert.ThrowsAsync<NotImplementedException>(
            async () => await _module.ScoreSubmissionAsync(Guid.NewGuid(), Guid.NewGuid(), new object()));
    }

    [Test]
    public void GetCellIdsAsync_ThrowsNotImplemented()
    {
        Assert.ThrowsAsync<NotImplementedException>(async () => await _module.GetCellIdsAsync(Guid.NewGuid()));
    }

    [Test]
    public void GetMaxAttemptsForCellAsync_ThrowsNotImplemented()
    {
        Assert.ThrowsAsync<NotImplementedException>(
            async () => await _module.GetMaxAttemptsForCellAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}

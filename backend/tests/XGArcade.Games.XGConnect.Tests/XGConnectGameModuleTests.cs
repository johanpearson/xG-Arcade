using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect.Tests;

// COMP-17/ADR-0103: S-211 scaffold only. GenerateInstanceAsync/
// ScoreSubmissionAsync/GetCellIdsAsync/GetMaxAttemptsForCellAsync/
// GetCellCategoryTypesAsync are permanently inapplicable to xG Connect's
// non-Round shape (not "not yet built") — the NotSupportedException tests
// below lock in that precedent so a future story doesn't accidentally widen
// this module back into the round-generation-shaped IGameModule slice
// ADR-0103 says it must not implement. Target-pick selection (REQ-1404),
// chain-step submission (REQ-1406/1407), and scoring/resolution
// (REQ-1408/1409) are S-211 onward's own service logic, not tested here.
public class XGConnectGameModuleTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IConnectMatchRepository _repository = null!;
    private XGConnectGameModule _module = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new ConnectMatchRepository(_dbContext);
        _module = new XGConnectGameModule(_repository);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public void GameKey_ReturnsXgConnect()
    {
        Assert.That(_module.GameKey, Is.EqualTo("xg-connect"));
        Assert.That(_module.GameKey, Is.EqualTo(XGConnectGameModule.XGConnectGameKey));
    }

    [Test]
    public void GenerateInstanceAsync_ThrowsNotSupportedException()
    {
        Assert.ThrowsAsync<NotSupportedException>(
            () => _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));
    }

    [Test]
    public void ScoreSubmissionAsync_ThrowsNotSupportedException()
    {
        Assert.ThrowsAsync<NotSupportedException>(
            () => _module.ScoreSubmissionAsync(Guid.NewGuid(), Guid.NewGuid(), new object()));
    }

    [Test]
    public void GetCellIdsAsync_ThrowsNotSupportedException()
    {
        Assert.ThrowsAsync<NotSupportedException>(
            () => _module.GetCellIdsAsync(Guid.NewGuid()));
    }

    [Test]
    public void GetMaxAttemptsForCellAsync_ThrowsNotSupportedException()
    {
        Assert.ThrowsAsync<NotSupportedException>(
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
    public async Task REQ710_PurgeUserDataAsync_AnonymizesMatchTargetPickAndChainStepRows()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var match = new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = userId,
            PlayerBUserId = otherUserId,
            CreatedAt = DateTime.UtcNow,
        };
        await _repository.AddMatchAsync(match);

        await _repository.AddOrUpdateTargetPickAsync(match.Id, userId, Guid.NewGuid(), DateTime.UtcNow);

        await _repository.AddChainStepAsync(new ConnectChainStep
        {
            Id = Guid.NewGuid(),
            ConnectMatchId = match.Id,
            UserId = userId,
            Position = 1,
            AttemptNumber = 1,
            CandidatePlayerId = Guid.NewGuid(),
            ClaimedClubName = "Some Club",
            IsValid = true,
            SubmittedAt = DateTime.UtcNow,
        });

        await _module.PurgeUserDataAsync(userId);

        var reloadedMatch = await _repository.GetMatchByIdAsync(match.Id);
        Assert.That(reloadedMatch, Is.Not.Null);
        Assert.That(reloadedMatch!.PlayerAUserId, Is.Null);
        // The other participant's UserId is untouched — purge is scoped to
        // exactly the deleted user.
        Assert.That(reloadedMatch.PlayerBUserId, Is.EqualTo(otherUserId));

        var reloadedPick = await _repository.GetTargetPickAsync(match.Id, null);
        Assert.That(reloadedPick, Is.Not.Null);
        Assert.That(reloadedPick!.UserId, Is.Null);

        var reloadedSteps = await _repository.GetChainStepsForMatchAndUserAsync(match.Id, null);
        Assert.That(reloadedSteps, Has.Count.EqualTo(1));
        Assert.That(reloadedSteps[0].UserId, Is.Null);
    }
}

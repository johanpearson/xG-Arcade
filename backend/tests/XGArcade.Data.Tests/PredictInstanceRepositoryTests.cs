using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// COMP-15 (Games.XGPredict)/ADR-0096: PredictInstanceRepository's own
// persistence round-trip (AddInstanceAsync/GetInstanceByIdAsync,
// GetTemplateByIdAsync) and REQ-1302's store/replace prediction semantics
// (AddOrUpdatePredictionAsync/GetPredictionAsync). Same InMemory-backed
// DbContext pattern as PlayerCareerStintRepositoryTests/
// PlayerAttributeRepositoryTests.
//
// Note (mirrors UserRepositoryTests' own documented caveat): the InMemory
// provider used here does not enforce unique indexes at all — the
// (PredictMatchId, UserId) unique index this table declares
// (XGArcadeDbContext.OnModelCreating) is a real-Postgres-only backstop
// against a concurrent double-insert race. What IS tested here, and what
// actually matters for REQ-1302's "never a second row" requirement in
// practice, is AddOrUpdatePredictionAsync's own application-level
// load-then-save upsert logic — the single-threaded path every real
// resubmission takes.
public class PredictInstanceRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPredictInstanceRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PredictInstanceRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    // ---- GetTemplateByIdAsync ------------------------------------------

    [Test]
    public async Task GetTemplateByIdAsync_ReturnsPersistedTemplate()
    {
        var template = new PredictTemplate { Id = Guid.NewGuid(), MatchCount = 5 };
        _dbContext.PredictTemplates.Add(template);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetTemplateByIdAsync(template.Id);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.MatchCount, Is.EqualTo(5));
    }

    [Test]
    public async Task GetTemplateByIdAsync_UnknownId_ReturnsNull()
    {
        var result = await _repository.GetTemplateByIdAsync(Guid.NewGuid());

        Assert.That(result, Is.Null);
    }

    // ---- AddInstanceAsync / GetInstanceByIdAsync -----------------------

    [Test]
    public async Task AddInstanceAsync_ThenGetInstanceByIdAsync_PersistsInstanceAndMatchesTogether()
    {
        var instanceId = Guid.NewGuid();
        var instance = new PredictInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            Matches =
            [
                new PredictMatch
                {
                    Id = Guid.NewGuid(),
                    PredictInstanceId = instanceId,
                    ExternalFixtureId = 101,
                    HomeTeamName = "Arsenal",
                    AwayTeamName = "Chelsea",
                    KickoffUtc = new DateTime(2026, 9, 5, 15, 0, 0, DateTimeKind.Utc),
                },
                new PredictMatch
                {
                    Id = Guid.NewGuid(),
                    PredictInstanceId = instanceId,
                    ExternalFixtureId = 102,
                    HomeTeamName = "Liverpool",
                    AwayTeamName = "Everton",
                    KickoffUtc = new DateTime(2026, 9, 5, 17, 30, 0, DateTimeKind.Utc),
                },
            ],
        };

        await _repository.AddInstanceAsync(instance);

        var result = await _repository.GetInstanceByIdAsync(instanceId);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.TemplateId, Is.EqualTo(instance.TemplateId));
        Assert.That(result.Matches, Has.Count.EqualTo(2));
        Assert.That(result.Matches.Select(m => m.ExternalFixtureId), Is.EquivalentTo(new[] { 101, 102 }));
        Assert.That(result.Matches.First(m => m.ExternalFixtureId == 101).HomeTeamName, Is.EqualTo("Arsenal"));
    }

    [Test]
    public async Task GetInstanceByIdAsync_UnknownId_ReturnsNull()
    {
        var result = await _repository.GetInstanceByIdAsync(Guid.NewGuid());

        Assert.That(result, Is.Null);
    }

    // ---- GetPredictionAsync / AddOrUpdatePredictionAsync ---------------

    [Test]
    public async Task AddOrUpdatePredictionAsync_NoExistingRow_InsertsNewPrediction()
    {
        var matchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var submittedAt = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        await _repository.AddOrUpdatePredictionAsync(matchId, userId, homeGoals: 2, awayGoals: 1, submittedAt);

        var stored = await _repository.GetPredictionAsync(matchId, userId);
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!.HomeGoals, Is.EqualTo(2));
        Assert.That(stored.AwayGoals, Is.EqualTo(1));
        Assert.That(stored.SubmittedAt, Is.EqualTo(submittedAt));
    }

    [Test]
    public async Task AddOrUpdatePredictionAsync_ExistingRowForSameMatchAndUser_ReplacesValueRatherThanInsertingSecondRow()
    {
        var matchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await _repository.AddOrUpdatePredictionAsync(matchId, userId, 2, 1, new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc));

        await _repository.AddOrUpdatePredictionAsync(matchId, userId, 0, 0, new DateTime(2026, 8, 30, 13, 0, 0, DateTimeKind.Utc));

        var stored = await _repository.GetPredictionAsync(matchId, userId);
        Assert.That(stored!.HomeGoals, Is.EqualTo(0));
        Assert.That(stored.AwayGoals, Is.EqualTo(0));
        Assert.That(await _dbContext.PredictMatchPredictions.CountAsync(p => p.PredictMatchId == matchId && p.UserId == userId),
            Is.EqualTo(1), "a resubmission must overwrite the existing row, never insert a second one (REQ-1302)");
    }

    [Test]
    public async Task AddOrUpdatePredictionAsync_DifferentUsersSameMatch_ProducesSeparateRows()
    {
        var matchId = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await _repository.AddOrUpdatePredictionAsync(matchId, userA, 2, 1, DateTime.UtcNow);
        await _repository.AddOrUpdatePredictionAsync(matchId, userB, 0, 3, DateTime.UtcNow);

        Assert.That((await _repository.GetPredictionAsync(matchId, userA))!.HomeGoals, Is.EqualTo(2));
        Assert.That((await _repository.GetPredictionAsync(matchId, userB))!.HomeGoals, Is.EqualTo(0));
        Assert.That(await _dbContext.PredictMatchPredictions.CountAsync(p => p.PredictMatchId == matchId), Is.EqualTo(2));
    }

    [Test]
    public async Task AddOrUpdatePredictionAsync_SameUserDifferentMatches_ProducesSeparateRows()
    {
        var matchA = Guid.NewGuid();
        var matchB = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _repository.AddOrUpdatePredictionAsync(matchA, userId, 2, 1, DateTime.UtcNow);
        await _repository.AddOrUpdatePredictionAsync(matchB, userId, 0, 3, DateTime.UtcNow);

        Assert.That((await _repository.GetPredictionAsync(matchA, userId))!.HomeGoals, Is.EqualTo(2));
        Assert.That((await _repository.GetPredictionAsync(matchB, userId))!.HomeGoals, Is.EqualTo(0));
        Assert.That(await _dbContext.PredictMatchPredictions.CountAsync(p => p.UserId == userId), Is.EqualTo(2));
    }

    [Test]
    public async Task GetPredictionAsync_NoStoredPrediction_ReturnsNull()
    {
        var result = await _repository.GetPredictionAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.That(result, Is.Null);
    }
}

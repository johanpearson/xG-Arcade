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

    // ---- REQ-1305/ADR-0097: GetMatchesReadyForGradingAsync -------------

    [Test]
    public async Task REQ1305_GetMatchesReadyForGradingAsync_ReturnsOnlyPendingMatchesWhoseKickoffPlusDurationHasPassed()
    {
        var nowUtc = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var typicalMatchDuration = TimeSpan.FromHours(2);

        // Kickoff 3h ago + 2h duration = 1h ago: ready.
        var readyMatch = await SeedMatchAsync(kickoffUtc: nowUtc.AddHours(-3));
        // Kickoff 1h ago + 2h duration = 1h in the future: not ready yet.
        await SeedMatchAsync(kickoffUtc: nowUtc.AddHours(-1));
        // Exactly at the boundary (kickoff + duration == now): ready
        // (<=, not <).
        var boundaryMatch = await SeedMatchAsync(kickoffUtc: nowUtc.AddHours(-2));

        var result = await _repository.GetMatchesReadyForGradingAsync(typicalMatchDuration, nowUtc);

        Assert.That(result.Select(m => m.Id), Is.EquivalentTo(new[] { readyMatch.Id, boundaryMatch.Id }));
    }

    [Test]
    public async Task REQ1305_GetMatchesReadyForGradingAsync_ExcludesAlreadyGradedAndVoidedMatches()
    {
        var nowUtc = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var typicalMatchDuration = TimeSpan.FromHours(2);
        var kickoffUtc = nowUtc.AddHours(-3);

        var pendingMatch = await SeedMatchAsync(kickoffUtc);
        var gradedMatch = await SeedMatchAsync(kickoffUtc);
        await _repository.GradeMatchAsync(gradedMatch.Id, 1, 0, new Dictionary<Guid, int>());
        var voidedMatch = await SeedMatchAsync(kickoffUtc);
        await _repository.VoidMatchAsync(voidedMatch.Id);

        var result = await _repository.GetMatchesReadyForGradingAsync(typicalMatchDuration, nowUtc);

        Assert.That(result.Select(m => m.Id), Is.EquivalentTo(new[] { pendingMatch.Id }),
            "a match already Graded or Voided must never be returned again — this query IS the whole idempotency mechanism (ADR-0097)");
    }

    // ---- REQ-1305/ADR-0097: GradeMatchAsync / VoidMatchAsync -----------

    [Test]
    public async Task REQ1305_GradeMatchAsync_SetsGradingStatusActualScoreAndEveryPredictionsFinalPoints()
    {
        var match = await SeedMatchAsync(kickoffUtc: DateTime.UtcNow.AddHours(-3));
        var predictionA = await AddAndReadPredictionAsync(match.Id, homeGoals: 2, awayGoals: 1);
        var predictionB = await AddAndReadPredictionAsync(match.Id, homeGoals: 0, awayGoals: 0);

        await _repository.GradeMatchAsync(
            match.Id, actualHomeGoals: 2, actualAwayGoals: 1,
            finalPointsByPredictionId: new Dictionary<Guid, int> { [predictionA.Id] = 30, [predictionB.Id] = 10 });

        var storedMatch = await _dbContext.PredictMatches.AsNoTracking().SingleAsync(m => m.Id == match.Id);
        Assert.That(storedMatch.GradingStatus, Is.EqualTo(PredictMatchGradingStatus.Graded));
        Assert.That(storedMatch.ActualHomeGoals, Is.EqualTo(2));
        Assert.That(storedMatch.ActualAwayGoals, Is.EqualTo(1));

        Assert.That((await _repository.GetPredictionAsync(match.Id, predictionA.UserId))!.FinalPoints, Is.EqualTo(30));
        Assert.That((await _repository.GetPredictionAsync(match.Id, predictionB.UserId))!.FinalPoints, Is.EqualTo(10));
    }

    [Test]
    public async Task REQ1305_VoidMatchAsync_SetsGradingStatusOnly_NeverTouchesActualScoreOrPredictions()
    {
        var match = await SeedMatchAsync(kickoffUtc: DateTime.UtcNow.AddHours(-3));
        var prediction = await AddAndReadPredictionAsync(match.Id, homeGoals: 2, awayGoals: 1);

        await _repository.VoidMatchAsync(match.Id);

        var storedMatch = await _dbContext.PredictMatches.AsNoTracking().SingleAsync(m => m.Id == match.Id);
        Assert.That(storedMatch.GradingStatus, Is.EqualTo(PredictMatchGradingStatus.Voided));
        Assert.That(storedMatch.ActualHomeGoals, Is.Null);
        Assert.That(storedMatch.ActualAwayGoals, Is.Null);

        var storedPrediction = await _repository.GetPredictionAsync(match.Id, prediction.UserId);
        Assert.That(storedPrediction!.FinalPoints, Is.Null);
    }

    // ---- REQ-1305/ADR-0097: GetTotalPointsByInstanceIdAsync ------------

    [Test]
    public async Task REQ1305_GetTotalPointsByInstanceIdAsync_SumsOnlyGradedMatches_GrowsAsFurtherMatchesAreGraded()
    {
        var instanceId = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var matchOne = new PredictMatch
        {
            Id = Guid.NewGuid(), PredictInstanceId = instanceId, ExternalFixtureId = 1,
            HomeTeamName = "A", AwayTeamName = "B", KickoffUtc = DateTime.UtcNow.AddHours(-5),
        };
        var matchTwo = new PredictMatch
        {
            Id = Guid.NewGuid(), PredictInstanceId = instanceId, ExternalFixtureId = 2,
            HomeTeamName = "C", AwayTeamName = "D", KickoffUtc = DateTime.UtcNow.AddHours(-4),
        };
        await _repository.AddInstanceAsync(new PredictInstance { Id = instanceId, TemplateId = Guid.NewGuid(), Matches = [matchOne, matchTwo] });

        await _repository.AddOrUpdatePredictionAsync(matchOne.Id, userA, 2, 1, DateTime.UtcNow);
        await _repository.AddOrUpdatePredictionAsync(matchOne.Id, userB, 0, 0, DateTime.UtcNow);
        await _repository.AddOrUpdatePredictionAsync(matchTwo.Id, userA, 1, 1, DateTime.UtcNow);

        // Before either match is graded: no totals at all.
        var beforeGrading = await _repository.GetTotalPointsByInstanceIdAsync(instanceId);
        Assert.That(beforeGrading, Is.Empty, "an ungraded match must contribute no components, not a placeholder worst-case value");

        // Grade only matchOne: userA's total reflects only matchOne.
        var matchOnePredictionA = await _repository.GetPredictionAsync(matchOne.Id, userA);
        var matchOnePredictionB = await _repository.GetPredictionAsync(matchOne.Id, userB);
        await _repository.GradeMatchAsync(
            matchOne.Id, actualHomeGoals: 2, actualAwayGoals: 1,
            finalPointsByPredictionId: new Dictionary<Guid, int> { [matchOnePredictionA!.Id] = 30, [matchOnePredictionB!.Id] = 10 });

        var afterFirstGrade = await _repository.GetTotalPointsByInstanceIdAsync(instanceId);
        Assert.That(afterFirstGrade[userA], Is.EqualTo(30));
        Assert.That(afterFirstGrade[userB], Is.EqualTo(10));

        // Grade matchTwo too: userA's total grows to include it; userB
        // (who never predicted matchTwo) is unaffected.
        var matchTwoPredictionA = await _repository.GetPredictionAsync(matchTwo.Id, userA);
        await _repository.GradeMatchAsync(
            matchTwo.Id, actualHomeGoals: 1, actualAwayGoals: 1,
            finalPointsByPredictionId: new Dictionary<Guid, int> { [matchTwoPredictionA!.Id] = 20 });

        var afterSecondGrade = await _repository.GetTotalPointsByInstanceIdAsync(instanceId);
        Assert.That(afterSecondGrade[userA], Is.EqualTo(50), "a round's total-score contribution must grow as further matches are graded");
        Assert.That(afterSecondGrade[userB], Is.EqualTo(10), "a user with no prediction on the newly-graded match is unaffected");
    }

    [Test]
    public async Task REQ1305_GetTotalPointsByInstanceIdAsync_VoidedMatchContributesNothing()
    {
        var instanceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var match = new PredictMatch
        {
            Id = Guid.NewGuid(), PredictInstanceId = instanceId, ExternalFixtureId = 9,
            HomeTeamName = "A", AwayTeamName = "B", KickoffUtc = DateTime.UtcNow.AddHours(-5),
        };
        await _repository.AddInstanceAsync(new PredictInstance { Id = instanceId, TemplateId = Guid.NewGuid(), Matches = [match] });
        await _repository.AddOrUpdatePredictionAsync(match.Id, userId, 2, 1, DateTime.UtcNow);

        await _repository.VoidMatchAsync(match.Id);

        var totals = await _repository.GetTotalPointsByInstanceIdAsync(instanceId);
        Assert.That(totals, Is.Empty, "a voided match must contribute nothing to any player's round total");
    }

    // ---- helpers (REQ-1305) --------------------------------------------

    private async Task<PredictMatch> SeedMatchAsync(DateTime kickoffUtc)
    {
        var instanceId = Guid.NewGuid();
        var match = new PredictMatch
        {
            Id = Guid.NewGuid(),
            PredictInstanceId = instanceId,
            ExternalFixtureId = Random.Shared.Next(),
            HomeTeamName = "Home",
            AwayTeamName = "Away",
            KickoffUtc = kickoffUtc,
        };
        await _repository.AddInstanceAsync(new PredictInstance { Id = instanceId, TemplateId = Guid.NewGuid(), Matches = [match] });
        return match;
    }

    private async Task<PredictMatchPrediction> AddAndReadPredictionAsync(Guid predictMatchId, int homeGoals, int awayGoals)
    {
        var userId = Guid.NewGuid();
        await _repository.AddOrUpdatePredictionAsync(predictMatchId, userId, homeGoals, awayGoals, DateTime.UtcNow);
        return (await _repository.GetPredictionAsync(predictMatchId, userId))!;
    }
}

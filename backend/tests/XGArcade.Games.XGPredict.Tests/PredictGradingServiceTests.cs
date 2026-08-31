using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Core.Scoring;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.FootballData;

namespace XGArcade.Games.XGPredict.Tests;

// REQ-1305/ADR-0097: PredictGradingService's own grading run — a
// confirmed/Finished match grades every prediction and persists correctly
// (REQ-1304's formula), a NotYetConfirmed match is left Pending and
// retried, a PostponedOrAbandoned match is voided while its round's other
// matches still grade normally, and an already-Graded/Voided match is
// excluded from the next run entirely (idempotency). Same no-mocking-
// framework pattern as XGPredictGameModuleTests: a real, InMemory-backed
// PredictInstanceRepository plus the hand-rolled FakeFootballDataClient.
public class PredictGradingServiceTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPredictInstanceRepository _repository = null!;
    private FakeFootballDataClient _footballDataClient = null!;
    private ManualTimeProvider _timeProvider = null!;
    private PredictGradingService _service = null!;

    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TypicalMatchDuration = TimeSpan.FromHours(2);

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PredictInstanceRepository(_dbContext);
        _footballDataClient = new FakeFootballDataClient();
        _timeProvider = new ManualTimeProvider(Now);
        var scoringStrategy = new XGPredictScoringStrategy { GameKey = XGPredictGameModule.XGPredictGameKey };
        var gradingOptions = new PredictGradingOptions { TypicalMatchDuration = TypicalMatchDuration };
        _service = new PredictGradingService(
            _repository, _footballDataClient, scoringStrategy, gradingOptions, _timeProvider,
            NullLogger<PredictGradingService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    // ---- Finished match: grades every prediction ----------------------

    [Test]
    public async Task REQ1305_GradeReadyMatchesAsync_FinishedMatch_GradesEveryPredictionAndPersistsResult()
    {
        // Kickoff 3h ago + 2h typical duration = 1h ago: ready for grading.
        var match = await SeedReadyMatchAsync(fixtureId: 101, kickoffHoursAgo: 3);
        var exactPrediction = await AddPredictionAsync(match.Id, homeGoals: 2, awayGoals: 1);
        var partialPrediction = await AddPredictionAsync(match.Id, homeGoals: 1, awayGoals: 1);
        _footballDataClient.Results[101] = new FootballDataFixtureResult(101, FootballDataFixtureOutcome.Finished, "FT", 2, 1);

        var result = await _service.GradeReadyMatchesAsync();

        Assert.That(result, Is.EqualTo(new PredictGradingRunResult(Graded: 1, Voided: 0, StillPending: 0, Failed: 0)));

        var storedMatch = await _dbContext.PredictMatches.AsNoTracking().SingleAsync(m => m.Id == match.Id);
        Assert.That(storedMatch.GradingStatus, Is.EqualTo(PredictMatchGradingStatus.Graded));
        Assert.That(storedMatch.ActualHomeGoals, Is.EqualTo(2));
        Assert.That(storedMatch.ActualAwayGoals, Is.EqualTo(1));

        // Predicted 2-1 for an actual 2-1 result: outcome + both goal
        // counts match, all 3 components.
        var storedExact = await _dbContext.PredictMatchPredictions.AsNoTracking().SingleAsync(p => p.Id == exactPrediction.Id);
        Assert.That(storedExact.FinalPoints, Is.EqualTo(3 * ScoringRules.PredictPointsPerComponent));

        // Predicted 1-1 (draw) for an actual 2-1 (home win): outcome
        // mismatch, home-goals mismatch, away-goals match — 1 component.
        var storedPartial = await _dbContext.PredictMatchPredictions.AsNoTracking().SingleAsync(p => p.Id == partialPrediction.Id);
        Assert.That(storedPartial.FinalPoints, Is.EqualTo(1 * ScoringRules.PredictPointsPerComponent));
    }

    [Test]
    public async Task REQ1305_GradeReadyMatchesAsync_MatchWithNoStoredPredictions_GradesWithZeroPointsToDistribute()
    {
        var match = await SeedReadyMatchAsync(fixtureId: 202, kickoffHoursAgo: 3);
        _footballDataClient.Results[202] = new FootballDataFixtureResult(202, FootballDataFixtureOutcome.Finished, "FT", 0, 0);

        var result = await _service.GradeReadyMatchesAsync();

        Assert.That(result.Graded, Is.EqualTo(1));
        var storedMatch = await _dbContext.PredictMatches.AsNoTracking().SingleAsync(m => m.Id == match.Id);
        Assert.That(storedMatch.GradingStatus, Is.EqualTo(PredictMatchGradingStatus.Graded));
    }

    // ---- NotYetConfirmed: left Pending, retried later ------------------

    [Test]
    public async Task REQ1305_GradeReadyMatchesAsync_NotYetConfirmedMatch_LeftPending_NoGradingWriteHappens()
    {
        var match = await SeedReadyMatchAsync(fixtureId: 303, kickoffHoursAgo: 3);
        var prediction = await AddPredictionAsync(match.Id, homeGoals: 2, awayGoals: 1);
        _footballDataClient.Results[303] = new FootballDataFixtureResult(303, FootballDataFixtureOutcome.NotYetConfirmed, "NS", null, null);

        var result = await _service.GradeReadyMatchesAsync();

        Assert.That(result, Is.EqualTo(new PredictGradingRunResult(Graded: 0, Voided: 0, StillPending: 1, Failed: 0)));

        var storedMatch = await _dbContext.PredictMatches.AsNoTracking().SingleAsync(m => m.Id == match.Id);
        Assert.That(storedMatch.GradingStatus, Is.EqualTo(PredictMatchGradingStatus.Pending));
        Assert.That(storedMatch.ActualHomeGoals, Is.Null);
        Assert.That(storedMatch.ActualAwayGoals, Is.Null);

        var storedPrediction = await _dbContext.PredictMatchPredictions.AsNoTracking().SingleAsync(p => p.Id == prediction.Id);
        Assert.That(storedPrediction.FinalPoints, Is.Null, "a not-yet-confirmed match must not be scored with any placeholder or default value");
    }

    // ---- PostponedOrAbandoned: voided, other matches unaffected --------

    [Test]
    public async Task REQ1305_GradeReadyMatchesAsync_PostponedOrAbandonedMatch_VoidedWithNoComponentsComputed_OtherMatchesStillGradeNormally()
    {
        var voidedMatch = await SeedReadyMatchAsync(fixtureId: 404, kickoffHoursAgo: 3);
        var voidedPrediction = await AddPredictionAsync(voidedMatch.Id, homeGoals: 2, awayGoals: 1);
        _footballDataClient.Results[404] = new FootballDataFixtureResult(404, FootballDataFixtureOutcome.PostponedOrAbandoned, "PST", null, null);

        var normalMatch = await SeedReadyMatchAsync(fixtureId: 405, kickoffHoursAgo: 4);
        var normalPrediction = await AddPredictionAsync(normalMatch.Id, homeGoals: 1, awayGoals: 0);
        _footballDataClient.Results[405] = new FootballDataFixtureResult(405, FootballDataFixtureOutcome.Finished, "FT", 1, 0);

        var result = await _service.GradeReadyMatchesAsync();

        Assert.That(result, Is.EqualTo(new PredictGradingRunResult(Graded: 1, Voided: 1, StillPending: 0, Failed: 0)));

        var storedVoidedMatch = await _dbContext.PredictMatches.AsNoTracking().SingleAsync(m => m.Id == voidedMatch.Id);
        Assert.That(storedVoidedMatch.GradingStatus, Is.EqualTo(PredictMatchGradingStatus.Voided));
        Assert.That(storedVoidedMatch.ActualHomeGoals, Is.Null, "a voided match's actual score must never be written — football-data.org's own values are untrustworthy for this outcome");
        Assert.That(storedVoidedMatch.ActualAwayGoals, Is.Null);

        var storedVoidedPrediction = await _dbContext.PredictMatchPredictions.AsNoTracking().SingleAsync(p => p.Id == voidedPrediction.Id);
        Assert.That(storedVoidedPrediction.FinalPoints, Is.Null, "a voided match's predictions must never have components computed");

        var storedNormalMatch = await _dbContext.PredictMatches.AsNoTracking().SingleAsync(m => m.Id == normalMatch.Id);
        Assert.That(storedNormalMatch.GradingStatus, Is.EqualTo(PredictMatchGradingStatus.Graded), "the round's other match must still grade normally and independently");
        var storedNormalPrediction = await _dbContext.PredictMatchPredictions.AsNoTracking().SingleAsync(p => p.Id == normalPrediction.Id);
        Assert.That(storedNormalPrediction.FinalPoints, Is.EqualTo(3 * ScoringRules.PredictPointsPerComponent));
    }

    // ---- Idempotency: Graded/Voided matches excluded from the next run --

    [Test]
    public async Task REQ1305_GradeReadyMatchesAsync_AlreadyGradedMatch_ExcludedFromNextRun_NeverRefetchedOrRescored()
    {
        var match = await SeedReadyMatchAsync(fixtureId: 501, kickoffHoursAgo: 3);
        var prediction = await AddPredictionAsync(match.Id, homeGoals: 2, awayGoals: 1);
        _footballDataClient.Results[501] = new FootballDataFixtureResult(501, FootballDataFixtureOutcome.Finished, "FT", 2, 1);
        var firstRun = await _service.GradeReadyMatchesAsync();
        Assert.That(firstRun.Graded, Is.EqualTo(1));
        Assert.That(_footballDataClient.RequestedFixtureIds, Is.EqualTo(new[] { 501 }));

        // Second run: nothing configured for fixture 501 in Results this
        // time — if the service ever re-requested it, GetFixtureResultAsync
        // would throw NotImplementedException and this run would fail.
        var secondRun = await _service.GradeReadyMatchesAsync();

        Assert.That(secondRun, Is.EqualTo(new PredictGradingRunResult(Graded: 0, Voided: 0, StillPending: 0, Failed: 0)));
        Assert.That(_footballDataClient.RequestedFixtureIds, Is.EqualTo(new[] { 501 }), "a Graded match must never be re-fetched from football-data.org on a later run");

        var storedPrediction = await _dbContext.PredictMatchPredictions.AsNoTracking().SingleAsync(p => p.Id == prediction.Id);
        Assert.That(storedPrediction.FinalPoints, Is.EqualTo(3 * ScoringRules.PredictPointsPerComponent), "re-running must not change an already-graded prediction's points");
    }

    [Test]
    public async Task REQ1305_GradeReadyMatchesAsync_AlreadyVoidedMatch_ExcludedFromNextRun_NeverRefetched()
    {
        var match = await SeedReadyMatchAsync(fixtureId: 502, kickoffHoursAgo: 3);
        _footballDataClient.Results[502] = new FootballDataFixtureResult(502, FootballDataFixtureOutcome.PostponedOrAbandoned, "PST", null, null);
        var firstRun = await _service.GradeReadyMatchesAsync();
        Assert.That(firstRun.Voided, Is.EqualTo(1));

        var secondRun = await _service.GradeReadyMatchesAsync();

        Assert.That(secondRun, Is.EqualTo(new PredictGradingRunResult(Graded: 0, Voided: 0, StillPending: 0, Failed: 0)));
        Assert.That(_footballDataClient.RequestedFixtureIds, Is.EqualTo(new[] { 502 }), "a Voided match must never be re-fetched from football-data.org on a later run");
    }

    // ---- Not-yet-ready matches: excluded from the query entirely ------

    [Test]
    public async Task REQ1305_GradeReadyMatchesAsync_MatchWhoseKickoffPlusDurationHasNotYetPassed_NotRequestedAtAll()
    {
        // Kickoff 1h ago + 2h typical duration = 1h in the FUTURE: not
        // ready yet.
        await SeedReadyMatchAsync(fixtureId: 601, kickoffHoursAgo: 1);

        var result = await _service.GradeReadyMatchesAsync();

        Assert.That(result, Is.EqualTo(new PredictGradingRunResult(Graded: 0, Voided: 0, StillPending: 0, Failed: 0)));
        Assert.That(_footballDataClient.RequestedFixtureIds, Is.Empty, "a match not yet due for grading must never reach football-data.org at all");
    }

    // ---- One match's FootballDataClientException doesn't abort the run --

    [Test]
    public async Task REQ1305_GradeReadyMatchesAsync_OneMatchThrowsFootballDataClientException_OtherMatchesStillGrade()
    {
        var failingMatch = await SeedReadyMatchAsync(fixtureId: 701, kickoffHoursAgo: 3);
        _footballDataClient.ExceptionsToThrow[701] = new FootballDataClientException("simulated transient football-data.org failure");

        var normalMatch = await SeedReadyMatchAsync(fixtureId: 702, kickoffHoursAgo: 3);
        var normalPrediction = await AddPredictionAsync(normalMatch.Id, homeGoals: 0, awayGoals: 0);
        _footballDataClient.Results[702] = new FootballDataFixtureResult(702, FootballDataFixtureOutcome.Finished, "FT", 0, 0);

        var result = await _service.GradeReadyMatchesAsync();

        Assert.That(result, Is.EqualTo(new PredictGradingRunResult(Graded: 1, Voided: 0, StillPending: 0, Failed: 1)));

        var storedFailingMatch = await _dbContext.PredictMatches.AsNoTracking().SingleAsync(m => m.Id == failingMatch.Id);
        Assert.That(storedFailingMatch.GradingStatus, Is.EqualTo(PredictMatchGradingStatus.Pending), "a failed lookup must leave the match Pending for a later retry");

        var storedNormalMatch = await _dbContext.PredictMatches.AsNoTracking().SingleAsync(m => m.Id == normalMatch.Id);
        Assert.That(storedNormalMatch.GradingStatus, Is.EqualTo(PredictMatchGradingStatus.Graded));
        var storedNormalPrediction = await _dbContext.PredictMatchPredictions.AsNoTracking().SingleAsync(p => p.Id == normalPrediction.Id);
        Assert.That(storedNormalPrediction.FinalPoints, Is.EqualTo(3 * ScoringRules.PredictPointsPerComponent));
    }

    // ---- helpers --------------------------------------------------

    private async Task<PredictMatch> SeedReadyMatchAsync(int fixtureId, int kickoffHoursAgo)
    {
        var instanceId = Guid.NewGuid();
        var match = new PredictMatch
        {
            Id = Guid.NewGuid(),
            PredictInstanceId = instanceId,
            ExternalFixtureId = fixtureId,
            HomeTeamName = $"Home {fixtureId}",
            AwayTeamName = $"Away {fixtureId}",
            KickoffUtc = Now.UtcDateTime.AddHours(-kickoffHoursAgo),
        };
        var instance = new PredictInstance { Id = instanceId, TemplateId = Guid.NewGuid(), Matches = [match] };
        await _repository.AddInstanceAsync(instance);
        return match;
    }

    private async Task<PredictMatchPrediction> AddPredictionAsync(Guid predictMatchId, int homeGoals, int awayGoals)
    {
        var userId = Guid.NewGuid();
        await _repository.AddOrUpdatePredictionAsync(predictMatchId, userId, homeGoals, awayGoals, Now.UtcDateTime.AddHours(-4));
        return await _dbContext.PredictMatchPredictions.AsNoTracking()
            .SingleAsync(p => p.PredictMatchId == predictMatchId && p.UserId == userId);
    }
}

using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.ApiFootball;

namespace XGArcade.Games.XGPredict.Tests;

// COMP-15/ADR-0096: REQ-1301 (round generation), REQ-1302 (prediction
// submission), REQ-1303 (round lock at the first match's kickoff), plus the
// trivial GetCellIdsAsync derivative. Follows this repo's no-mocking-
// framework pattern (docs/coding-guidelines.md "don't over-mock") — a real,
// InMemory-backed PredictInstanceRepository plus a hand-rolled
// FakeApiFootballClient, same "compose the real thing, fake only external
// I/O" shape XGPathGameModuleTests/GridGameModuleTests already use.
//
// REQ-1304/1305 (scoring/grading) are explicitly out of scope for this
// story — not tested here.
//
// GameKey_ReturnsXgPredict/GetMaxAttemptsForCellAsync_ThrowsNotImplementedException/
// the REQ-215/REQ-216 tests below are unchanged from S-190/S-191's original
// scaffold. GenerateInstanceAsync_ThrowsNotImplementedException/
// ScoreSubmissionAsync_ThrowsNotImplementedException/
// GetCellIdsAsync_ThrowsNotImplementedException were removed — those three
// methods are no longer stubs (ADR-0096, this story).
public class XGPredictGameModuleTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPredictInstanceRepository _repository = null!;
    private FakeApiFootballClient _apiFootballClient = null!;
    private ManualTimeProvider _timeProvider = null!;
    private XGPredictGameModule _module = null!;

    private static readonly DateTimeOffset Now = new(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PredictInstanceRepository(_dbContext);
        _apiFootballClient = new FakeApiFootballClient();
        _timeProvider = new ManualTimeProvider(Now);
        _module = new XGPredictGameModule(_repository, _apiFootballClient, _timeProvider);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public void GameKey_ReturnsXgPredict()
    {
        Assert.That(_module.GameKey, Is.EqualTo("xg-predict"));
        Assert.That(_module.GameKey, Is.EqualTo(XGPredictGameModule.XGPredictGameKey));
    }

    // ---- REQ-1301: round generation ----------------------------------

    [Test]
    public async Task REQ1301_GenerateInstanceAsync_SelectsTightestKickoffClusterOfMatchCount()
    {
        var template = await AddTemplateAsync(matchCount: 5);
        // Saturday 3pm block (tight, span 30min) vs. two Sunday outliers
        // (would widen the span enormously if included) — the tight block
        // must win.
        var baseTime = new DateTime(2026, 9, 5, 15, 0, 0, DateTimeKind.Utc);
        _apiFootballClient.Fixtures =
        [
            Fixture(1, baseTime),
            Fixture(2, baseTime.AddMinutes(5)),
            Fixture(3, baseTime.AddMinutes(10)),
            Fixture(4, baseTime.AddMinutes(20)),
            Fixture(5, baseTime.AddMinutes(30)),
            Fixture(6, baseTime.AddDays(1)),
            Fixture(7, baseTime.AddDays(-1)),
        ];

        var result = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _repository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Matches, Has.Count.EqualTo(5));
        var selectedFixtureIds = instance.Matches.Select(m => m.ExternalFixtureId).OrderBy(id => id).ToList();
        Assert.That(selectedFixtureIds, Is.EqualTo(new[] { 1, 2, 3, 4, 5 }),
            "the tightest 5-match window (30-minute span) must be selected over the two wider-spread outliers");
    }

    [Test]
    public async Task REQ1301_GenerateInstanceAsync_TieCase_FirstOccurrenceWins()
    {
        var template = await AddTemplateAsync(matchCount: 2);
        var baseTime = new DateTime(2026, 9, 5, 15, 0, 0, DateTimeKind.Utc);
        // Two windows with an equal 10-minute span: (1,2) and (3,4).
        _apiFootballClient.Fixtures =
        [
            Fixture(1, baseTime),
            Fixture(2, baseTime.AddMinutes(10)),
            Fixture(3, baseTime.AddHours(2)),
            Fixture(4, baseTime.AddHours(2).AddMinutes(10)),
        ];

        var result = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _repository.GetInstanceByIdAsync(result.Id);
        var selectedFixtureIds = instance!.Matches.Select(m => m.ExternalFixtureId).OrderBy(id => id).ToList();
        Assert.That(selectedFixtureIds, Is.EqualTo(new[] { 1, 2 }),
            "on a span tie, the first-occurring window (earliest kickoffs) must win");
    }

    [Test]
    public async Task REQ1301_GenerateInstanceAsync_FewerThanMatchCountFixtures_ThrowsPredictGenerationException_NoInstancePersisted()
    {
        var template = await AddTemplateAsync(matchCount: 5);
        var baseTime = new DateTime(2026, 9, 5, 15, 0, 0, DateTimeKind.Utc);
        _apiFootballClient.Fixtures =
        [
            Fixture(1, baseTime),
            Fixture(2, baseTime.AddMinutes(5)),
            Fixture(3, baseTime.AddMinutes(10)),
        ];

        Assert.ThrowsAsync<PredictGenerationException>(
            () => _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(await _dbContext.PredictInstances.CountAsync(), Is.EqualTo(0),
            "an aborted generation must not persist a degraded instance");
    }

    [Test]
    public async Task REQ1301_GenerateInstanceAsync_Deterministic_SameFixtureListSameSelectionEveryTime()
    {
        var template = await AddTemplateAsync(matchCount: 3);
        var baseTime = new DateTime(2026, 9, 5, 15, 0, 0, DateTimeKind.Utc);
        var fixtures = new List<ApiFootballFixture>
        {
            Fixture(1, baseTime.AddHours(3)),
            Fixture(2, baseTime),
            Fixture(3, baseTime.AddMinutes(5)),
            Fixture(4, baseTime.AddMinutes(10)),
            Fixture(5, baseTime.AddHours(5)),
        };

        _apiFootballClient.Fixtures = fixtures;
        var firstResult = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var firstInstance = await _repository.GetInstanceByIdAsync(firstResult.Id);
        var firstSelection = firstInstance!.Matches.Select(m => m.ExternalFixtureId).OrderBy(id => id).ToList();

        _apiFootballClient.Fixtures = fixtures; // same list again
        var secondResult = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var secondInstance = await _repository.GetInstanceByIdAsync(secondResult.Id);
        var secondSelection = secondInstance!.Matches.Select(m => m.ExternalFixtureId).OrderBy(id => id).ToList();

        Assert.That(secondSelection, Is.EqualTo(firstSelection));
    }

    [Test]
    public void REQ1301_GenerateInstanceAsync_TemplateNotFound_ThrowsPredictGenerationException()
    {
        Assert.ThrowsAsync<PredictGenerationException>(
            () => _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));
    }

    // ---- REQ-1302: prediction submission ------------------------------

    [Test]
    public async Task REQ1302_ScoreSubmissionAsync_ValidPredictionBeforeLock_IsStored()
    {
        var (instanceId, match) = await SeedInstanceAsync(kickoffOffsetsHours: [1, 2, 3, 4, 5]);
        var userId = Guid.NewGuid();

        var result = await _module.ScoreSubmissionAsync(
            instanceId, userId, new PredictionSubmission(match.Id, HomeGoals: 2, AwayGoals: 1));

        Assert.That(result.IsCorrect, Is.False, "ADR-0096 §4: IsCorrect=false here means 'not yet graded', not 'wrong'");
        Assert.That(result.PlayerAnswerId, Is.Null);
        var stored = await _repository.GetPredictionAsync(match.Id, userId);
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!.HomeGoals, Is.EqualTo(2));
        Assert.That(stored.AwayGoals, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ1302_ScoreSubmissionAsync_ResubmissionBeforeLock_ReplacesPriorValue_NotSecondRow()
    {
        var (instanceId, match) = await SeedInstanceAsync(kickoffOffsetsHours: [1, 2, 3, 4, 5]);
        var userId = Guid.NewGuid();
        await _module.ScoreSubmissionAsync(instanceId, userId, new PredictionSubmission(match.Id, 2, 1));

        await _module.ScoreSubmissionAsync(instanceId, userId, new PredictionSubmission(match.Id, 0, 0));

        var stored = await _repository.GetPredictionAsync(match.Id, userId);
        Assert.That(stored!.HomeGoals, Is.EqualTo(0));
        Assert.That(stored.AwayGoals, Is.EqualTo(0));
        Assert.That(await _dbContext.PredictMatchPredictions.CountAsync(p => p.PredictMatchId == match.Id && p.UserId == userId),
            Is.EqualTo(1), "a resubmission must replace the row, never insert a second one");
    }

    [TestCase(-1, 0)]
    [TestCase(0, -1)]
    [TestCase(-1, -1)]
    public async Task REQ1302_ScoreSubmissionAsync_NegativeGoalCount_ThrowsPredictScoringException_ExistingPredictionUnchanged(
        int homeGoals, int awayGoals)
    {
        var (instanceId, match) = await SeedInstanceAsync(kickoffOffsetsHours: [1, 2, 3, 4, 5]);
        var userId = Guid.NewGuid();
        await _module.ScoreSubmissionAsync(instanceId, userId, new PredictionSubmission(match.Id, 2, 1));

        Assert.ThrowsAsync<PredictScoringException>(
            () => _module.ScoreSubmissionAsync(instanceId, userId, new PredictionSubmission(match.Id, homeGoals, awayGoals)));

        var stored = await _repository.GetPredictionAsync(match.Id, userId);
        Assert.That(stored!.HomeGoals, Is.EqualTo(2), "an invalid resubmission must leave the previously stored prediction unchanged");
        Assert.That(stored.AwayGoals, Is.EqualTo(1));
    }

    [Test]
    public void REQ1302_ScoreSubmissionAsync_InstanceNotFound_ThrowsPredictScoringException()
    {
        Assert.ThrowsAsync<PredictScoringException>(
            () => _module.ScoreSubmissionAsync(Guid.NewGuid(), Guid.NewGuid(), new PredictionSubmission(Guid.NewGuid(), 1, 1)));
    }

    [Test]
    public async Task REQ1302_ScoreSubmissionAsync_MatchNotFound_ThrowsPredictScoringException()
    {
        var (instanceId, _) = await SeedInstanceAsync(kickoffOffsetsHours: [1, 2, 3, 4, 5]);

        Assert.ThrowsAsync<PredictScoringException>(
            () => _module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new PredictionSubmission(Guid.NewGuid(), 1, 1)));
    }

    // ---- REQ-1303: round lock at the first match's kickoff ------------

    [Test]
    public async Task REQ1303_ScoreSubmissionAsync_BeforeLock_Succeeds()
    {
        // Earliest kickoff is 1 hour from Now — still before lock.
        var (instanceId, match) = await SeedInstanceAsync(kickoffOffsetsHours: [1, 2, 3, 4, 5]);

        Assert.DoesNotThrowAsync(
            () => _module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new PredictionSubmission(match.Id, 1, 0)));
    }

    [Test]
    public async Task REQ1303_ScoreSubmissionAsync_AtOrAfterFirstKickoff_RejectsEveryMatch_IncludingOneNotYetKickedOff()
    {
        // Matches kick off at Now+1h, Now+2h, ... Now+5h. Advance the clock
        // to exactly the first match's kickoff (the round-level lock
        // instant) — matches 2-5 have individually NOT kicked off yet, but
        // REQ-1303 says the whole round is locked regardless.
        var (instanceId, matches) = await SeedInstanceWithAllMatchesAsync(kickoffOffsetsHours: [1, 2, 3, 4, 5]);
        _timeProvider.Advance(TimeSpan.FromHours(1));

        foreach (var match in matches)
        {
            Assert.ThrowsAsync<PredictRoundLockedException>(
                () => _module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new PredictionSubmission(match.Id, 1, 1)),
                $"match {match.Id} (kickoff offset present in the round) must be rejected once the round-level lock has passed, " +
                "even if this specific match's own kickoff hasn't happened yet");
        }
    }

    // ---- GetCellIdsAsync -------------------------------------------

    [Test]
    public async Task GetCellIdsAsync_ReturnsAllMatchIds()
    {
        var (instanceId, matches) = await SeedInstanceWithAllMatchesAsync(kickoffOffsetsHours: [1, 2, 3, 4, 5]);

        var cellIds = await _module.GetCellIdsAsync(instanceId);

        Assert.That(cellIds, Is.EquivalentTo(matches.Select(m => m.Id)));
    }

    [Test]
    public void GetCellIdsAsync_InstanceNotFound_ThrowsPredictScoringException()
    {
        Assert.ThrowsAsync<PredictScoringException>(() => _module.GetCellIdsAsync(Guid.NewGuid()));
    }

    // ---- Unchanged from S-190/S-191's scaffold -------------------------

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

    // ---- helpers --------------------------------------------------

    private async Task<PredictTemplate> AddTemplateAsync(int matchCount)
    {
        var template = new PredictTemplate { Id = Guid.NewGuid(), MatchCount = matchCount };
        _dbContext.PredictTemplates.Add(template);
        await _dbContext.SaveChangesAsync();
        return template;
    }

    private static ApiFootballFixture Fixture(int fixtureId, DateTime kickoffUtc) =>
        new(fixtureId, HomeTeamId: fixtureId * 100, HomeTeamName: $"Home {fixtureId}",
            AwayTeamId: fixtureId * 100 + 1, AwayTeamName: $"Away {fixtureId}", KickoffUtc: kickoffUtc);

    // Seeds a PredictInstance directly via the repository (bypassing
    // GenerateInstanceAsync/FakeApiFootballClient) — REQ-1302/1303's own
    // tests care about scoring/locking behavior against an already-generated
    // instance, not selection, same "seed the entity directly" precedent
    // XGPathGameModuleTests uses for its own REQ-1204/1205 scoring tests.
    private async Task<(Guid InstanceId, PredictMatch FirstMatch)> SeedInstanceAsync(int[] kickoffOffsetsHours)
    {
        var (instanceId, matches) = await SeedInstanceWithAllMatchesAsync(kickoffOffsetsHours);
        return (instanceId, matches[0]);
    }

    private async Task<(Guid InstanceId, List<PredictMatch> Matches)> SeedInstanceWithAllMatchesAsync(int[] kickoffOffsetsHours)
    {
        var instanceId = Guid.NewGuid();
        var matches = kickoffOffsetsHours.Select((offset, i) => new PredictMatch
        {
            Id = Guid.NewGuid(),
            PredictInstanceId = instanceId,
            ExternalFixtureId = i + 1,
            HomeTeamName = $"Home {i + 1}",
            AwayTeamName = $"Away {i + 1}",
            KickoffUtc = Now.UtcDateTime.AddHours(offset),
        }).ToList();

        var instance = new PredictInstance { Id = instanceId, TemplateId = Guid.NewGuid(), Matches = matches };
        await _repository.AddInstanceAsync(instance);
        return (instanceId, matches);
    }
}

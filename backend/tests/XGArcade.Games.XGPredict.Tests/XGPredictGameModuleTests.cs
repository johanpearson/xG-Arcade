using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.FootballData;

namespace XGArcade.Games.XGPredict.Tests;

// COMP-15/ADR-0096: REQ-1301 (round generation), REQ-1302 (prediction
// submission), REQ-1303 (round lock at the first match's kickoff), plus the
// trivial GetCellIdsAsync derivative. Follows this repo's no-mocking-
// framework pattern (docs/coding-guidelines.md "don't over-mock") — a real,
// InMemory-backed PredictInstanceRepository plus a hand-rolled
// FakeFootballDataClient, same "compose the real thing, fake only external
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
    private FakeFootballDataClient _footballDataClient = null!;
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
        _footballDataClient = new FakeFootballDataClient();
        _timeProvider = new ManualTimeProvider(Now);
        _module = new XGPredictGameModule(_repository, _footballDataClient, new PredictGradingOptions(), _timeProvider);
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
        _footballDataClient.Fixtures =
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

        // ADR-0102: a first-ever round (no LatestGameInstanceId configured
        // above) must never return null.
        Assert.That(result, Is.Not.Null);
        var instance = await _repository.GetInstanceByIdAsync(result!.Id);
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
        _footballDataClient.Fixtures =
        [
            Fixture(1, baseTime),
            Fixture(2, baseTime.AddMinutes(10)),
            Fixture(3, baseTime.AddHours(2)),
            Fixture(4, baseTime.AddHours(2).AddMinutes(10)),
        ];

        var result = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(result, Is.Not.Null, "a first-ever round must never return null (ADR-0102)");
        var instance = await _repository.GetInstanceByIdAsync(result!.Id);
        var selectedFixtureIds = instance!.Matches.Select(m => m.ExternalFixtureId).OrderBy(id => id).ToList();
        Assert.That(selectedFixtureIds, Is.EqualTo(new[] { 1, 2 }),
            "on a span tie, the first-occurring window (earliest kickoffs) must win");
    }

    [Test]
    public async Task REQ1301_GenerateInstanceAsync_FewerThanMatchCountFixtures_ThrowsPredictGenerationException_NoInstancePersisted()
    {
        var template = await AddTemplateAsync(matchCount: 5);
        var baseTime = new DateTime(2026, 9, 5, 15, 0, 0, DateTimeKind.Utc);
        _footballDataClient.Fixtures =
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
        var fixtures = new List<FootballDataFixture>
        {
            Fixture(1, baseTime.AddHours(3)),
            Fixture(2, baseTime),
            Fixture(3, baseTime.AddMinutes(5)),
            Fixture(4, baseTime.AddMinutes(10)),
            Fixture(5, baseTime.AddHours(5)),
        };

        _footballDataClient.Fixtures = fixtures;
        var firstResult = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        Assert.That(firstResult, Is.Not.Null, "a first-ever round must never return null (ADR-0102)");
        var firstInstance = await _repository.GetInstanceByIdAsync(firstResult!.Id);
        var firstSelection = firstInstance!.Matches.Select(m => m.ExternalFixtureId).OrderBy(id => id).ToList();

        // Neither call configures RoundConfig.LatestGameInstanceId, so
        // ADR-0102's dedup check never runs here — each call is treated as
        // independent "first-ever round" generation, unaffected by the
        // other. (REQ1301_GenerateInstanceAsync_LatestInstanceHasIdenticalFixtureSet_ReturnsNull
        // below is where the dedup path itself is covered.)
        _footballDataClient.Fixtures = fixtures; // same list again
        var secondResult = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        Assert.That(secondResult, Is.Not.Null);
        var secondInstance = await _repository.GetInstanceByIdAsync(secondResult!.Id);
        var secondSelection = secondInstance!.Matches.Select(m => m.ExternalFixtureId).OrderBy(id => id).ToList();

        Assert.That(secondSelection, Is.EqualTo(firstSelection));
    }

    [Test]
    public void REQ1301_GenerateInstanceAsync_TemplateNotFound_ThrowsPredictGenerationException()
    {
        Assert.ThrowsAsync<PredictGenerationException>(
            () => _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));
    }

    // ---- ADR-0102: matchday-tracked generation (S-204) -----------------
    //
    // RoundGenerationService chains rounds by elapsed time, agnostic to
    // real fixture timing — these tests prove GenerateInstanceAsync's own
    // fixture-ID-set dedup against config.LatestGameInstanceId is what
    // actually keeps a midweek matchday from being silently skipped or
    // duplicated (see ADR-0102's worked examples for the full reasoning;
    // that dedup decision is made independently of Round.StartTime/EndTime,
    // which is why these tests exercise XGPredictGameModule directly rather
    // than going through RoundGenerationService).

    [Test]
    public async Task REQ1301_GenerateInstanceAsync_MidweekMatchdayFollowsWeekendMatchday_NeitherSkippedNorDuplicated()
    {
        var template = await AddTemplateAsync(matchCount: 5);
        // Gameweek 1: Saturday block. Gameweek 2: the following Tuesday —
        // a real, routine midweek spacing (cup replays/European weeks),
        // deliberately NOT 7 days after gameweek 1.
        var saturday = new DateTime(2026, 9, 5, 15, 0, 0, DateTimeKind.Utc);
        var tuesday = new DateTime(2026, 9, 8, 19, 0, 0, DateTimeKind.Utc);
        var gameweekOneFixtures = Enumerable.Range(1, 5).Select(i => Fixture(i, saturday.AddMinutes(i * 5))).ToList();
        var gameweekTwoFixtures = Enumerable.Range(11, 5).Select(i => Fixture(i, tuesday.AddMinutes((i - 10) * 5))).ToList();

        // Call 1: no LatestGameInstanceId (first-ever round) — must pick up
        // gameweek 1 and never return null.
        _footballDataClient.Fixtures = gameweekOneFixtures;
        var firstResult = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        Assert.That(firstResult, Is.Not.Null, "a first-ever round must never return null (ADR-0102)");

        // Call 2: LatestGameInstanceId now points at gameweek 1's instance,
        // but the fake client has already moved on to gameweek 2's fixtures
        // (still fully future, same as GetUpcomingGameweekFixturesAsync's
        // real contract) — this must produce a genuinely NEW instance, not
        // null, proving the midweek round is not silently dropped.
        _timeProvider.Advance(TimeSpan.FromDays(2));
        _footballDataClient.Fixtures = gameweekTwoFixtures;
        var secondResult = await _module.GenerateInstanceAsync(
            new RoundConfig { TemplateId = template.Id, LatestGameInstanceId = firstResult!.Id });
        Assert.That(secondResult, Is.Not.Null, "a genuinely new (midweek) matchday must not be dropped");
        Assert.That(secondResult!.Id, Is.Not.EqualTo(firstResult.Id));
        var secondInstance = await _repository.GetInstanceByIdAsync(secondResult.Id);
        var secondFixtureIds = secondInstance!.Matches.Select(m => m.ExternalFixtureId).OrderBy(id => id).ToList();
        Assert.That(secondFixtureIds, Is.EqualTo(new[] { 11, 12, 13, 14, 15 }));

        // Call 3: LatestGameInstanceId now points at gameweek 2's instance,
        // fixtures unchanged ("next upcoming matchday" hasn't changed) —
        // must return null, proving no duplicate is created for the same
        // matchday on a repeat call.
        _timeProvider.Advance(TimeSpan.FromHours(6));
        var thirdResult = await _module.GenerateInstanceAsync(
            new RoundConfig { TemplateId = template.Id, LatestGameInstanceId = secondResult.Id });
        Assert.That(thirdResult, Is.Null, "the same matchday must not be duplicated on a repeat call");
    }

    [Test]
    public async Task REQ1301_GenerateInstanceAsync_LatestInstanceHasIdenticalFixtureSet_ReturnsNull_RegardlessOfElapsedTime()
    {
        var template = await AddTemplateAsync(matchCount: 5);
        var baseTime = new DateTime(2026, 9, 5, 15, 0, 0, DateTimeKind.Utc);
        var fixtures = Enumerable.Range(1, 5).Select(i => Fixture(i, baseTime.AddMinutes(i * 5))).ToList();

        _footballDataClient.Fixtures = fixtures;
        var firstResult = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        Assert.That(firstResult, Is.Not.Null);

        // A large amount of simulated time passes (well beyond any
        // plausible RoundDuration), but the fake client's "next upcoming
        // matchday" is still the exact same fixture set — this must still
        // return null. Proves the ordinary weekly cadence doesn't
        // over-generate regardless of how RoundDuration and real fixture
        // timing happen to line up.
        _timeProvider.Advance(TimeSpan.FromDays(30));
        var result = await _module.GenerateInstanceAsync(
            new RoundConfig { TemplateId = template.Id, LatestGameInstanceId = firstResult!.Id });

        Assert.That(result, Is.Null);
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
    public async Task REQ1302_ScoreSubmissionAsync_NegativeGoalCount_ThrowsPredictInvalidSubmissionException_ExistingPredictionUnchanged(
        int homeGoals, int awayGoals)
    {
        var (instanceId, match) = await SeedInstanceAsync(kickoffOffsetsHours: [1, 2, 3, 4, 5]);
        var userId = Guid.NewGuid();
        await _module.ScoreSubmissionAsync(instanceId, userId, new PredictionSubmission(match.Id, 2, 1));

        Assert.ThrowsAsync<PredictInvalidSubmissionException>(
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

    // ---- REQ-710/S-201: account-deletion purge -------------------------
    // AccountDeletionService (Core.Auth) never references PredictMatchPrediction/
    // PredictPlayerLock/IPredictInstanceRepository directly (ADR-0003) — it
    // reaches this module's own per-user data exclusively through
    // IGameModule.PurgeUserDataAsync, exercised here against the real,
    // InMemory-backed _repository this test class already uses everywhere
    // else. AccountDeletionServiceTests (XGArcade.Core.Tests) only proves the
    // generic "called on every registered module" loop, via FakeGameModule —
    // this is where the actual anonymize/hard-delete behavior is covered.

    [Test]
    public async Task REQ710_PurgeUserDataAsync_AnonymizesPredictMatchPredictionRows_SeversLinkWithoutDeletingRows()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var ownPrediction = await SeedPredictionAsync(userId);
        var otherPrediction = await SeedPredictionAsync(otherUserId);

        await _module.PurgeUserDataAsync(userId);

        // The row itself must survive — other users' PredictInstance point
        // totals (IPredictInstanceRepository.GetTotalPointsByInstanceIdAsync)
        // depend on it, same reasoning as Guess (REQ-710).
        var remainingOwnPrediction = await _dbContext.PredictMatchPredictions
            .AsNoTracking().SingleAsync(p => p.Id == ownPrediction.Id);
        Assert.That(remainingOwnPrediction.UserId, Is.Null);
        // A different user's prediction in the same seed data must be
        // completely untouched (proves scoping, not an over-broad update).
        var remainingOtherPrediction = await _dbContext.PredictMatchPredictions
            .AsNoTracking().SingleAsync(p => p.Id == otherPrediction.Id);
        Assert.That(remainingOtherPrediction.UserId, Is.EqualTo(otherUserId));
    }

    [Test]
    public async Task REQ710_PurgeUserDataAsync_HardDeletesPredictPlayerLockRows_ForDeletedUserOnly()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await SeedPlayerLockAsync(userId);
        var otherLock = await SeedPlayerLockAsync(otherUserId);

        await _module.PurgeUserDataAsync(userId);

        // Unlike Guess/PredictMatchPrediction, PredictPlayerLock.UserId is
        // non-nullable (half of its composite primary key) — the row is
        // hard-deleted rather than anonymized (XGArcadeDbContext's own
        // OnModelCreating comment on PredictPlayerLock).
        var remaining = await _dbContext.PredictPlayerLocks
            .AsNoTracking().Where(l => l.UserId == userId).ToListAsync();
        Assert.That(remaining, Is.Empty);
        // A different user's lock row in the same seed data must survive.
        var remainingOtherLock = await _dbContext.PredictPlayerLocks
            .AsNoTracking()
            .SingleOrDefaultAsync(l => l.PredictInstanceId == otherLock.PredictInstanceId && l.UserId == otherLock.UserId);
        Assert.That(remainingOtherLock, Is.Not.Null);
    }

    private async Task<PredictMatchPrediction> SeedPredictionAsync(Guid userId)
    {
        var prediction = new PredictMatchPrediction
        {
            Id = Guid.NewGuid(),
            PredictMatchId = Guid.NewGuid(),
            UserId = userId,
            HomeGoals = 2,
            AwayGoals = 1,
            SubmittedAt = DateTime.UtcNow,
        };
        _dbContext.PredictMatchPredictions.Add(prediction);
        await _dbContext.SaveChangesAsync();
        return prediction;
    }

    private async Task<PredictPlayerLock> SeedPlayerLockAsync(Guid userId, Guid? predictInstanceId = null)
    {
        var predictPlayerLock = new PredictPlayerLock
        {
            PredictInstanceId = predictInstanceId ?? Guid.NewGuid(),
            UserId = userId,
            LockedAt = DateTime.UtcNow,
        };
        _dbContext.PredictPlayerLocks.Add(predictPlayerLock);
        await _dbContext.SaveChangesAsync();
        return predictPlayerLock;
    }

    // ---- helpers --------------------------------------------------

    private async Task<PredictTemplate> AddTemplateAsync(int matchCount)
    {
        var template = new PredictTemplate { Id = Guid.NewGuid(), MatchCount = matchCount };
        _dbContext.PredictTemplates.Add(template);
        await _dbContext.SaveChangesAsync();
        return template;
    }

    private static FootballDataFixture Fixture(int fixtureId, DateTime kickoffUtc) =>
        new(fixtureId, HomeTeamId: fixtureId * 100, HomeTeamName: $"Home {fixtureId}",
            AwayTeamId: fixtureId * 100 + 1, AwayTeamName: $"Away {fixtureId}", KickoffUtc: kickoffUtc);

    // Seeds a PredictInstance directly via the repository (bypassing
    // GenerateInstanceAsync/FakeFootballDataClient) — REQ-1302/1303's own
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

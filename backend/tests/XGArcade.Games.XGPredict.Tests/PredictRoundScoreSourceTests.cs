using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGPredict.Tests;

// ADR-0100: the "xg-predict" implementation of Core.Scoring's
// IRoundScoreSource — closes the gap S-193/S-195/S-197/S-198 each flagged
// (LeaderboardService never sourced "xg-predict" totals at all). Same
// no-mocking-framework pattern as PredictGradingServiceTests: a real,
// InMemory-backed PredictInstanceRepository, no fakes for this class'
// single dependency. Round/User rows are plain, unpersisted objects built
// directly in each test (never through IRoundRepository/IUserRepository —
// ADR-0100's "For AI agents" rule: PredictRoundScoreSource must never
// inject either), mirroring exactly what LeaderboardService would hand in.
public class PredictRoundScoreSourceTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPredictInstanceRepository _repository = null!;
    private PredictRoundScoreSource _source = null!;

    private static readonly DateTime Now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PredictInstanceRepository(_dbContext);
        _source = new PredictRoundScoreSource(_repository);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    // ---- GetPerRoundTotalsByUserIdsAsync (REQ-409) ---------------------

    [Test]
    public async Task REQ409_GetPerRoundTotalsByUserIdsAsync_ParticipantWithGradedMatch_ContributesGradedTotal()
    {
        var userId = Guid.NewGuid();
        var (instanceId, matchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(matchId, userId, homeGoals: 2, awayGoals: 1);
        await GradeMatchAsync(matchId, actualHomeGoals: 2, actualAwayGoals: 1, pointsForEachPrediction: 9);
        var round = ClosedPredictRound(instanceId);

        // members: [] with applyGuestEligibilityRules: false — eligibility
        // isn't under test here (see the dedicated guest/claimed-account
        // cases further below), so members is deliberately left empty
        // rather than needing a matching User row for every participant.
        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([userId], [round], [], applyGuestEligibilityRules: false);

        Assert.That(totals[userId], Is.EqualTo(new[] { 9 }));
    }

    [Test]
    public async Task REQ409_GetPerRoundTotalsByUserIdsAsync_ParticipantWithPredictionsButNothingGradedYet_StillQualifiesContributingZero()
    {
        // ADR-0100 §3's own example: a closed round where the user predicted
        // but grading hasn't run yet must still count as a qualifying round
        // (contributing 0), not silently fail to qualify or vanish.
        var userId = Guid.NewGuid();
        var (instanceId, matchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(matchId, userId, homeGoals: 2, awayGoals: 1);
        var round = ClosedPredictRound(instanceId);

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([userId], [round], [], applyGuestEligibilityRules: false);

        Assert.That(totals[userId], Is.EqualTo(new[] { 0 }));
    }

    [Test]
    public async Task REQ409_GetPerRoundTotalsByUserIdsAsync_UserNeverPredictedInThisRound_AbsentFromResult()
    {
        var participantId = Guid.NewGuid();
        var neverPredictedId = Guid.NewGuid();
        var (instanceId, matchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(matchId, participantId, homeGoals: 2, awayGoals: 1);
        await GradeMatchAsync(matchId, actualHomeGoals: 2, actualAwayGoals: 1, pointsForEachPrediction: 9);
        var round = ClosedPredictRound(instanceId);

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync(
            [participantId, neverPredictedId], [round], [], applyGuestEligibilityRules: false);

        Assert.That(totals.ContainsKey(neverPredictedId), Is.False, "absent, not defaulted to an empty list");
    }

    [Test]
    public async Task REQ409_GetPerRoundTotalsByUserIdsAsync_RequestedUserIdsFilter_ExcludesParticipantsNotInTheList()
    {
        var requestedUserId = Guid.NewGuid();
        var otherParticipantId = Guid.NewGuid();
        var (instanceId, matchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(matchId, requestedUserId, homeGoals: 1, awayGoals: 0);
        await AddPredictionAsync(matchId, otherParticipantId, homeGoals: 0, awayGoals: 0);
        await GradeMatchAsync(matchId, actualHomeGoals: 1, actualAwayGoals: 0, pointsForEachPrediction: 6);
        var round = ClosedPredictRound(instanceId);

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([requestedUserId], [round], [], applyGuestEligibilityRules: false);

        Assert.That(totals.Keys, Is.EqualTo(new[] { requestedUserId }));
    }

    [Test]
    public async Task REQ409_GetPerRoundTotalsByUserIdsAsync_ClosedRoundsForAnotherGameKey_NeverContributeAnything()
    {
        var userId = Guid.NewGuid();
        var (instanceId, matchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(matchId, userId, homeGoals: 2, awayGoals: 1);
        await GradeMatchAsync(matchId, actualHomeGoals: 2, actualAwayGoals: 1, pointsForEachPrediction: 9);
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = "xg-grid", // deliberately NOT "xg-predict".
            GameInstanceId = instanceId,
            SequenceNumber = 1,
            StartTime = Now.AddDays(-2),
            EndTime = Now.AddDays(-1),
            AllowGuessChange = true,
            ClosedAt = Now.AddDays(-1),
        };

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([userId], [round], [], applyGuestEligibilityRules: false);

        Assert.That(totals.ContainsKey(userId), Is.False);
    }

    [Test]
    public async Task REQ409_GetPerRoundTotalsByUserIdsAsync_MultipleClosedRounds_OneListEntryPerRoundInOrderSupplied()
    {
        var userId = Guid.NewGuid();
        var (firstInstanceId, firstMatchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(firstMatchId, userId, homeGoals: 2, awayGoals: 1);
        await GradeMatchAsync(firstMatchId, actualHomeGoals: 2, actualAwayGoals: 1, pointsForEachPrediction: 9);
        var (secondInstanceId, secondMatchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(secondMatchId, userId, homeGoals: 0, awayGoals: 0);
        await GradeMatchAsync(secondMatchId, actualHomeGoals: 1, actualAwayGoals: 0, pointsForEachPrediction: 0);
        var firstRound = ClosedPredictRound(firstInstanceId);
        var secondRound = ClosedPredictRound(secondInstanceId);

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync(
            [userId], [firstRound, secondRound], [], applyGuestEligibilityRules: false);

        Assert.That(totals[userId], Is.EqualTo(new[] { 9, 0 }));
    }

    [Test]
    public async Task REQ717_GetPerRoundTotalsByUserIdsAsync_ApplyGuestEligibilityRulesTrue_GuestMemberExcluded()
    {
        var guestId = Guid.NewGuid();
        var guest = new User
        {
            Id = guestId,
            AuthProviderUserId = Guid.NewGuid(),
            DisplayName = "GuestPlayer",
            EmailConfirmed = false,
            IsGuest = true,
            CreatedAt = Now,
        };
        var (instanceId, matchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(matchId, guestId, homeGoals: 2, awayGoals: 1);
        await GradeMatchAsync(matchId, actualHomeGoals: 2, actualAwayGoals: 1, pointsForEachPrediction: 9);
        var round = ClosedPredictRound(instanceId);

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([guestId], [round], [guest], applyGuestEligibilityRules: true);

        Assert.That(totals.ContainsKey(guestId), Is.False);
    }

    [Test]
    public async Task REQ717_GetPerRoundTotalsByUserIdsAsync_ApplyGuestEligibilityRulesFalse_GuestMemberIncluded()
    {
        var guestId = Guid.NewGuid();
        var guest = new User
        {
            Id = guestId,
            AuthProviderUserId = Guid.NewGuid(),
            DisplayName = "GuestPlayer",
            EmailConfirmed = false,
            IsGuest = true,
            CreatedAt = Now,
        };
        var (instanceId, matchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(matchId, guestId, homeGoals: 2, awayGoals: 1);
        await GradeMatchAsync(matchId, actualHomeGoals: 2, actualAwayGoals: 1, pointsForEachPrediction: 9);
        var round = ClosedPredictRound(instanceId);

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([guestId], [round], [guest], applyGuestEligibilityRules: false);

        Assert.That(totals[guestId], Is.EqualTo(new[] { 9 }));
    }

    [Test]
    public async Task REQ717_GetPerRoundTotalsByUserIdsAsync_RoundClosedBeforeClaiming_ExcludedFromClaimedAccount()
    {
        var userId = Guid.NewGuid();
        var claimedAt = Now.AddDays(-1);
        var claimedUser = new User
        {
            Id = userId,
            AuthProviderUserId = Guid.NewGuid(),
            DisplayName = "You",
            EmailConfirmed = true,
            IsGuest = false,
            ClaimedAt = claimedAt,
            CreatedAt = Now.AddDays(-10),
        };
        var (instanceId, matchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(matchId, userId, homeGoals: 2, awayGoals: 1);
        await GradeMatchAsync(matchId, actualHomeGoals: 2, actualAwayGoals: 1, pointsForEachPrediction: 9);
        // Closed strictly BEFORE claiming.
        var round = ClosedPredictRound(instanceId, closedAt: claimedAt.AddDays(-1));

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([userId], [round], [claimedUser]);

        Assert.That(totals.ContainsKey(userId), Is.False);
    }

    [Test]
    public async Task REQ717_GetPerRoundTotalsByUserIdsAsync_RoundClosedAfterClaiming_IncludedForClaimedAccount()
    {
        var userId = Guid.NewGuid();
        var claimedAt = Now.AddDays(-5);
        var claimedUser = new User
        {
            Id = userId,
            AuthProviderUserId = Guid.NewGuid(),
            DisplayName = "You",
            EmailConfirmed = true,
            IsGuest = false,
            ClaimedAt = claimedAt,
            CreatedAt = Now.AddDays(-10),
        };
        var (instanceId, matchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(matchId, userId, homeGoals: 2, awayGoals: 1);
        await GradeMatchAsync(matchId, actualHomeGoals: 2, actualAwayGoals: 1, pointsForEachPrediction: 9);
        // Closed strictly AFTER claiming.
        var round = ClosedPredictRound(instanceId, closedAt: claimedAt.AddDays(1));

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([userId], [round], [claimedUser]);

        Assert.That(totals[userId], Is.EqualTo(new[] { 9 }));
    }

    [Test]
    public async Task REQ409_GetPerRoundTotalsByUserIdsAsync_RoundWithZeroParticipants_ContributesNothingToAnyone()
    {
        var userId = Guid.NewGuid();
        var (instanceId, _) = await SeedInstanceWithOneMatchAsync();
        var round = ClosedPredictRound(instanceId);

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([userId], [round], []);

        Assert.That(totals, Is.Empty);
    }

    // ---- GetActiveRoundTotalsByUserIdAsync (REQ-406/407) ----------------

    [Test]
    public async Task ADR0100_GetActiveRoundTotalsByUserIdAsync_SameGradedSoFarReadAsClosedRoundScope_NoSeparateLiveFormula()
    {
        // ADR-0100 §4: the exact same graded-so-far total, whether the round
        // is still active or already closed — no separate "live" formula.
        var userId = Guid.NewGuid();
        var (instanceId, matchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(matchId, userId, homeGoals: 2, awayGoals: 1);
        await GradeMatchAsync(matchId, actualHomeGoals: 2, actualAwayGoals: 1, pointsForEachPrediction: 9);
        var activeRound = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = XGPredictGameModule.XGPredictGameKey,
            GameInstanceId = instanceId,
            SequenceNumber = 1,
            StartTime = Now.AddDays(-1),
            EndTime = Now.AddDays(6),
            AllowGuessChange = true,
        };

        var totals = await _source.GetActiveRoundTotalsByUserIdAsync(activeRound);

        Assert.That(totals[userId], Is.EqualTo(9));
    }

    [Test]
    public async Task REQ407_GetActiveRoundTotalsByUserIdAsync_ParticipantWithNothingGradedYet_AbsentNotZero()
    {
        var userId = Guid.NewGuid();
        var (instanceId, matchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(matchId, userId, homeGoals: 2, awayGoals: 1);
        var activeRound = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = XGPredictGameModule.XGPredictGameKey,
            GameInstanceId = instanceId,
            SequenceNumber = 1,
            StartTime = Now.AddDays(-1),
            EndTime = Now.AddDays(6),
            AllowGuessChange = true,
        };

        var totals = await _source.GetActiveRoundTotalsByUserIdAsync(activeRound);

        Assert.That(totals.ContainsKey(userId), Is.False, "an ungraded-match-only participant contributes no key yet");
    }

    // ---- GetTotalsByRoundAsync (REQ-408) --------------------------------

    [Test]
    public async Task REQ408_GetTotalsByRoundAsync_ClosedRound_ReturnsGradedTotalsPerUser()
    {
        var firstUserId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        var (instanceId, matchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(matchId, firstUserId, homeGoals: 2, awayGoals: 1);
        await AddPredictionAsync(matchId, secondUserId, homeGoals: 0, awayGoals: 0);
        await _repository.GradeMatchAsync(matchId, actualHomeGoals: 2, actualAwayGoals: 1, finalPointsByPredictionId: await FinalPointsByPredictionIdAsync(matchId, firstUserId, 9, secondUserId, 0));
        var round = ClosedPredictRound(instanceId);

        var totals = await _source.GetTotalsByRoundAsync(round);

        Assert.That(totals[firstUserId], Is.EqualTo(9));
        Assert.That(totals[secondUserId], Is.EqualTo(0));
    }

    // ---- GetTotalsByRoundsAsync (REQ-405) --------------------------------

    [Test]
    public async Task REQ405_GetTotalsByRoundsAsync_SumsGradedTotalsAcrossEveryRoundSupplied()
    {
        var userId = Guid.NewGuid();
        var (firstInstanceId, firstMatchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(firstMatchId, userId, homeGoals: 2, awayGoals: 1);
        await GradeMatchAsync(firstMatchId, actualHomeGoals: 2, actualAwayGoals: 1, pointsForEachPrediction: 9);
        var (secondInstanceId, secondMatchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(secondMatchId, userId, homeGoals: 1, awayGoals: 1);
        await GradeMatchAsync(secondMatchId, actualHomeGoals: 1, actualAwayGoals: 1, pointsForEachPrediction: 9);
        var firstRound = ClosedPredictRound(firstInstanceId);
        var secondRound = ClosedPredictRound(secondInstanceId);

        var totals = await _source.GetTotalsByRoundsAsync([firstRound, secondRound]);

        Assert.That(totals[userId], Is.EqualTo(18));
    }

    [Test]
    public async Task REQ405_GetTotalsByRoundsAsync_RoundWithNothingGradedYet_ContributesZeroNotAbsent()
    {
        var userId = Guid.NewGuid();
        var (gradedInstanceId, gradedMatchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(gradedMatchId, userId, homeGoals: 2, awayGoals: 1);
        await GradeMatchAsync(gradedMatchId, actualHomeGoals: 2, actualAwayGoals: 1, pointsForEachPrediction: 9);
        var (ungradedInstanceId, ungradedMatchId) = await SeedInstanceWithOneMatchAsync();
        await AddPredictionAsync(ungradedMatchId, userId, homeGoals: 1, awayGoals: 0);
        var gradedRound = ClosedPredictRound(gradedInstanceId);
        var ungradedRound = ClosedPredictRound(ungradedInstanceId);

        var totals = await _source.GetTotalsByRoundsAsync([gradedRound, ungradedRound]);

        Assert.That(totals[userId], Is.EqualTo(9), "the ungraded round contributes 0, same as SUM(FinalPoints ?? 0)");
    }

    // ---- helpers --------------------------------------------------------

    private async Task<(Guid InstanceId, Guid MatchId)> SeedInstanceWithOneMatchAsync()
    {
        var instanceId = Guid.NewGuid();
        var match = new PredictMatch
        {
            Id = Guid.NewGuid(),
            PredictInstanceId = instanceId,
            ExternalFixtureId = Random.Shared.Next(1, int.MaxValue),
            HomeTeamName = "Home",
            AwayTeamName = "Away",
            KickoffUtc = Now.AddDays(-1),
        };
        var instance = new PredictInstance { Id = instanceId, TemplateId = Guid.NewGuid(), Matches = [match] };
        await _repository.AddInstanceAsync(instance);
        return (instanceId, match.Id);
    }

    private async Task AddPredictionAsync(Guid matchId, Guid userId, int homeGoals, int awayGoals) =>
        await _repository.AddOrUpdatePredictionAsync(matchId, userId, homeGoals, awayGoals, Now.AddHours(-4));

    // Grades a match with exactly one stored prediction, awarding it
    // pointsForEachPrediction — the common single-participant case most
    // tests above need.
    private async Task GradeMatchAsync(Guid matchId, int actualHomeGoals, int actualAwayGoals, int pointsForEachPrediction)
    {
        var predictions = await _repository.GetPredictionsForMatchAsync(matchId);
        var finalPointsByPredictionId = predictions.ToDictionary(p => p.Id, _ => pointsForEachPrediction);
        await _repository.GradeMatchAsync(matchId, actualHomeGoals, actualAwayGoals, finalPointsByPredictionId);
    }

    // Builds a finalPointsByPredictionId map for a match with exactly two
    // stored predictions, one per user, each awarded its own point value.
    private async Task<IReadOnlyDictionary<Guid, int>> FinalPointsByPredictionIdAsync(
        Guid matchId, Guid firstUserId, int firstUserPoints, Guid secondUserId, int secondUserPoints)
    {
        var predictions = await _repository.GetPredictionsForMatchAsync(matchId);
        return predictions.ToDictionary(
            p => p.Id,
            p => p.UserId == firstUserId ? firstUserPoints : secondUserPoints);
    }

    // A closed "xg-predict" Round pointing at the given PredictInstance —
    // never persisted via IRoundRepository (PredictRoundScoreSource must
    // never inject it, ADR-0100's "For AI agents" rule), same shape
    // LeaderboardService would resolve and hand in.
    private static Round ClosedPredictRound(Guid predictInstanceId, DateTime? closedAt = null)
    {
        var closedAtValue = closedAt ?? Now.AddDays(-1);
        return new Round
        {
            Id = Guid.NewGuid(),
            GameKey = XGPredictGameModule.XGPredictGameKey,
            GameInstanceId = predictInstanceId,
            SequenceNumber = 1,
            StartTime = closedAtValue.AddDays(-1),
            EndTime = closedAtValue,
            AllowGuessChange = true,
            ClosedAt = closedAtValue,
        };
    }
}

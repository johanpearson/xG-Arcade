using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGHigherLower.Tests;

// ADR-0100/REQ-1505/S-226: the "xg-higher-lower" implementation of
// Core.Scoring's IRoundScoreSource — closes the gap CI caught (widening
// LeaderboardEndpoints.ValidateGameKey's allow-list without a matching
// IRoundScoreSourceResolver entry is a live 500, not a harmless gap, now
// that RoundScoreSourceResolver.Resolve is mandatory for every accepted
// GameKey). Same no-mocking-framework pattern as
// PredictRoundScoreSourceTests (Games.XGPredict.Tests): a real,
// InMemory-backed HigherLowerInstanceRepository, no fakes for this class'
// single dependency. Round/User rows are plain, unpersisted objects built
// directly in each test (never through IRoundRepository/IUserRepository —
// ADR-0100's "For AI agents" rule: HigherLowerRoundScoreSource must never
// inject either), mirroring exactly what LeaderboardService would hand in.
//
// Unlike PredictRoundScoreSourceTests, there is no "graded vs. ungraded"
// axis to cover — HigherLowerAttempt.StreakLength is always a real, current
// value the instant an attempt row exists, ended or in-progress alike (see
// HigherLowerAttempt's/HigherLowerRoundScoreSource's own doc comments), so
// every test below just varies HasEnded/StreakLength directly rather than
// needing a separate "grade the match" step.
public class HigherLowerRoundScoreSourceTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IHigherLowerInstanceRepository _repository = null!;
    private HigherLowerRoundScoreSource _source = null!;

    private static readonly DateTime Now = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new HigherLowerInstanceRepository(_dbContext);
        _source = new HigherLowerRoundScoreSource(_repository);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    // ---- GetPerRoundTotalsByUserIdsAsync (REQ-409/1505) -----------------

    [Test]
    public async Task REQ1505_GetPerRoundTotalsByUserIdsAsync_ParticipantWithAttempt_ContributesStreakLength()
    {
        var userId = Guid.NewGuid();
        var instanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(instanceId, userId, streakLength: 6, hasEnded: true);
        var round = ClosedHigherLowerRound(instanceId);

        // members: [] with applyGuestEligibilityRules: false — eligibility
        // isn't under test here (see the dedicated guest/claimed-account
        // cases further below), so members is deliberately left empty
        // rather than needing a matching User row for every participant.
        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([userId], [round], [], applyGuestEligibilityRules: false);

        Assert.That(totals[userId], Is.EqualTo(new[] { 6 }));
    }

    [Test]
    public async Task REQ1505_GetPerRoundTotalsByUserIdsAsync_UserNeverAttempted_AbsentFromResult()
    {
        var participantId = Guid.NewGuid();
        var neverAttemptedId = Guid.NewGuid();
        var instanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(instanceId, participantId, streakLength: 4, hasEnded: true);
        var round = ClosedHigherLowerRound(instanceId);

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync(
            [participantId, neverAttemptedId], [round], [], applyGuestEligibilityRules: false);

        Assert.That(totals.ContainsKey(neverAttemptedId), Is.False, "absent, not defaulted to an empty list");
    }

    [Test]
    public async Task REQ1505_GetPerRoundTotalsByUserIdsAsync_RequestedUserIdsFilter_ExcludesParticipantsNotInTheList()
    {
        var requestedUserId = Guid.NewGuid();
        var otherParticipantId = Guid.NewGuid();
        var instanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(instanceId, requestedUserId, streakLength: 3, hasEnded: true);
        await SaveAttemptAsync(instanceId, otherParticipantId, streakLength: 5, hasEnded: true);
        var round = ClosedHigherLowerRound(instanceId);

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([requestedUserId], [round], [], applyGuestEligibilityRules: false);

        Assert.That(totals.Keys, Is.EqualTo(new[] { requestedUserId }));
    }

    [Test]
    public async Task REQ1505_GetPerRoundTotalsByUserIdsAsync_ClosedRoundsForAnotherGameKey_NeverContributeAnything()
    {
        var userId = Guid.NewGuid();
        var instanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(instanceId, userId, streakLength: 7, hasEnded: true);
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = "xg-grid", // deliberately NOT "xg-higher-lower".
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
    public async Task REQ1505_GetPerRoundTotalsByUserIdsAsync_MultipleClosedRounds_OneListEntryPerRoundInOrderSupplied()
    {
        var userId = Guid.NewGuid();
        var firstInstanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(firstInstanceId, userId, streakLength: 9, hasEnded: true);
        var secondInstanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(secondInstanceId, userId, streakLength: 2, hasEnded: true);
        var firstRound = ClosedHigherLowerRound(firstInstanceId);
        var secondRound = ClosedHigherLowerRound(secondInstanceId);

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync(
            [userId], [firstRound, secondRound], [], applyGuestEligibilityRules: false);

        Assert.That(totals[userId], Is.EqualTo(new[] { 9, 2 }));
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
        var instanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(instanceId, guestId, streakLength: 8, hasEnded: true);
        var round = ClosedHigherLowerRound(instanceId);

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
        var instanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(instanceId, guestId, streakLength: 8, hasEnded: true);
        var round = ClosedHigherLowerRound(instanceId);

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([guestId], [round], [guest], applyGuestEligibilityRules: false);

        Assert.That(totals[guestId], Is.EqualTo(new[] { 8 }));
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
        var instanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(instanceId, userId, streakLength: 5, hasEnded: true);
        // Closed strictly BEFORE claiming.
        var round = ClosedHigherLowerRound(instanceId, closedAt: claimedAt.AddDays(-1));

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
        var instanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(instanceId, userId, streakLength: 5, hasEnded: true);
        // Closed strictly AFTER claiming.
        var round = ClosedHigherLowerRound(instanceId, closedAt: claimedAt.AddDays(1));

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([userId], [round], [claimedUser]);

        Assert.That(totals[userId], Is.EqualTo(new[] { 5 }));
    }

    [Test]
    public async Task REQ1505_GetPerRoundTotalsByUserIdsAsync_RoundWithZeroParticipants_ContributesNothingToAnyone()
    {
        var userId = Guid.NewGuid();
        var instanceId = await SeedInstanceAsync();
        var round = ClosedHigherLowerRound(instanceId);

        var totals = await _source.GetPerRoundTotalsByUserIdsAsync([userId], [round], []);

        Assert.That(totals, Is.Empty);
    }

    // ---- GetActiveRoundTotalsByUserIdAsync (REQ-406/407/1505) -----------

    [Test]
    public async Task REQ1505_GetActiveRoundTotalsByUserIdAsync_InProgressNotYetEndedAttempt_StillContributesCurrentStreak()
    {
        // REQ-1504/1505: StreakLength is a live "current progress" value the
        // instant an attempt row exists — an in-progress attempt (HasEnded
        // = false) contributes its current streak exactly the same as an
        // ended one, unlike xg-predict's own "nothing graded yet" gap.
        var userId = Guid.NewGuid();
        var instanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(instanceId, userId, streakLength: 3, hasEnded: false);
        var activeRound = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = XGHigherLowerGameModule.XGHigherLowerGameKey,
            GameInstanceId = instanceId,
            SequenceNumber = 1,
            StartTime = Now.AddDays(-1),
            EndTime = Now.AddDays(6),
            AllowGuessChange = true,
        };

        var totals = await _source.GetActiveRoundTotalsByUserIdAsync(activeRound);

        Assert.That(totals[userId], Is.EqualTo(3));
    }

    [Test]
    public async Task ADR0100_GetActiveRoundTotalsByUserIdAsync_SameReadAsClosedRoundScope_NoSeparateLiveFormula()
    {
        // ADR-0100 §4: the exact same current-streak total, whether the
        // round is still active or already closed — no separate "live"
        // formula.
        var userId = Guid.NewGuid();
        var instanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(instanceId, userId, streakLength: 10, hasEnded: true);
        var activeRound = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = XGHigherLowerGameModule.XGHigherLowerGameKey,
            GameInstanceId = instanceId,
            SequenceNumber = 1,
            StartTime = Now.AddDays(-1),
            EndTime = Now.AddDays(6),
            AllowGuessChange = true,
        };

        var totals = await _source.GetActiveRoundTotalsByUserIdAsync(activeRound);

        Assert.That(totals[userId], Is.EqualTo(10));
    }

    [Test]
    public async Task REQ407_GetActiveRoundTotalsByUserIdAsync_UserNeverAttempted_AbsentNotZero()
    {
        var userId = Guid.NewGuid();
        var instanceId = await SeedInstanceAsync();
        var activeRound = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = XGHigherLowerGameModule.XGHigherLowerGameKey,
            GameInstanceId = instanceId,
            SequenceNumber = 1,
            StartTime = Now.AddDays(-1),
            EndTime = Now.AddDays(6),
            AllowGuessChange = true,
        };

        var totals = await _source.GetActiveRoundTotalsByUserIdAsync(activeRound);

        Assert.That(totals.ContainsKey(userId), Is.False, "a never-attempted participant contributes no key at all");
    }

    // ---- GetTotalsByRoundAsync (REQ-408/1505) ----------------------------

    [Test]
    public async Task REQ1505_GetTotalsByRoundAsync_ClosedRound_ReturnsStreakLengthsPerUser()
    {
        var firstUserId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        var instanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(instanceId, firstUserId, streakLength: 9, hasEnded: true);
        await SaveAttemptAsync(instanceId, secondUserId, streakLength: 0, hasEnded: true);
        var round = ClosedHigherLowerRound(instanceId);

        var totals = await _source.GetTotalsByRoundAsync(round);

        Assert.That(totals[firstUserId], Is.EqualTo(9));
        Assert.That(totals[secondUserId], Is.EqualTo(0));
    }

    // ---- GetTotalsByRoundsAsync (REQ-405/1505) ---------------------------

    [Test]
    public async Task REQ1505_GetTotalsByRoundsAsync_SumsStreakLengthsAcrossEveryRoundSupplied()
    {
        var userId = Guid.NewGuid();
        var firstInstanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(firstInstanceId, userId, streakLength: 9, hasEnded: true);
        var secondInstanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(secondInstanceId, userId, streakLength: 4, hasEnded: true);
        var firstRound = ClosedHigherLowerRound(firstInstanceId);
        var secondRound = ClosedHigherLowerRound(secondInstanceId);

        var totals = await _source.GetTotalsByRoundsAsync([firstRound, secondRound]);

        Assert.That(totals[userId], Is.EqualTo(13));
    }

    [Test]
    public async Task REQ1505_GetTotalsByRoundsAsync_RoundWithNoAttempts_ContributesZeroNotAbsent()
    {
        var userId = Guid.NewGuid();
        var attemptedInstanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(attemptedInstanceId, userId, streakLength: 9, hasEnded: true);
        var emptyInstanceId = await SeedInstanceAsync();
        var attemptedRound = ClosedHigherLowerRound(attemptedInstanceId);
        var emptyRound = ClosedHigherLowerRound(emptyInstanceId);

        var totals = await _source.GetTotalsByRoundsAsync([attemptedRound, emptyRound]);

        Assert.That(totals[userId], Is.EqualTo(9), "the round the user never attempted contributes 0, same as SUM(StreakLength ?? 0)");
    }

    // ---- HasAnyParticipantAsync (REQ-305) --------------------------------

    [Test]
    public async Task REQ305_HasAnyParticipantAsync_InstanceWithAttempt_ReturnsTrue()
    {
        var instanceId = await SeedInstanceAsync();
        await SaveAttemptAsync(instanceId, Guid.NewGuid(), streakLength: 1, hasEnded: false);
        var round = ClosedHigherLowerRound(instanceId);

        var hasAnyParticipant = await _source.HasAnyParticipantAsync(round);

        Assert.That(hasAnyParticipant, Is.True);
    }

    [Test]
    public async Task REQ305_HasAnyParticipantAsync_InstanceWithNoAttempts_ReturnsFalse()
    {
        // This is the exact case RoundGenerationService's own bug (routing
        // this question through IGuessRepository directly instead of this
        // GameKey's own IRoundScoreSource) would have gotten wrong —
        // "xg-higher-lower" never writes a Guess row at all, so that
        // earlier check would have always read zero regardless of this
        // instance's real HigherLowerAttempt state.
        var instanceId = await SeedInstanceAsync();
        var round = ClosedHigherLowerRound(instanceId);

        var hasAnyParticipant = await _source.HasAnyParticipantAsync(round);

        Assert.That(hasAnyParticipant, Is.False);
    }

    // ---- helpers ----------------------------------------------------------

    private async Task<Guid> SeedInstanceAsync()
    {
        var instanceId = Guid.NewGuid();
        var instance = new HigherLowerInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            StatCategory = "trophy",
            BaselinePlayerId = Guid.NewGuid(),
            BaselineValue = 1,
            Comparators = [],
        };
        await _repository.AddInstanceAsync(instance);
        return instanceId;
    }

    private async Task SaveAttemptAsync(Guid instanceId, Guid userId, int streakLength, bool hasEnded) =>
        await _repository.SaveAttemptAsync(
            instanceId, userId, streakLength, currentBaselinePlayerId: Guid.NewGuid(), currentBaselineValue: 0, hasEnded);

    // A closed "xg-higher-lower" Round pointing at the given
    // HigherLowerInstance — never persisted via IRoundRepository
    // (HigherLowerRoundScoreSource must never inject it, ADR-0100's "For AI
    // agents" rule), same shape LeaderboardService would resolve and hand
    // in.
    private static Round ClosedHigherLowerRound(Guid higherLowerInstanceId, DateTime? closedAt = null)
    {
        var closedAtValue = closedAt ?? Now.AddDays(-1);
        return new Round
        {
            Id = Guid.NewGuid(),
            GameKey = XGHigherLowerGameModule.XGHigherLowerGameKey,
            GameInstanceId = higherLowerInstanceId,
            SequenceNumber = 1,
            StartTime = closedAtValue.AddDays(-1),
            EndTime = closedAtValue,
            AllowGuessChange = true,
            ClosedAt = closedAtValue,
        };
    }
}

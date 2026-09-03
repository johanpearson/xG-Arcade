using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// Games.XGConnect (COMP-17)/ADR-0103, S-208: ConnectMatchRepository's basic
// persistence round-trips for ConnectMatch/ConnectTargetPick/
// ConnectChainStep, plus (S-212/REQ-1405) the match-start/forfeit-timer/
// resolution primitives StartMatchAsync/MarkPlayerTimedOutAsync/
// ResolveMatchAsync/GetActiveMatchesPastDeadlineAsync. Pure persistence
// only — no trivial-pair rejection, live overlap validation, or bust/
// scoring/chain-completion logic (that's S-211/S-213/S-214; those
// primitives' own business-logic orchestration is
// ConnectTargetPickService/ConnectMatchLifecycleService's job, covered by
// their own test files).
public class ConnectMatchRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IConnectMatchRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new ConnectMatchRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    // ---- ConnectMatch CRUD ----------------------------------------------

    [Test]
    public async Task AddMatchAsync_ThenGetMatchByIdAsync_PersistsAndRetrievesTheRow()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var createdAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var match = new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = playerA,
            PlayerBUserId = playerB,
            CreatedAt = createdAt,
        };

        var added = await _repository.AddMatchAsync(match);

        Assert.That(added, Is.SameAs(match));
        var result = await _repository.GetMatchByIdAsync(match.Id);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.PlayerAUserId, Is.EqualTo(playerA));
        Assert.That(result.PlayerBUserId, Is.EqualTo(playerB));
        Assert.That(result.Status, Is.EqualTo(ConnectMatchStatus.AwaitingTargetPicks), "Status defaults to AwaitingTargetPicks");
        Assert.That(result.Outcome, Is.EqualTo(ConnectMatchOutcome.Pending), "Outcome defaults to Pending");
        Assert.That(result.StartedAt, Is.Null);
        Assert.That(result.DeadlineUtc, Is.Null);
        Assert.That(result.ResolvedAt, Is.Null);
    }

    [Test]
    public async Task GetMatchByIdAsync_UnknownId_ReturnsNull()
    {
        var result = await _repository.GetMatchByIdAsync(Guid.NewGuid());

        Assert.That(result, Is.Null);
    }

    // ---- REQ-1405/S-212: match-start/forfeit-timer/resolution primitives ----

    [Test]
    public async Task REQ1405_StartMatchAsync_SetsStatusActiveAndStartedAtAndDeadlineUtc()
    {
        var match = await _repository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            CreatedAt = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc),
        });
        var startedAt = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        var deadlineUtc = startedAt.AddHours(6);

        var result = await _repository.StartMatchAsync(match.Id, startedAt, deadlineUtc);

        Assert.That(result.Status, Is.EqualTo(ConnectMatchStatus.Active));
        Assert.That(result.StartedAt, Is.EqualTo(startedAt));
        Assert.That(result.DeadlineUtc, Is.EqualTo(deadlineUtc));
        var stored = await _repository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.Active));
    }

    [Test]
    public async Task REQ1405_MarkPlayerTimedOutAsync_UnsetSlot_SetsThatSlotOnly()
    {
        var match = await _repository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            CreatedAt = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc),
        });
        var timedOutAt = new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc);

        var result = await _repository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: true, timedOutAt);

        Assert.That(result.PlayerATimedOutAt, Is.EqualTo(timedOutAt));
        Assert.That(result.PlayerBTimedOutAt, Is.Null, "only the requested slot is set");
    }

    [Test]
    public async Task REQ1405_MarkPlayerTimedOutAsync_AlreadySetSlot_IsIdempotentAndNeverOverwrites()
    {
        var match = await _repository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            CreatedAt = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc),
        });
        var firstTimedOutAt = new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc);
        await _repository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: true, firstTimedOutAt);

        var laterTimedOutAt = firstTimedOutAt.AddHours(1);
        var result = await _repository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: true, laterTimedOutAt);

        Assert.That(result.PlayerATimedOutAt, Is.EqualTo(firstTimedOutAt), "a later call must never overwrite an already-set timeout timestamp");
    }

    [Test]
    public async Task REQ1405_ResolveMatchAsync_SetsStatusResolvedOutcomeAndResolvedAt()
    {
        var match = await _repository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            CreatedAt = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc),
        });
        var resolvedAt = new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc);

        var result = await _repository.ResolveMatchAsync(match.Id, ConnectMatchOutcome.Draw, resolvedAt);

        Assert.That(result.Status, Is.EqualTo(ConnectMatchStatus.Resolved));
        Assert.That(result.Outcome, Is.EqualTo(ConnectMatchOutcome.Draw));
        Assert.That(result.ResolvedAt, Is.EqualTo(resolvedAt));
    }

    [Test]
    public async Task REQ1405_GetActiveMatchesPastDeadlineAsync_ReturnsOnlyActiveMatchesWithPassedDeadline()
    {
        var now = new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc);

        var pastDeadlineActive = await _repository.AddMatchAsync(new ConnectMatch { Id = Guid.NewGuid(), CreatedAt = now.AddHours(-7) });
        await _repository.StartMatchAsync(pastDeadlineActive.Id, now.AddHours(-7), now.AddHours(-1));

        var notYetPastDeadlineActive = await _repository.AddMatchAsync(new ConnectMatch { Id = Guid.NewGuid(), CreatedAt = now.AddHours(-1) });
        await _repository.StartMatchAsync(notYetPastDeadlineActive.Id, now.AddHours(-1), now.AddHours(5));

        // Awaiting picks, never started — DeadlineUtc is null, must never
        // be swept even though it's old.
        await _repository.AddMatchAsync(new ConnectMatch { Id = Guid.NewGuid(), CreatedAt = now.AddDays(-1) });

        // Already resolved, deadline was in the past — must not be swept
        // again.
        var alreadyResolved = await _repository.AddMatchAsync(new ConnectMatch { Id = Guid.NewGuid(), CreatedAt = now.AddHours(-7) });
        await _repository.StartMatchAsync(alreadyResolved.Id, now.AddHours(-7), now.AddHours(-1));
        await _repository.ResolveMatchAsync(alreadyResolved.Id, ConnectMatchOutcome.Draw, now.AddHours(-1));

        var result = await _repository.GetActiveMatchesPastDeadlineAsync(now);

        Assert.That(result.Select(m => m.Id), Is.EquivalentTo(new[] { pastDeadlineActive.Id }));
    }

    // ---- ConnectTargetPick CRUD (AddOrUpdateTargetPickAsync) ------------

    [Test]
    public async Task AddOrUpdateTargetPickAsync_NoExistingRow_InsertsNewPick()
    {
        var matchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var targetPlayerId = Guid.NewGuid();
        var selectedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        var pick = await _repository.AddOrUpdateTargetPickAsync(matchId, userId, targetPlayerId, selectedAt);

        Assert.That(pick.ConnectMatchId, Is.EqualTo(matchId));
        Assert.That(pick.UserId, Is.EqualTo(userId));
        Assert.That(pick.TargetPlayerId, Is.EqualTo(targetPlayerId));
        Assert.That(pick.SelectedAt, Is.EqualTo(selectedAt));
        Assert.That(pick.IsLocked, Is.False);

        var stored = await _repository.GetTargetPickAsync(matchId, userId);
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!.TargetPlayerId, Is.EqualTo(targetPlayerId));
    }

    [Test]
    public async Task AddOrUpdateTargetPickAsync_ExistingRowForSameMatchAndUser_ReplacesValueRatherThanInsertingSecondRow()
    {
        var matchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var firstTarget = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        await _repository.AddOrUpdateTargetPickAsync(matchId, userId, firstTarget, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));

        await _repository.AddOrUpdateTargetPickAsync(matchId, userId, secondTarget, new DateTime(2026, 9, 1, 13, 0, 0, DateTimeKind.Utc));

        var stored = await _repository.GetTargetPickAsync(matchId, userId);
        Assert.That(stored!.TargetPlayerId, Is.EqualTo(secondTarget));
        Assert.That(await _dbContext.ConnectTargetPicks.CountAsync(p => p.ConnectMatchId == matchId && p.UserId == userId),
            Is.EqualTo(1), "a resubmission must overwrite the existing row, never insert a second one (REQ-1404/1405)");
    }

    [Test]
    public async Task GetTargetPickAsync_NoStoredPick_ReturnsNull()
    {
        var result = await _repository.GetTargetPickAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetTargetPicksForMatchAsync_ReturnsBothPlayersPicksForThatMatch()
    {
        var matchId = Guid.NewGuid();
        var otherMatchId = Guid.NewGuid();
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        await _repository.AddOrUpdateTargetPickAsync(matchId, playerA, Guid.NewGuid(), DateTime.UtcNow);
        await _repository.AddOrUpdateTargetPickAsync(matchId, playerB, Guid.NewGuid(), DateTime.UtcNow);
        await _repository.AddOrUpdateTargetPickAsync(otherMatchId, Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);

        var result = await _repository.GetTargetPicksForMatchAsync(matchId);

        Assert.That(result.Select(p => p.UserId), Is.EquivalentTo(new Guid?[] { playerA, playerB }));
    }

    // ---- ConnectChainStep CRUD -------------------------------------------

    [Test]
    public async Task AddChainStepAsync_ThenGetChainStepsForMatchAndUserAsync_PersistsAndRetrievesTheRow()
    {
        var matchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var candidatePlayerId = Guid.NewGuid();
        var submittedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var chainStep = new ConnectChainStep
        {
            Id = Guid.NewGuid(),
            ConnectMatchId = matchId,
            UserId = userId,
            Position = 1,
            AttemptNumber = 1,
            CandidatePlayerId = candidatePlayerId,
            ClaimedClubName = "Arsenal",
            IsValid = true,
            ClosesChain = false,
            SubmittedAt = submittedAt,
        };

        var added = await _repository.AddChainStepAsync(chainStep);

        Assert.That(added, Is.SameAs(chainStep));
        var result = await _repository.GetChainStepsForMatchAndUserAsync(matchId, userId);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CandidatePlayerId, Is.EqualTo(candidatePlayerId));
        Assert.That(result[0].ClaimedClubName, Is.EqualTo("Arsenal"));
        Assert.That(result[0].IsValid, Is.True);
    }

    // REQ-1406/1407: a failed first attempt and a successful retry at the
    // same position are both legitimate, distinct rows — never overwritten.
    [Test]
    public async Task AddChainStepAsync_FailedFirstAttemptThenRetryAtSamePosition_BothRowsPersist()
    {
        var matchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var failedAttempt = new ConnectChainStep
        {
            Id = Guid.NewGuid(), ConnectMatchId = matchId, UserId = userId, Position = 1, AttemptNumber = 1,
            CandidatePlayerId = Guid.NewGuid(), ClaimedClubName = "Wrong Club", IsValid = false, ClosesChain = false, SubmittedAt = DateTime.UtcNow,
        };
        var retryAttempt = new ConnectChainStep
        {
            Id = Guid.NewGuid(), ConnectMatchId = matchId, UserId = userId, Position = 1, AttemptNumber = 2,
            CandidatePlayerId = Guid.NewGuid(), ClaimedClubName = "Right Club", IsValid = true, ClosesChain = false, SubmittedAt = DateTime.UtcNow,
        };

        await _repository.AddChainStepAsync(failedAttempt);
        await _repository.AddChainStepAsync(retryAttempt);

        var result = await _repository.GetChainStepsForMatchAndUserAsync(matchId, userId);
        Assert.That(result, Has.Count.EqualTo(2), "a failed first attempt and a successful retry are both legitimate, distinct rows");
        Assert.That(result.Select(s => s.AttemptNumber), Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public async Task GetChainStepsForMatchAndUserAsync_IsScopedToOneMatchAndOneUser()
    {
        var matchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await _repository.AddChainStepAsync(new ConnectChainStep
        {
            Id = Guid.NewGuid(), ConnectMatchId = matchId, UserId = userId, Position = 1, AttemptNumber = 1,
            CandidatePlayerId = Guid.NewGuid(), ClaimedClubName = "Club", IsValid = true, ClosesChain = false, SubmittedAt = DateTime.UtcNow,
        });
        await _repository.AddChainStepAsync(new ConnectChainStep
        {
            Id = Guid.NewGuid(), ConnectMatchId = matchId, UserId = otherUserId, Position = 1, AttemptNumber = 1,
            CandidatePlayerId = Guid.NewGuid(), ClaimedClubName = "Club", IsValid = true, ClosesChain = false, SubmittedAt = DateTime.UtcNow,
        });

        var result = await _repository.GetChainStepsForMatchAndUserAsync(matchId, userId);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].UserId, Is.EqualTo(userId));
    }

    [Test]
    public async Task GetChainStepsForMatchAndUserAsync_NoSteps_ReturnsEmpty()
    {
        var result = await _repository.GetChainStepsForMatchAndUserAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.That(result, Is.Empty);
    }
}

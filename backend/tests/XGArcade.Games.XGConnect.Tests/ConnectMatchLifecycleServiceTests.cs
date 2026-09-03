using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.TestSupport;

namespace XGArcade.Games.XGConnect.Tests;

// REQ-1405 (docs/requirements-document.md §4.15): match start, 6h forfeit
// timer, and resolution scaffolding. REQ-1407/1408/1409/S-214: bust-tracking
// primitives, the mixed-outcome-aware forfeit sweep, and
// TryResolveMatchIfBothTerminalAsync's own win/draw/forfeit resolution.
// Same real-InMemory-backed-repository, no-mocking-framework pattern as
// ConnectTargetPickServiceTests — IConnectMatchRepository is exercised
// through the real ConnectMatchRepository against an InMemory-backed
// XGArcadeDbContext; IConnectScoringService is the real ConnectScoringService
// (a pure calculation with no external dependency of its own, so no fake is
// needed — it gets its own dedicated, direct coverage in
// ConnectScoringServiceTests.cs).
public class ConnectMatchLifecycleServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private XGArcadeDbContext _dbContext = null!;
    private IConnectMatchRepository _connectMatchRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _connectMatchRepository = new ConnectMatchRepository(_dbContext);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private ConnectMatchLifecycleService BuildService(DateTimeOffset now) =>
        new(_connectMatchRepository, new ConnectScoringService(), new FixedTimeProvider(now));

    // A single ConnectChainStep row for the given slot's UserId —
    // CandidatePlayerId is a fresh Guid with no corresponding Player row,
    // matching ConnectMatchRepositoryTests' own precedent that the InMemory
    // provider doesn't enforce this FK for direct repository-level test
    // setup.
    private async Task AddStepAsync(
        Guid matchId, Guid? userId, int position, int attemptNumber, bool isValid, bool closesChain, DateTime submittedAt) =>
        await _connectMatchRepository.AddChainStepAsync(new ConnectChainStep
        {
            Id = Guid.NewGuid(), ConnectMatchId = matchId, UserId = userId, Position = position, AttemptNumber = attemptNumber,
            CandidatePlayerId = Guid.NewGuid(), ClaimedClubName = "Club", IsValid = isValid, ClosesChain = closesChain, SubmittedAt = submittedAt,
        });

    // The minimal "this player completed a 1-connector, zero-penalty chain"
    // fixture (score 1) used by several REQ-1409 tests below.
    private Task AddClosingStepAsync(Guid matchId, Guid? userId, int position, DateTime submittedAt) =>
        AddStepAsync(matchId, userId, position, attemptNumber: 1, isValid: true, closesChain: true, submittedAt: submittedAt);

    private async Task<ConnectMatch> CreateMatchAsync(Guid playerAUserId, Guid playerBUserId, DateTime createdAt) =>
        await _connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = playerAUserId,
            PlayerBUserId = playerBUserId,
            CreatedAt = createdAt,
        });

    // ---- StartMatchIfBothPicksLockedAsync -----------------------------------

    [Test]
    public async Task REQ1405_StartMatchIfBothPicksLockedAsync_FewerThanTwoPicks_NoOps()
    {
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, match.PlayerAUserId, Guid.NewGuid(), FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        await service.StartMatchIfBothPicksLockedAsync(match.Id);

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.AwaitingTargetPicks));
        Assert.That(stored.StartedAt, Is.Null);
        Assert.That(stored.DeadlineUtc, Is.Null);
    }

    [Test]
    public async Task REQ1405_StartMatchIfBothPicksLockedAsync_TwoPicksButNotBothLocked_NoOps()
    {
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, match.PlayerAUserId, Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, match.PlayerBUserId, Guid.NewGuid(), FixedNow.UtcDateTime);
        // Deliberately NOT calling LockTargetPicksForMatchAsync — both picks
        // exist but neither is locked.
        var service = BuildService(FixedNow);

        await service.StartMatchIfBothPicksLockedAsync(match.Id);

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.AwaitingTargetPicks));
        Assert.That(stored.StartedAt, Is.Null);
    }

    [Test]
    public async Task REQ1405_StartMatchIfBothPicksLockedAsync_BothPicksLocked_StartsMatchWithSixHourDeadline()
    {
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, match.PlayerAUserId, Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, match.PlayerBUserId, Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.LockTargetPicksForMatchAsync(match.Id);
        var service = BuildService(FixedNow);

        await service.StartMatchIfBothPicksLockedAsync(match.Id);

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.Active));
        Assert.That(stored.StartedAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(stored.DeadlineUtc, Is.EqualTo(FixedNow.UtcDateTime.AddHours(6)));
    }

    // ---- RunForfeitSweepAsync ------------------------------------------------

    [Test]
    public async Task REQ1405_RunForfeitSweepAsync_MatchPastDeadlineNeitherPlayerTerminal_ForfeitsBothAndResolvesInOneCall()
    {
        var startedAt = FixedNow.UtcDateTime.AddHours(-7);
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), startedAt);
        await _connectMatchRepository.StartMatchAsync(match.Id, startedAt, startedAt.AddHours(6));
        var service = BuildService(FixedNow);

        var result = await service.RunForfeitSweepAsync();

        Assert.That(result.PlayersForfeited, Is.EqualTo(2));
        Assert.That(result.MatchesResolved, Is.EqualTo(1), "both slots reached terminal in this same sweep call — resolution must not wait for a second pass");

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.PlayerATimedOutAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(stored.PlayerBTimedOutAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(stored.Status, Is.EqualTo(ConnectMatchStatus.Resolved));
        Assert.That(stored.Outcome, Is.EqualTo(ConnectMatchOutcome.Draw));
        Assert.That(stored.ResolvedAt, Is.EqualTo(FixedNow.UtcDateTime));
    }

    [Test]
    public async Task REQ1405_RunForfeitSweepAsync_MatchNotYetPastDeadline_LeavesItUntouched()
    {
        var startedAt = FixedNow.UtcDateTime.AddHours(-1);
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), startedAt);
        await _connectMatchRepository.StartMatchAsync(match.Id, startedAt, startedAt.AddHours(6));
        var service = BuildService(FixedNow);

        var result = await service.RunForfeitSweepAsync();

        Assert.That(result.PlayersForfeited, Is.EqualTo(0));
        Assert.That(result.MatchesResolved, Is.EqualTo(0));

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.Active));
        Assert.That(stored.PlayerATimedOutAt, Is.Null);
        Assert.That(stored.PlayerBTimedOutAt, Is.Null);
        Assert.That(stored.Outcome, Is.EqualTo(ConnectMatchOutcome.Pending));
    }

    // REQ-1405 GWT#2/#3: independent per-player enforcement — a slot already
    // terminal (seeded directly through the repository, simulating an
    // earlier-reached terminal state) is left alone, and the still-active
    // slot is swept and the match resolved immediately in the SAME sweep
    // call, never deferred to a later pass.
    [Test]
    public async Task REQ1405_RunForfeitSweepAsync_PlayerAAlreadyTerminal_MarksPlayerBAndResolvesImmediatelyInSameCall()
    {
        var startedAt = FixedNow.UtcDateTime.AddHours(-7);
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), startedAt);
        await _connectMatchRepository.StartMatchAsync(match.Id, startedAt, startedAt.AddHours(6));
        var earlierTerminalAt = FixedNow.UtcDateTime.AddHours(-2);
        await _connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: true, earlierTerminalAt);
        var service = BuildService(FixedNow);

        var result = await service.RunForfeitSweepAsync();

        Assert.That(result.PlayersForfeited, Is.EqualTo(1), "player A was already terminal — only player B's slot is newly forfeited this call");
        Assert.That(result.MatchesResolved, Is.EqualTo(1));

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        // Player A's original timestamp is untouched — idempotent, never
        // overwritten by this later sweep pass.
        Assert.That(stored!.PlayerATimedOutAt, Is.EqualTo(earlierTerminalAt));
        Assert.That(stored.PlayerBTimedOutAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(stored.Status, Is.EqualTo(ConnectMatchStatus.Resolved));
        Assert.That(stored.Outcome, Is.EqualTo(ConnectMatchOutcome.Draw));
        Assert.That(stored.ResolvedAt, Is.EqualTo(FixedNow.UtcDateTime));
    }

    // REQ-1405 GWT#3: resolution never happens with only one side terminal —
    // this constructs that intermediate state directly through the
    // repository (the sweep itself always evaluates both slots together for
    // a match once it's past deadline, so this state can't be observed
    // through the sweep alone) and asserts the match is untouched by a
    // sweep whose OWN match set doesn't include it (not yet past deadline).
    [Test]
    public async Task REQ1405_RunForfeitSweepAsync_OnlyOneSideTerminalAndNotPastDeadline_StatusStaysActiveOutcomeStaysPending()
    {
        var startedAt = FixedNow.UtcDateTime.AddHours(-1);
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), startedAt);
        await _connectMatchRepository.StartMatchAsync(match.Id, startedAt, startedAt.AddHours(6));
        await _connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: true, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.RunForfeitSweepAsync();

        Assert.That(result.MatchesResolved, Is.EqualTo(0), "only one side is terminal and the deadline hasn't passed — never resolved");

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.Active));
        Assert.That(stored.Outcome, Is.EqualTo(ConnectMatchOutcome.Pending));
        Assert.That(stored.PlayerATimedOutAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(stored.PlayerBTimedOutAt, Is.Null);
    }

    // REQ-1409/S-214: the "mixed-outcome" gap RunForfeitSweepAsync's own doc
    // comment used to flag as out of scope — a player who already completed
    // their chain before the deadline must never be marked timed-out just
    // because the shared deadline has now passed.
    [Test]
    public async Task REQ1409_RunForfeitSweepAsync_PlayerCompletedChainBeforeDeadline_DoesNotMarkThatSlotTimedOut_ResolvesAsCompleterWin()
    {
        var startedAt = FixedNow.UtcDateTime.AddHours(-7);
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), startedAt);
        await _connectMatchRepository.StartMatchAsync(match.Id, startedAt, startedAt.AddHours(6));
        await AddClosingStepAsync(match.Id, match.PlayerAUserId, position: 1, submittedAt: startedAt.AddHours(1));
        var service = BuildService(FixedNow);

        var result = await service.RunForfeitSweepAsync();

        Assert.That(result.PlayersForfeited, Is.EqualTo(1), "player A already completed their chain — only player B's slot is newly forfeited");
        Assert.That(result.MatchesResolved, Is.EqualTo(1));

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.PlayerATimedOutAt, Is.Null, "a player who already completed their chain must never be marked timed out");
        Assert.That(stored.PlayerBTimedOutAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(stored.Status, Is.EqualTo(ConnectMatchStatus.Resolved));
        Assert.That(stored.Outcome, Is.EqualTo(ConnectMatchOutcome.PlayerAWin));
        Assert.That(stored.PlayerAScore, Is.EqualTo(1));
        Assert.That(stored.PlayerBScore, Is.Null);
    }

    // REQ-1407/1409/S-214: a player already busted before the deadline must
    // never also be marked timed out — the sweep only touches the OTHER
    // (still-active) slot, and the match resolves as a draw once both are
    // terminal via any mix of bust/timeout.
    [Test]
    public async Task REQ1409_RunForfeitSweepAsync_PlayerAlreadyBustedBeforeDeadline_MarksOnlyOtherSlot_ResolvesDraw()
    {
        var startedAt = FixedNow.UtcDateTime.AddHours(-7);
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), startedAt);
        await _connectMatchRepository.StartMatchAsync(match.Id, startedAt, startedAt.AddHours(6));
        var bustedAt = FixedNow.UtcDateTime.AddHours(-2);
        await _connectMatchRepository.MarkPlayerBustedAsync(match.Id, isPlayerA: true, bustedAt);
        var service = BuildService(FixedNow);

        var result = await service.RunForfeitSweepAsync();

        Assert.That(result.PlayersForfeited, Is.EqualTo(1), "player A was already busted — only player B's slot is newly forfeited");
        Assert.That(result.MatchesResolved, Is.EqualTo(1));

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.PlayerABustedAt, Is.EqualTo(bustedAt));
        Assert.That(stored.PlayerATimedOutAt, Is.Null, "a busted slot must never also be marked timed out");
        Assert.That(stored.PlayerBTimedOutAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(stored.Outcome, Is.EqualTo(ConnectMatchOutcome.Draw));
        Assert.That(stored.PlayerAScore, Is.Null);
        Assert.That(stored.PlayerBScore, Is.Null);
    }

    // ---- TryResolveMatchIfBothTerminalAsync -----------------------------------

    [Test]
    public async Task REQ1409_TryResolveMatchIfBothTerminalAsync_BothCompletedStrictlyLowerScoreWins()
    {
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(match.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        // Player A: one connector, no penalties — score 1.
        await AddClosingStepAsync(match.Id, match.PlayerAUserId, position: 1, submittedAt: FixedNow.UtcDateTime);
        // Player B: two connectors — score 2.
        await AddStepAsync(match.Id, match.PlayerBUserId, position: 1, attemptNumber: 1, isValid: true, closesChain: false, submittedAt: FixedNow.UtcDateTime);
        await AddClosingStepAsync(match.Id, match.PlayerBUserId, position: 2, submittedAt: FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var resolved = await service.TryResolveMatchIfBothTerminalAsync(match.Id);

        Assert.That(resolved, Is.True);
        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Outcome, Is.EqualTo(ConnectMatchOutcome.PlayerAWin));
        Assert.That(stored.PlayerAScore, Is.EqualTo(1));
        Assert.That(stored.PlayerBScore, Is.EqualTo(2));
        Assert.That(stored.Status, Is.EqualTo(ConnectMatchStatus.Resolved));
        Assert.That(stored.ResolvedAt, Is.EqualTo(FixedNow.UtcDateTime));
    }

    [Test]
    public async Task REQ1409_TryResolveMatchIfBothTerminalAsync_BothCompletedEqualScores_ResolvesDraw()
    {
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(match.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        await AddClosingStepAsync(match.Id, match.PlayerAUserId, position: 1, submittedAt: FixedNow.UtcDateTime);
        await AddClosingStepAsync(match.Id, match.PlayerBUserId, position: 1, submittedAt: FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var resolved = await service.TryResolveMatchIfBothTerminalAsync(match.Id);

        Assert.That(resolved, Is.True);
        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Outcome, Is.EqualTo(ConnectMatchOutcome.Draw));
        Assert.That(stored.PlayerAScore, Is.EqualTo(1));
        Assert.That(stored.PlayerBScore, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ1409_TryResolveMatchIfBothTerminalAsync_OneCompletedOtherForfeited_CompleterWinsRegardlessOfScore()
    {
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(match.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        // Player B completes with a high (bad) score — still wins outright,
        // no minimum score required to win by forfeit.
        await AddStepAsync(match.Id, match.PlayerBUserId, position: 1, attemptNumber: 1, isValid: false, closesChain: false, submittedAt: FixedNow.UtcDateTime);
        await AddStepAsync(match.Id, match.PlayerBUserId, position: 1, attemptNumber: 2, isValid: true, closesChain: false, submittedAt: FixedNow.UtcDateTime);
        await AddClosingStepAsync(match.Id, match.PlayerBUserId, position: 2, submittedAt: FixedNow.UtcDateTime);
        // Player A busted — never completed.
        await _connectMatchRepository.MarkPlayerBustedAsync(match.Id, isPlayerA: true, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var resolved = await service.TryResolveMatchIfBothTerminalAsync(match.Id);

        Assert.That(resolved, Is.True);
        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Outcome, Is.EqualTo(ConnectMatchOutcome.PlayerBWin));
        Assert.That(stored.PlayerAScore, Is.Null, "a forfeiting player has no valid score");
        Assert.That(stored.PlayerBScore, Is.EqualTo(3), "1 penalty (first-attempt failure) + 2 connectors");
    }

    [Test]
    public async Task REQ1409_TryResolveMatchIfBothTerminalAsync_BothForfeited_ResolvesDrawWithNoScores()
    {
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(match.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        await _connectMatchRepository.MarkPlayerBustedAsync(match.Id, isPlayerA: true, FixedNow.UtcDateTime);
        await _connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: false, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var resolved = await service.TryResolveMatchIfBothTerminalAsync(match.Id);

        Assert.That(resolved, Is.True);
        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Outcome, Is.EqualTo(ConnectMatchOutcome.Draw));
        Assert.That(stored.PlayerAScore, Is.Null);
        Assert.That(stored.PlayerBScore, Is.Null);
    }

    // REQ-1409 GWT#5: a forfeiting player's only consequence is losing/
    // drawing THIS match and having no score for it — nothing carries
    // forward into any other match, including a second match between the
    // very same two players. ConnectMatch has no cross-match field at all
    // (no per-user penalty counter, no rating) that a resolution could even
    // write to beyond its own row; this test pins that a second, unrelated
    // match between the same players is left completely untouched by
    // resolving the first.
    [Test]
    public async Task REQ1409_TryResolveMatchIfBothTerminalAsync_PlayerForfeits_LeavesAnyOtherMatchBetweenSamePlayersUntouched()
    {
        var playerAUserId = Guid.NewGuid();
        var playerBUserId = Guid.NewGuid();
        var forfeitedMatch = await CreateMatchAsync(playerAUserId, playerBUserId, FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(forfeitedMatch.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        await _connectMatchRepository.MarkPlayerBustedAsync(forfeitedMatch.Id, isPlayerA: true, FixedNow.UtcDateTime);
        await _connectMatchRepository.MarkPlayerTimedOutAsync(forfeitedMatch.Id, isPlayerA: false, FixedNow.UtcDateTime);

        // A second, otherwise-unrelated match between the SAME two players,
        // still awaiting target picks — untouched by anything below.
        var otherMatch = await CreateMatchAsync(playerAUserId, playerBUserId, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var resolved = await service.TryResolveMatchIfBothTerminalAsync(forfeitedMatch.Id);

        Assert.That(resolved, Is.True);
        var storedForfeitedMatch = await _connectMatchRepository.GetMatchByIdAsync(forfeitedMatch.Id);
        Assert.That(storedForfeitedMatch!.Outcome, Is.EqualTo(ConnectMatchOutcome.Draw));

        var storedOtherMatch = await _connectMatchRepository.GetMatchByIdAsync(otherMatch.Id);
        Assert.That(storedOtherMatch!.Status, Is.EqualTo(ConnectMatchStatus.AwaitingTargetPicks),
            "a forfeit consequence must never carry over into a different match, even one between the same two players");
        Assert.That(storedOtherMatch.Outcome, Is.EqualTo(ConnectMatchOutcome.Pending));
        Assert.That(storedOtherMatch.PlayerABustedAt, Is.Null);
        Assert.That(storedOtherMatch.PlayerATimedOutAt, Is.Null);
        Assert.That(storedOtherMatch.PlayerBBustedAt, Is.Null);
        Assert.That(storedOtherMatch.PlayerBTimedOutAt, Is.Null);
        Assert.That(storedOtherMatch.PlayerAScore, Is.Null);
        Assert.That(storedOtherMatch.PlayerBScore, Is.Null);
    }

    [Test]
    public async Task REQ1409_TryResolveMatchIfBothTerminalAsync_OnlyOnePlayerTerminal_ReturnsFalse_DoesNotResolve()
    {
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(match.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        await _connectMatchRepository.MarkPlayerBustedAsync(match.Id, isPlayerA: true, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var resolved = await service.TryResolveMatchIfBothTerminalAsync(match.Id);

        Assert.That(resolved, Is.False);
        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.Active));
        Assert.That(stored.Outcome, Is.EqualTo(ConnectMatchOutcome.Pending));
    }

    [Test]
    public async Task REQ1409_TryResolveMatchIfBothTerminalAsync_AlreadyResolved_ReturnsFalse_NeverReResolves()
    {
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(match.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        await _connectMatchRepository.ResolveMatchAsync(match.Id, ConnectMatchOutcome.Draw, FixedNow.UtcDateTime, null, null);
        var service = BuildService(FixedNow.AddHours(1));

        var resolved = await service.TryResolveMatchIfBothTerminalAsync(match.Id);

        Assert.That(resolved, Is.False);
        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.ResolvedAt, Is.EqualTo(FixedNow.UtcDateTime), "an already-resolved match must never be re-resolved");
    }
}

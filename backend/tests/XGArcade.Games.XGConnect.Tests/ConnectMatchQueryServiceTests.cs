using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.TestSupport;

namespace XGArcade.Games.XGConnect.Tests;

// S-218 prep (docs/backlog.md's S-218 entry, REQ-1404/1405/1406/1409/1411):
// the read-only projection layer backing GET /matches and
// GET /matches/{matchId} (XGArcade.Api.Connect.ConnectMatchQueryEndpoints).
// Same no-mocking-framework, real-InMemory-backed-repository pattern as
// ConnectMatchLifecycleServiceTests/ConnectTargetPickServiceTests —
// IConnectMatchRepository/IPlayerRepository are exercised through the real
// ConnectMatchRepository/PlayerRepository against an InMemory-backed
// XGArcadeDbContext; IConnectMatchLifecycleService is the real
// ConnectMatchLifecycleService (its own GetMatchesAwaitingActionAsync gets
// dedicated, direct coverage in ConnectMatchLifecycleServiceTests.cs — this
// file only asserts that ConnectMatchQueryService correctly REUSES it, not
// its own per-slot terminal-state rules again).
public class ConnectMatchQueryServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private XGArcadeDbContext _dbContext = null!;
    private IConnectMatchRepository _connectMatchRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private IUserRepository _userRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _connectMatchRepository = new ConnectMatchRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _userRepository = new UserRepository(_dbContext);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private ConnectMatchQueryService BuildService(DateTimeOffset now) =>
        new(_connectMatchRepository,
            new ConnectMatchLifecycleService(_connectMatchRepository, new ConnectScoringService(), new FixedTimeProvider(now)),
            _playerRepository,
            _userRepository);

    // SCREEN-15 "Identity gap" fix: seeds a real User row so
    // IUserRepository.GetByIdsAsync's batch-resolve has something to find —
    // same shape AddPlayerAsync below already uses for Player rows.
    private async Task<Guid> AddUserAsync(string displayName)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = $"{Guid.NewGuid()}@example.com",
            DisplayName = displayName,
            EmailConfirmed = true,
            CreatedAt = FixedNow.UtcDateTime,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user.Id;
    }

    private async Task<ConnectMatch> CreateMatchAsync(Guid playerAUserId, Guid playerBUserId, DateTime createdAt) =>
        await _connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = playerAUserId,
            PlayerBUserId = playerBUserId,
            CreatedAt = createdAt,
        });

    private async Task<Guid> AddPlayerAsync(string fullName)
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = fullName };
        _dbContext.Players.Add(player);
        await _dbContext.SaveChangesAsync();
        return player.Id;
    }

    private Task AddStepAsync(
        Guid matchId, Guid? userId, int position, int attemptNumber, Guid candidatePlayerId,
        string claimedClubName, bool isValid, bool closesChain, DateTime submittedAt) =>
        _connectMatchRepository.AddChainStepAsync(new ConnectChainStep
        {
            Id = Guid.NewGuid(),
            ConnectMatchId = matchId,
            UserId = userId,
            Position = position,
            AttemptNumber = attemptNumber,
            CandidatePlayerId = candidatePlayerId,
            ClaimedClubName = claimedClubName,
            IsValid = isValid,
            ClosesChain = closesChain,
            SubmittedAt = submittedAt,
        });

    // ---- GetMatchesForUserAsync (REQ-1411/1409) -----------------------------

    [Test]
    public async Task REQ1411_GetMatchesForUserAsync_IncludesResolvedMatches_UnlikeGetOpenMatchesForUserAsync()
    {
        var callerId = Guid.NewGuid();
        var match = await CreateMatchAsync(callerId, Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(match.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        await _connectMatchRepository.ResolveMatchAsync(match.Id, ConnectMatchOutcome.Draw, FixedNow.UtcDateTime, null, null);
        var service = BuildService(FixedNow);

        var result = await service.GetMatchesForUserAsync(callerId);

        Assert.That(result.Select(m => m.MatchId), Is.EquivalentTo(new[] { match.Id }));
        Assert.That(result.Single().Status, Is.EqualTo(ConnectMatchStatus.Resolved));
    }

    [Test]
    public async Task REQ1409_GetMatchesForUserAsync_OutcomeIsTranslatedToCallersOwnPerspective()
    {
        var callerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        // Caller is PlayerB here — PlayerAWin must translate to "Loss" for
        // this caller, not the raw PlayerAWin/PlayerBWin slot value.
        var match = await CreateMatchAsync(otherId, callerId, FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(match.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        await _connectMatchRepository.ResolveMatchAsync(match.Id, ConnectMatchOutcome.PlayerAWin, FixedNow.UtcDateTime, 1, null);
        var service = BuildService(FixedNow);

        var result = await service.GetMatchesForUserAsync(callerId);

        Assert.That(result.Single().Outcome, Is.EqualTo(ConnectMatchPerspectiveOutcome.Loss));
        Assert.That(result.Single().OpponentUserId, Is.EqualTo(otherId));
    }

    // SCREEN-15 "Identity gap" fix: two matches against two DIFFERENT
    // opponents proves the batch-resolve dictionary is keyed correctly per
    // row, not just "some name for everyone" — mirrors 087b2e7's own
    // dedicated multi-row case for FriendEndpoints/ChallengeEndpoints.
    [Test]
    public async Task REQ1411_GetMatchesForUserAsync_MultipleOpponents_ResolvesEachRowsOwnDisplayNameCorrectly()
    {
        var callerId = Guid.NewGuid();
        var opponentOneId = await AddUserAsync("Opponent One");
        var opponentTwoId = await AddUserAsync("Opponent Two");
        var matchOne = await CreateMatchAsync(callerId, opponentOneId, FixedNow.UtcDateTime);
        var matchTwo = await CreateMatchAsync(opponentTwoId, callerId, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.GetMatchesForUserAsync(callerId);

        Assert.That(result.Single(m => m.MatchId == matchOne.Id).OpponentDisplayName, Is.EqualTo("Opponent One"));
        Assert.That(result.Single(m => m.MatchId == matchTwo.Id).OpponentDisplayName, Is.EqualTo("Opponent Two"));
    }

    // REQ-710: an anonymized opponent (OpponentUserId already null) must
    // never resolve to a placeholder DisplayName — OpponentDisplayName
    // mirrors OpponentUserId's own nullability exactly.
    [Test]
    public async Task REQ1411_GetMatchesForUserAsync_OpponentUserIdIsNull_OpponentDisplayNameIsAlsoNull()
    {
        var callerId = Guid.NewGuid();
        // PlayerBUserId is null here (REQ-710 anonymization already ran for
        // that slot) — CreateMatchAsync's own Guid (non-nullable) parameters
        // can't express that, so this constructs the match directly.
        await _connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = callerId,
            PlayerBUserId = null,
            CreatedAt = FixedNow.UtcDateTime,
        });
        var service = BuildService(FixedNow);

        var result = await service.GetMatchesForUserAsync(callerId);

        Assert.That(result.Single().OpponentUserId, Is.Null);
        Assert.That(result.Single().OpponentDisplayName, Is.Null);
    }

    [Test]
    public async Task REQ1411_GetMatchesForUserAsync_ReusesGetMatchesAwaitingActionAsync_ForAwaitingMyActionFlag()
    {
        var callerId = Guid.NewGuid();
        var stillAwaitingMatch = await CreateMatchAsync(callerId, Guid.NewGuid(), FixedNow.UtcDateTime);

        var bustedMatch = await CreateMatchAsync(callerId, Guid.NewGuid(), FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(bustedMatch.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        await _connectMatchRepository.MarkPlayerBustedAsync(bustedMatch.Id, isPlayerA: true, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.GetMatchesForUserAsync(callerId);

        var stillAwaiting = result.Single(m => m.MatchId == stillAwaitingMatch.Id);
        var busted = result.Single(m => m.MatchId == bustedMatch.Id);
        Assert.That(stillAwaiting.AwaitingMyAction, Is.True);
        Assert.That(busted.AwaitingMyAction, Is.False, "caller's own slot already busted — no longer awaiting their move");
    }

    // ---- GetMatchDetailAsync (REQ-1404/1405/1406/1409) ----------------------

    [Test]
    public async Task REQ1404_GetMatchDetailAsync_MatchNotFound_ReturnsMatchNotFound()
    {
        var service = BuildService(FixedNow);

        var result = await service.GetMatchDetailAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.That(result.Outcome, Is.EqualTo(ConnectMatchDetailOutcome.MatchNotFound));
        Assert.That(result.Detail, Is.Null);
    }

    [Test]
    public async Task REQ1404_GetMatchDetailAsync_CallerNotAParticipant_ReturnsNotAParticipant()
    {
        var match = await CreateMatchAsync(Guid.NewGuid(), Guid.NewGuid(), FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.GetMatchDetailAsync(match.Id, Guid.NewGuid());

        Assert.That(result.Outcome, Is.EqualTo(ConnectMatchDetailOutcome.NotAParticipant));
    }

    // REQ-1404: the puzzle's mutual-invisibility rule — the opponent's
    // target pick must stay hidden while Status is still
    // AwaitingTargetPicks, even though the opponent has already picked
    // (unlocked) at this point.
    [Test]
    public async Task REQ1404_GetMatchDetailAsync_AwaitingTargetPicks_OpponentPickAlreadyExistsButStaysHidden()
    {
        var callerId = Guid.NewGuid();
        var opponentId = Guid.NewGuid();
        var match = await CreateMatchAsync(callerId, opponentId, FixedNow.UtcDateTime);
        var opponentTargetPlayerId = await AddPlayerAsync("Opponent Target");
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, opponentId, opponentTargetPlayerId, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.GetMatchDetailAsync(match.Id, callerId);

        Assert.That(result.Outcome, Is.EqualTo(ConnectMatchDetailOutcome.Found));
        Assert.That(result.Detail!.MyTargetPick, Is.Null, "caller hasn't picked yet");
        Assert.That(result.Detail.OpponentTargetPick, Is.Null,
            "REQ-1404: opponent's pick must stay hidden until the match leaves AwaitingTargetPicks");
    }

    // REQ-1404/1405: once both picks are locked (match Active), both
    // target picks become visible to both players, with names resolved.
    [Test]
    public async Task REQ1405_GetMatchDetailAsync_MatchActive_BothTargetPicksVisibleWithResolvedNames()
    {
        var callerId = Guid.NewGuid();
        var opponentId = Guid.NewGuid();
        var match = await CreateMatchAsync(callerId, opponentId, FixedNow.UtcDateTime);
        var myTargetPlayerId = await AddPlayerAsync("My Target");
        var opponentTargetPlayerId = await AddPlayerAsync("Opponent Target");
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, callerId, myTargetPlayerId, FixedNow.UtcDateTime);
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, opponentId, opponentTargetPlayerId, FixedNow.UtcDateTime);
        await _connectMatchRepository.LockTargetPicksForMatchAsync(match.Id);
        await _connectMatchRepository.StartMatchAsync(match.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        var service = BuildService(FixedNow);

        var result = await service.GetMatchDetailAsync(match.Id, callerId);

        Assert.That(result.Detail!.MyTargetPick, Is.Not.Null);
        Assert.That(result.Detail.MyTargetPick!.TargetPlayerId, Is.EqualTo(myTargetPlayerId));
        Assert.That(result.Detail.MyTargetPick.TargetPlayerName, Is.EqualTo("My Target"));
        Assert.That(result.Detail.MyTargetPick.Locked, Is.True);
        Assert.That(result.Detail.OpponentTargetPick, Is.Not.Null);
        Assert.That(result.Detail.OpponentTargetPick!.TargetPlayerId, Is.EqualTo(opponentTargetPlayerId));
        Assert.That(result.Detail.OpponentTargetPick.TargetPlayerName, Is.EqualTo("Opponent Target"));
    }

    // REQ-1406: only the caller's own chain steps are ever returned, in
    // submission order, with candidate names resolved.
    [Test]
    public async Task REQ1406_GetMatchDetailAsync_ReturnsOnlyCallersOwnChainStepsInOrder()
    {
        var callerId = Guid.NewGuid();
        var opponentId = Guid.NewGuid();
        var match = await CreateMatchAsync(callerId, opponentId, FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(match.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        var candidateOneId = await AddPlayerAsync("Candidate One");
        var candidateTwoId = await AddPlayerAsync("Candidate Two");
        var opponentCandidateId = await AddPlayerAsync("Opponent's Own Candidate");
        await AddStepAsync(match.Id, callerId, position: 1, attemptNumber: 1, candidateOneId, "Arsenal", isValid: true, closesChain: false, FixedNow.UtcDateTime);
        await AddStepAsync(match.Id, callerId, position: 2, attemptNumber: 1, candidateTwoId, "Chelsea", isValid: true, closesChain: true, FixedNow.UtcDateTime.AddMinutes(1));
        await AddStepAsync(match.Id, opponentId, position: 1, attemptNumber: 1, opponentCandidateId, "Liverpool", isValid: true, closesChain: false, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.GetMatchDetailAsync(match.Id, callerId);

        Assert.That(result.Detail!.MyChainSteps, Has.Count.EqualTo(2));
        Assert.That(result.Detail.MyChainSteps[0].CandidatePlayerId, Is.EqualTo(candidateOneId));
        Assert.That(result.Detail.MyChainSteps[0].CandidatePlayerName, Is.EqualTo("Candidate One"));
        Assert.That(result.Detail.MyChainSteps[1].CandidatePlayerId, Is.EqualTo(candidateTwoId));
        Assert.That(result.Detail.MyChainSteps[1].ClosesChain, Is.True);
    }

    // REQ-1406/1407/1408: the opponent's terminal state is exposed as three
    // booleans only — their actual chain steps are never included anywhere
    // in the response.
    [Test]
    public async Task REQ1406_GetMatchDetailAsync_OpponentTerminalStateReflectsClosedChain_ButStepsNeverExposed()
    {
        var callerId = Guid.NewGuid();
        var opponentId = Guid.NewGuid();
        var match = await CreateMatchAsync(callerId, opponentId, FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(match.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        var opponentCandidateId = await AddPlayerAsync("Opponent Candidate");
        await AddStepAsync(match.Id, opponentId, position: 1, attemptNumber: 1, opponentCandidateId, "Arsenal", isValid: true, closesChain: true, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.GetMatchDetailAsync(match.Id, callerId);

        Assert.That(result.Detail!.OpponentTerminalState.Completed, Is.True);
        Assert.That(result.Detail.OpponentTerminalState.Busted, Is.False);
        Assert.That(result.Detail.OpponentTerminalState.TimedOut, Is.False);
        Assert.That(result.Detail.MyTerminalState, Is.EqualTo(new ConnectTerminalState(false, false, false)));
    }

    // REQ-1407: busted/timed-out are read per-slot, correctly attributed to
    // caller vs. opponent regardless of which slot (A/B) each occupies.
    [Test]
    public async Task REQ1407_GetMatchDetailAsync_CallerIsPlayerB_TerminalStatesCorrectlyAttributedPerSlot()
    {
        var callerId = Guid.NewGuid();
        var opponentId = Guid.NewGuid();
        // Caller is PlayerB.
        var match = await CreateMatchAsync(opponentId, callerId, FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(match.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        await _connectMatchRepository.MarkPlayerBustedAsync(match.Id, isPlayerA: false, FixedNow.UtcDateTime);
        await _connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: true, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.GetMatchDetailAsync(match.Id, callerId);

        Assert.That(result.Detail!.MyTerminalState.Busted, Is.True, "caller occupies PlayerB, which was marked busted");
        Assert.That(result.Detail.OpponentTerminalState.TimedOut, Is.True, "opponent occupies PlayerA, which was marked timed out");
    }

    // REQ-1409: scores are attributed per-slot, correctly, once resolved.
    [Test]
    public async Task REQ1409_GetMatchDetailAsync_ResolvedMatch_ScoresAttributedPerSlot()
    {
        var callerId = Guid.NewGuid();
        var opponentId = Guid.NewGuid();
        // Caller is PlayerB.
        var match = await CreateMatchAsync(opponentId, callerId, FixedNow.UtcDateTime);
        await _connectMatchRepository.StartMatchAsync(match.Id, FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(6));
        await _connectMatchRepository.ResolveMatchAsync(match.Id, ConnectMatchOutcome.PlayerBWin, FixedNow.UtcDateTime, playerAScore: 3, playerBScore: 1);
        var service = BuildService(FixedNow);

        var result = await service.GetMatchDetailAsync(match.Id, callerId);

        Assert.That(result.Detail!.MyScore, Is.EqualTo(1));
        Assert.That(result.Detail.OpponentScore, Is.EqualTo(3));
        Assert.That(result.Detail.Outcome, Is.EqualTo(ConnectMatchPerspectiveOutcome.Win));
    }

    // SCREEN-15 "Identity gap" fix: GetMatchDetailAsync's own single-id
    // resolve, exercised independently of GetMatchesForUserAsync's
    // batch-across-a-page version above.
    [Test]
    public async Task REQ1404_GetMatchDetailAsync_ResolvesOpponentDisplayName()
    {
        var callerId = Guid.NewGuid();
        var opponentId = await AddUserAsync("Opponent Name");
        var match = await CreateMatchAsync(callerId, opponentId, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.GetMatchDetailAsync(match.Id, callerId);

        Assert.That(result.Detail!.OpponentUserId, Is.EqualTo(opponentId));
        Assert.That(result.Detail.OpponentDisplayName, Is.EqualTo("Opponent Name"));
    }

    // REQ-710: same anonymization rule as GetMatchesForUserAsync's own
    // null-opponent test above, exercised here for GetMatchDetailAsync too.
    [Test]
    public async Task REQ1404_GetMatchDetailAsync_OpponentUserIdIsNull_OpponentDisplayNameIsAlsoNull()
    {
        var callerId = Guid.NewGuid();
        var match = await _connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = callerId,
            PlayerBUserId = null,
            CreatedAt = FixedNow.UtcDateTime,
        });
        var service = BuildService(FixedNow);

        var result = await service.GetMatchDetailAsync(match.Id, callerId);

        Assert.That(result.Detail!.OpponentUserId, Is.Null);
        Assert.That(result.Detail.OpponentDisplayName, Is.Null);
    }

    // A candidate/target player id with no corresponding Player row (should
    // never happen given the FK, but this pins the fallback rather than
    // throwing) resolves to a placeholder name instead of failing the whole
    // read.
    [Test]
    public async Task REQ1404_GetMatchDetailAsync_TargetPlayerRowMissing_FallsBackToPlaceholderName()
    {
        var callerId = Guid.NewGuid();
        var opponentId = Guid.NewGuid();
        var match = await CreateMatchAsync(callerId, opponentId, FixedNow.UtcDateTime);
        var unknownTargetPlayerId = Guid.NewGuid();
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, callerId, unknownTargetPlayerId, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.GetMatchDetailAsync(match.Id, callerId);

        Assert.That(result.Detail!.MyTargetPick!.TargetPlayerName, Is.EqualTo("Unknown player"));
    }
}

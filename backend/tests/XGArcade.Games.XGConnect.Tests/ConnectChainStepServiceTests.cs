using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.TestSupport;

namespace XGArcade.Games.XGConnect.Tests;

// REQ-1406 (docs/requirements-document.md §4.15): incremental connection-
// chain-step submission and live validation. REQ-1407/S-214: the two-
// strikes-per-step bust rule and the "already forfeited" precondition. Same
// no-mocking-framework, real-InMemory-backed-repository pattern as
// ConnectTargetPickServiceTests — IConnectMatchRepository/IPlayerRepository
// are exercised through their real implementations against an
// InMemory-backed XGArcadeDbContext; IPlayerCareerOverlapService is
// hand-rolled-faked (FakePlayerCareerOverlapService) since its own
// overlap-detection logic gets dedicated, direct coverage in
// PlayerCareerOverlapServiceTests.cs; IConnectMatchLifecycleService is the
// real ConnectMatchLifecycleService (backed by the same real
// ConnectMatchRepository and a real ConnectScoringService) rather than a
// fake, since REQ-1407's bust branch needs to actually observe
// TryResolveMatchIfBothTerminalAsync's real resolution effect — this file
// is only concerned with SubmitChainStepAsync's own orchestration around
// each check's result.
public class ConnectChainStepServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private XGArcadeDbContext _dbContext = null!;
    private IConnectMatchRepository _connectMatchRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private FakePlayerCareerOverlapService _overlapService = null!;
    private IConnectMatchLifecycleService _connectMatchLifecycleService = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _connectMatchRepository = new ConnectMatchRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _overlapService = new FakePlayerCareerOverlapService();
        _connectMatchLifecycleService = new ConnectMatchLifecycleService(
            _connectMatchRepository, new ConnectScoringService(), new FixedTimeProvider(FixedNow));
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private ConnectChainStepService BuildService(DateTimeOffset now) =>
        new(_connectMatchRepository, _overlapService, _playerRepository, _connectMatchLifecycleService, new FixedTimeProvider(now));

    private async Task<Player> SeedPlayerAsync(string fullName) =>
        await _playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = fullName });

    // Creates an ACTIVE match (both target picks locked) — the precondition
    // REQ-1406 itself requires ("given an active match").
    private async Task<(ConnectMatch Match, Guid AUserId, Guid BUserId, Guid ATargetPlayerId, Guid BTargetPlayerId)> CreateActiveMatchAsync()
    {
        var aUserId = Guid.NewGuid();
        var bUserId = Guid.NewGuid();
        var match = await _connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = aUserId,
            PlayerBUserId = bUserId,
            CreatedAt = FixedNow.UtcDateTime,
            Status = ConnectMatchStatus.Active,
            StartedAt = FixedNow.UtcDateTime,
            DeadlineUtc = FixedNow.UtcDateTime.AddHours(6),
        });

        var aTarget = await SeedPlayerAsync("A Target Player");
        var bTarget = await SeedPlayerAsync("B Target Player");
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, aUserId, aTarget.Id, FixedNow.UtcDateTime);
        await _connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, bUserId, bTarget.Id, FixedNow.UtcDateTime);

        return (match, aUserId, bUserId, aTarget.Id, bTarget.Id);
    }

    // ---- REQ-1406 GWT#1/#2: a valid overlapping-time step is accepted and
    // ---- appended --------------------------------------------------------

    [Test]
    public async Task REQ1406_SubmitChainStepAsync_ValidOverlappingTimeStep_AcceptsAndAppendsStep()
    {
        var (match, aUserId, _, aTargetPlayerId, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        _overlapService.SetOverlapAtClub(candidate.Id, aTargetPlayerId, "Arsenal", overlaps: true);
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Middle Link Player", "Arsenal");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.StepAccepted));
        Assert.That(result.ChainStep, Is.Not.Null);
        Assert.That(result.ChainStep!.IsValid, Is.True);
        Assert.That(result.ChainStep.ClosesChain, Is.False);
        Assert.That(result.ChainStep.Position, Is.EqualTo(1));
        Assert.That(result.ChainStep.AttemptNumber, Is.EqualTo(1));
        Assert.That(result.ChainStep.CandidatePlayerId, Is.EqualTo(candidate.Id));
        Assert.That(result.ChainStep.ClaimedClubName, Is.EqualTo("Arsenal"));

        var persisted = await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId);
        Assert.That(persisted, Has.Count.EqualTo(1));
        Assert.That(persisted[0].IsValid, Is.True);
        Assert.That(persisted[0].ClosesChain, Is.False);
    }

    // ---- REQ-1406 GWT#3: a non-overlapping-period claim is rejected -------

    [Test]
    public async Task REQ1406_SubmitChainStepAsync_NonOverlappingPeriodClaim_RejectsAsInvalidStep_AndPersistsIt()
    {
        var (match, aUserId, _, aTargetPlayerId, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        _overlapService.SetOverlapAtClub(candidate.Id, aTargetPlayerId, "Arsenal", overlaps: false);
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Middle Link Player", "Arsenal");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.InvalidStep));
        Assert.That(result.ChainStep, Is.Not.Null);
        Assert.That(result.ChainStep!.IsValid, Is.False);
        Assert.That(result.ChainStep.ClosesChain, Is.False);

        var persisted = await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId);
        Assert.That(persisted, Has.Count.EqualTo(1), "a failed attempt is still persisted — it IS the outcome this entity records");
        Assert.That(persisted[0].IsValid, Is.False);
    }

    // ---- REQ-1406 GWT#3 variant: a club the candidate never played for
    // ---- at all is rejected the same way -----------------------------------

    [Test]
    public async Task REQ1406_SubmitChainStepAsync_ClubCandidateNeverPlayedFor_RejectsAsInvalidStep()
    {
        var (match, aUserId, _, aTargetPlayerId, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        // Never configured on the fake — defaults to "no overlap," matching
        // "candidate never played for this club at all."
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Middle Link Player", "Some Unrelated Club");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.InvalidStep));
        Assert.That(result.ChainStep!.IsValid, Is.False);
        Assert.That(_overlapService.ClubCalls, Has.Count.EqualTo(1));
        Assert.That(_overlapService.ClubCalls[0], Is.EqualTo((candidate.Id, aTargetPlayerId, "Some Unrelated Club")));
    }

    // ---- REQ-1406 GWT#4: a closing step is detected against the OTHER
    // ---- target pick, never the one the chain started from -----------------

    [Test]
    public async Task REQ1406_SubmitChainStepAsync_CandidateConnectsToOtherTarget_ReturnsChainClosed_AndPersistsClosesChainTrue()
    {
        var (match, aUserId, _, aTargetPlayerId, bTargetPlayerId) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Closing Link Player");
        _overlapService.SetOverlapAtClub(candidate.Id, aTargetPlayerId, "Arsenal", overlaps: true);
        // Closes against the OTHER target (B's), not the one the chain
        // started from (A's) — HaveSharedClubOverlapAsync is the "any shared
        // club" check, called against candidate vs. bTargetPlayerId.
        _overlapService.SetOverlap(candidate.Id, bTargetPlayerId, overlaps: true);
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Closing Link Player", "Arsenal");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.ChainClosed));
        Assert.That(result.ChainStep!.IsValid, Is.True);
        Assert.That(result.ChainStep.ClosesChain, Is.True);
        Assert.That(_overlapService.Calls, Has.Count.EqualTo(1));
        Assert.That(_overlapService.Calls[0], Is.EqualTo((candidate.Id, bTargetPlayerId)),
            "the closing check must run against the OTHER participant's target pick");

        var persisted = await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId);
        Assert.That(persisted, Has.Count.EqualTo(1));
        Assert.That(persisted[0].ClosesChain, Is.True);
    }

    // ---- REQ-1406 GWT#4's own "never the starting target" guard: a
    // ---- candidate that connects back to the STARTING target only must
    // ---- NOT be treated as closing -----------------------------------------

    [Test]
    public async Task REQ1406_SubmitChainStepAsync_CandidateOnlyConnectsBackToStartingTarget_DoesNotClose()
    {
        var (match, aUserId, _, aTargetPlayerId, bTargetPlayerId) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Non-Closing Link Player");
        _overlapService.SetOverlapAtClub(candidate.Id, aTargetPlayerId, "Arsenal", overlaps: true);
        // Deliberately NOT configuring an overlap against bTargetPlayerId —
        // defaults to false. Even though the fake WOULD also happily report
        // an overlap against aTargetPlayerId if asked, the service must only
        // ever check the OTHER (B) target for closing.
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Non-Closing Link Player", "Arsenal");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.StepAccepted));
        Assert.That(result.ChainStep!.ClosesChain, Is.False);
        Assert.That(_overlapService.Calls[0], Is.EqualTo((candidate.Id, bTargetPlayerId)),
            "the closing check must be against B's target, never A's (the starting target)");
    }

    // ---- REQ-1406: no further steps after the chain has closed -------------

    [Test]
    public async Task REQ1406_SubmitChainStepAsync_AfterChainAlreadyClosed_ReturnsChainAlreadyComplete_PersistsNothingNew()
    {
        var (match, aUserId, _, aTargetPlayerId, bTargetPlayerId) = await CreateActiveMatchAsync();
        var closingCandidate = await SeedPlayerAsync("Closing Link Player");
        _overlapService.SetOverlapAtClub(closingCandidate.Id, aTargetPlayerId, "Arsenal", overlaps: true);
        _overlapService.SetOverlap(closingCandidate.Id, bTargetPlayerId, overlaps: true);
        var firstService = BuildService(FixedNow);
        await firstService.SubmitChainStepAsync(match.Id, aUserId, "Closing Link Player", "Arsenal");

        // Deliberately an unknown name — proves ChainAlreadyComplete
        // short-circuits before candidate resolution ever runs.
        var secondService = BuildService(FixedNow.AddMinutes(5));
        var result = await secondService.SubmitChainStepAsync(match.Id, aUserId, "Should Never Be Reached", "Anywhere");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.ChainAlreadyComplete));
        Assert.That(result.ChainStep, Is.Null);

        var persisted = await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId);
        Assert.That(persisted, Has.Count.EqualTo(1), "only the original closing step must exist — nothing new persisted after completion");
    }

    // ---- Mechanical precondition branches -----------------------------------

    [Test]
    public async Task REQ1406_SubmitChainStepAsync_MatchNotFound_ReturnsMatchNotFoundOutcome()
    {
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(Guid.NewGuid(), Guid.NewGuid(), "Anyone", "Anywhere");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.MatchNotFound));
        Assert.That(result.ChainStep, Is.Null);
    }

    [Test]
    public async Task REQ1406_SubmitChainStepAsync_CallerNotAParticipant_ReturnsNotAParticipantOutcome()
    {
        var (match, _, _, _, _) = await CreateActiveMatchAsync();
        var outsider = Guid.NewGuid();
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, outsider, "Anyone", "Anywhere");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.NotAParticipant));
        Assert.That(result.ChainStep, Is.Null);
    }

    [Test]
    public async Task REQ1406_SubmitChainStepAsync_MatchNotActive_ReturnsMatchNotActiveOutcome()
    {
        var aUserId = Guid.NewGuid();
        var bUserId = Guid.NewGuid();
        var match = await _connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = aUserId,
            PlayerBUserId = bUserId,
            CreatedAt = FixedNow.UtcDateTime,
            Status = ConnectMatchStatus.AwaitingTargetPicks,
        });
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Anyone", "Anywhere");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.MatchNotActive));
        Assert.That(result.ChainStep, Is.Null);
    }

    [Test]
    public async Task REQ1406_SubmitChainStepAsync_CandidateNameDoesNotResolveToAnyPlayer_ReturnsCandidateNotFound_PersistsNothing()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Nobody Real", "Anywhere");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.CandidateNotFound));
        Assert.That(result.ChainStep, Is.Null);
        Assert.That(await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId), Is.Empty);
    }

    [Test]
    public async Task REQ1406_SubmitChainStepAsync_LiveLookupUnavailableOnMainCheck_PersistsNothing()
    {
        var (match, aUserId, _, aTargetPlayerId, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        _overlapService.SetLiveLookupUnavailableAtClub(candidate.Id, aTargetPlayerId, "Arsenal");
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Middle Link Player", "Arsenal");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.LiveLookupUnavailable));
        Assert.That(result.ChainStep, Is.Null);
        Assert.That(await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId), Is.Empty);
    }

    [Test]
    public async Task REQ1406_SubmitChainStepAsync_LiveLookupUnavailableOnClosingCheck_DiscardsWholeStep_PersistsNothing()
    {
        var (match, aUserId, _, aTargetPlayerId, bTargetPlayerId) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        // Main check passes...
        _overlapService.SetOverlapAtClub(candidate.Id, aTargetPlayerId, "Arsenal", overlaps: true);
        // ...but the closing check against B's target fails technically.
        _overlapService.SetLiveLookupUnavailable(candidate.Id, bTargetPlayerId);
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Middle Link Player", "Arsenal");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.LiveLookupUnavailable));
        Assert.That(result.ChainStep, Is.Null);
        Assert.That(await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId), Is.Empty,
            "the whole step — including its already-passed main check — must be discarded, never partially persisted");
    }

    // ---- REQ-1407: two-strikes-per-step penalty and bust rule ---------------

    [Test]
    public async Task REQ1407_SubmitChainStepAsync_FirstFailureAtPosition_ReturnsInvalidStep_NeverBusts()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        await SeedPlayerAsync("Middle Link Player");
        // Never configured on the fake — defaults to "no overlap."
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Middle Link Player", "Wrong Club");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.InvalidStep),
            "a first failure at a position is an ordinary invalid step, not a bust");
        Assert.That(result.ChainStep!.AttemptNumber, Is.EqualTo(1));
        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.PlayerABustedAt, Is.Null);
        Assert.That(stored.PlayerBBustedAt, Is.Null);
    }

    [Test]
    public async Task REQ1407_SubmitChainStepAsync_SecondConsecutiveFailureAtSamePosition_BustsThePlayer()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        await SeedPlayerAsync("First Attempt Player");
        await SeedPlayerAsync("Retry Attempt Player");
        // Neither candidate is configured to overlap — both attempts fail.
        var service = BuildService(FixedNow);
        var firstResult = await service.SubmitChainStepAsync(match.Id, aUserId, "First Attempt Player", "Wrong Club");
        Assert.That(firstResult.Outcome, Is.EqualTo(SubmitChainStepOutcome.InvalidStep));

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Retry Attempt Player", "Also Wrong Club");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.Busted));
        Assert.That(result.ChainStep, Is.Not.Null, "the failed retry attempt is still persisted, same as any InvalidStep");
        Assert.That(result.ChainStep!.AttemptNumber, Is.EqualTo(2));
        Assert.That(result.ChainStep.IsValid, Is.False);

        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.PlayerABustedAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(stored.PlayerBBustedAt, Is.Null, "only the busted player's own slot is marked");

        var persisted = await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId);
        Assert.That(persisted, Has.Count.EqualTo(2), "both the first failure and the failed retry are distinct, persisted rows");
    }

    [Test]
    public async Task REQ1407_SubmitChainStepAsync_SuccessfulRetry_KeepsChainGoing_NeverBusts_AndResetsStrikeCountForNextPosition()
    {
        var (match, aUserId, _, aTargetPlayerId, _) = await CreateActiveMatchAsync();
        await SeedPlayerAsync("Failed First Attempt");
        var retryCandidate = await SeedPlayerAsync("Successful Retry");
        _overlapService.SetOverlapAtClub(retryCandidate.Id, aTargetPlayerId, "Arsenal", overlaps: true);
        var service = BuildService(FixedNow);
        await service.SubmitChainStepAsync(match.Id, aUserId, "Failed First Attempt", "Wrong Club");

        var retryResult = await service.SubmitChainStepAsync(match.Id, aUserId, "Successful Retry", "Arsenal");

        Assert.That(retryResult.Outcome, Is.EqualTo(SubmitChainStepOutcome.StepAccepted));
        Assert.That(retryResult.ChainStep!.AttemptNumber, Is.EqualTo(2));
        var storedAfterRetry = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(storedAfterRetry!.PlayerABustedAt, Is.Null, "a successful retry must never bust the player");

        // A later failure at the NEXT position gets its own independent
        // first attempt — never combined with the earlier position's strike.
        await SeedPlayerAsync("Next Position Failure");
        var nextResult = await service.SubmitChainStepAsync(match.Id, aUserId, "Next Position Failure", "Unrelated Club");

        Assert.That(nextResult.Outcome, Is.EqualTo(SubmitChainStepOutcome.InvalidStep),
            "a fresh position starts its own independent strike count, not a bust");
        Assert.That(nextResult.ChainStep!.Position, Is.EqualTo(2));
        Assert.That(nextResult.ChainStep.AttemptNumber, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ1407_SubmitChainStepAsync_CallerAlreadyBusted_ReturnsAlreadyForfeited_PersistsNothing()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        await _connectMatchRepository.MarkPlayerBustedAsync(match.Id, isPlayerA: true, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Anyone", "Anywhere");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.AlreadyForfeited));
        Assert.That(result.ChainStep, Is.Null);
        Assert.That(await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId), Is.Empty);
    }

    // Closes the pre-existing gap this story's brief calls out: a player who
    // already timed out (REQ-1405) but whose match Status is still Active
    // (because the other player hasn't reached terminal yet) must not be
    // able to submit further steps.
    [Test]
    public async Task REQ1407_SubmitChainStepAsync_CallerAlreadyTimedOutButMatchStillActive_ReturnsAlreadyForfeited()
    {
        var (match, _, bUserId, _, _) = await CreateActiveMatchAsync();
        await _connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: false, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, bUserId, "Anyone", "Anywhere");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.AlreadyForfeited));
        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.Active), "the match itself is still Active — only the caller's own forfeited slot blocks them");
    }

    // ---- REQ-1409: a bust/chain-close is a terminal state, so resolution
    // ---- is attempted immediately from this service, not deferred --------

    [Test]
    public async Task REQ1409_SubmitChainStepAsync_BustWhileOtherPlayerAlreadyForfeited_ResolvesMatchImmediatelyAsDraw()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        await _connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: false, FixedNow.UtcDateTime);
        await SeedPlayerAsync("First Attempt Player");
        await SeedPlayerAsync("Retry Attempt Player");
        var service = BuildService(FixedNow);
        await service.SubmitChainStepAsync(match.Id, aUserId, "First Attempt Player", "Wrong Club");

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Retry Attempt Player", "Also Wrong Club");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.Busted));
        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.Resolved), "both players are now terminal — resolution must happen in this same call");
        Assert.That(stored.Outcome, Is.EqualTo(ConnectMatchOutcome.Draw));
    }

    [Test]
    public async Task REQ1409_SubmitChainStepAsync_ChainClosedWhileOtherPlayerAlreadyForfeited_ResolvesMatchImmediatelyAsCompleterWin()
    {
        var (match, aUserId, _, aTargetPlayerId, bTargetPlayerId) = await CreateActiveMatchAsync();
        await _connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: false, FixedNow.UtcDateTime);
        var candidate = await SeedPlayerAsync("Closing Link Player");
        _overlapService.SetOverlapAtClub(candidate.Id, aTargetPlayerId, "Arsenal", overlaps: true);
        _overlapService.SetOverlap(candidate.Id, bTargetPlayerId, overlaps: true);
        var service = BuildService(FixedNow);

        var result = await service.SubmitChainStepAsync(match.Id, aUserId, "Closing Link Player", "Arsenal");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.ChainClosed));
        var stored = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(stored!.Status, Is.EqualTo(ConnectMatchStatus.Resolved));
        Assert.That(stored.Outcome, Is.EqualTo(ConnectMatchOutcome.PlayerAWin));
        Assert.That(stored.PlayerAScore, Is.EqualTo(1));
        Assert.That(stored.PlayerBScore, Is.Null);
    }
}

using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.TestSupport;

namespace XGArcade.Games.XGConnect.Tests;

// REQ-1412/1413/1414 (docs/requirements-document.md §4.15), ADR-0109:
// dispute-a-failed-chain-step raise/review, and the REQ-1414
// data-correction-suggestion by-product. Same real-InMemory-backed-
// repository, no-mocking-framework pattern as ConnectChainStepServiceTests
// — IConnectMatchRepository is exercised through the real
// ConnectMatchRepository against an InMemory-backed XGArcadeDbContext;
// IConnectMatchLifecycleService is the real ConnectMatchLifecycleService
// (backed by the same real repository and a real ConnectScoringService),
// since REQ-1413's resolution-gating and REQ-1412's bust/resolve-attempt
// effects need to be observed for real, not stubbed.
public class ConnectChainStepDisputeServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private XGArcadeDbContext _dbContext = null!;
    private IConnectMatchRepository _connectMatchRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private ConnectMatchLifecycleService _connectMatchLifecycleService = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _connectMatchRepository = new ConnectMatchRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _connectMatchLifecycleService = new ConnectMatchLifecycleService(
            _connectMatchRepository, new ConnectScoringService(), new FixedTimeProvider(FixedNow));
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private ConnectChainStepDisputeService BuildService(DateTimeOffset now) =>
        new(_connectMatchRepository, _connectMatchLifecycleService, new FixedTimeProvider(now));

    private async Task<Player> SeedPlayerAsync(string fullName) =>
        await _playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = fullName });

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

    private async Task<ConnectChainStep> AddInvalidStepAsync(
        Guid matchId, Guid userId, int position, int attemptNumber, Guid candidatePlayerId, DateTime submittedAt) =>
        await _connectMatchRepository.AddChainStepAsync(new ConnectChainStep
        {
            Id = Guid.NewGuid(),
            ConnectMatchId = matchId,
            UserId = userId,
            Position = position,
            AttemptNumber = attemptNumber,
            CandidatePlayerId = candidatePlayerId,
            MatchedClubName = null,
            IsValid = false,
            ClosesChain = false,
            SubmittedAt = submittedAt,
        });

    private async Task<ConnectChainStep> AddValidStepAsync(
        Guid matchId, Guid userId, int position, int attemptNumber, Guid candidatePlayerId, bool closesChain, DateTime submittedAt) =>
        await _connectMatchRepository.AddChainStepAsync(new ConnectChainStep
        {
            Id = Guid.NewGuid(),
            ConnectMatchId = matchId,
            UserId = userId,
            Position = position,
            AttemptNumber = attemptNumber,
            CandidatePlayerId = candidatePlayerId,
            MatchedClubName = "Some Club",
            MatchedOverlapStartYear = 2000,
            MatchedOverlapEndYear = 2005,
            IsValid = true,
            ClosesChain = closesChain,
            SubmittedAt = submittedAt,
        });

    // ---- REQ-1412: raising a dispute on either failure type -----------------

    [Test]
    public async Task REQ1412_RaiseDisputeAsync_FirstFailure_RecordsPendingDispute_AndMarksSlotBusted()
    {
        var (match, aUserId, _, aTargetPlayerId, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        var step = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.RaiseDisputeAsync(match.Id, step.Id, aUserId, "Arsenal");

        Assert.That(result.Outcome, Is.EqualTo(RaiseChainStepDisputeOutcome.Raised));
        Assert.That(result.Dispute!.Status, Is.EqualTo(ConnectChainStepDisputeStatus.Pending));
        Assert.That(result.Dispute.ClaimedClubName, Is.EqualTo("Arsenal"));

        var storedMatch = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(storedMatch!.PlayerABustedAt, Is.EqualTo(FixedNow.UtcDateTime),
            "raising a dispute on a first failure consumes the retry immediately — the slot is marked busted now");

        var storedStep = (await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId)).Single(s => s.Id == step.Id);
        Assert.That(storedStep.HasPendingDispute, Is.True);
    }

    [Test]
    public async Task REQ1412_RaiseDisputeAsync_BustCausingSecondFailure_RecordsPendingDispute_LeavesExistingBustUnchanged()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        var firstCandidate = await SeedPlayerAsync("First Attempt Player");
        var retryCandidate = await SeedPlayerAsync("Retry Attempt Player");
        await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, firstCandidate.Id, FixedNow.UtcDateTime);
        var bustStep = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 2, retryCandidate.Id, FixedNow.UtcDateTime);
        // Mirrors what SubmitChainStepAsync's own bust branch already does
        // before a dispute is ever raised.
        await _connectMatchRepository.MarkPlayerBustedAsync(match.Id, isPlayerA: true, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow.AddMinutes(5));

        var result = await service.RaiseDisputeAsync(match.Id, bustStep.Id, aUserId, "Arsenal");

        Assert.That(result.Outcome, Is.EqualTo(RaiseChainStepDisputeOutcome.Raised));
        var storedMatch = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(storedMatch!.PlayerABustedAt, Is.EqualTo(FixedNow.UtcDateTime),
            "MarkPlayerBustedAsync's own idempotent `??=` — the ORIGINAL bust timestamp survives, not the dispute-raise time");
    }

    [Test]
    public async Task REQ1412_RaiseDisputeAsync_PendingDispute_LetsCallerKeepSubmittingFurtherSteps()
    {
        var (match, aUserId, _, _, bTargetPlayerId) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        var step = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);
        await service.RaiseDisputeAsync(match.Id, step.Id, aUserId, "Arsenal");

        var overlapService = new FakePlayerCareerOverlapService();
        var closingCandidate = await SeedPlayerAsync("Closing Link Player");
        // The next step's "preceding player" must be the DISPUTED step's own
        // candidate — proving the chain really did advance past it.
        overlapService.SetSharedClubOverlaps(closingCandidate.Id, candidate.Id, new SharedClubOverlap("Chelsea", 2005, 2010));
        overlapService.SetOverlap(closingCandidate.Id, bTargetPlayerId, overlaps: true);
        var chainStepService = new ConnectChainStepService(
            _connectMatchRepository, overlapService, _playerRepository, _connectMatchLifecycleService, new FixedTimeProvider(FixedNow));

        var result = await chainStepService.SubmitChainStepAsync(match.Id, aUserId, "Closing Link Player");

        Assert.That(result.Outcome, Is.EqualTo(SubmitChainStepOutcome.ChainClosed),
            "a player with a Pending dispute may keep submitting, and even close their chain (REQ-1412)");
        Assert.That(result.ChainStep!.Position, Is.EqualTo(2));
    }

    [Test]
    public async Task REQ1412_RaiseDisputeAsync_StepAlreadyValid_ReturnsStepNotInvalid()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Valid Link Player");
        var step = await AddValidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, closesChain: false, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.RaiseDisputeAsync(match.Id, step.Id, aUserId, "Arsenal");

        Assert.That(result.Outcome, Is.EqualTo(RaiseChainStepDisputeOutcome.StepNotInvalid));
    }

    [Test]
    public async Task REQ1412_RaiseDisputeAsync_AlreadyDisputed_ReturnsAlreadyDisputed()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        var step = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);
        await service.RaiseDisputeAsync(match.Id, step.Id, aUserId, "Arsenal");

        var result = await service.RaiseDisputeAsync(match.Id, step.Id, aUserId, "Chelsea");

        Assert.That(result.Outcome, Is.EqualTo(RaiseChainStepDisputeOutcome.AlreadyDisputed));
    }

    [Test]
    public async Task REQ1412_RaiseDisputeAsync_OldSupersededFailure_ReturnsStepSuperseded()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        var firstCandidate = await SeedPlayerAsync("First Attempt Player");
        var retryCandidate = await SeedPlayerAsync("Retry Attempt Player");
        var firstStep = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, firstCandidate.Id, FixedNow.UtcDateTime);
        await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 2, retryCandidate.Id, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.RaiseDisputeAsync(match.Id, firstStep.Id, aUserId, "Arsenal");

        Assert.That(result.Outcome, Is.EqualTo(RaiseChainStepDisputeOutcome.StepSuperseded));
    }

    [Test]
    public async Task REQ1412_RaiseDisputeAsync_NotStepOwner_ReturnsNotStepOwner()
    {
        var (match, aUserId, bUserId, _, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        var step = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.RaiseDisputeAsync(match.Id, step.Id, bUserId, "Arsenal");

        Assert.That(result.Outcome, Is.EqualTo(RaiseChainStepDisputeOutcome.NotStepOwner));
    }

    [Test]
    public async Task REQ1412_RaiseDisputeAsync_BlankClaimedClubName_ReturnsInvalidClaimedClubName()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        var step = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, FixedNow.UtcDateTime);
        var service = BuildService(FixedNow);

        var result = await service.RaiseDisputeAsync(match.Id, step.Id, aUserId, "   ");

        Assert.That(result.Outcome, Is.EqualTo(RaiseChainStepDisputeOutcome.InvalidClaimedClubName));
    }

    // ---- REQ-1412's reopen-an-already-resolved-match behavior ---------------

    [Test]
    public async Task REQ1412_RaiseDisputeAsync_AgainstAlreadyResolvedMatch_ReopensMatch_BeforePersistingDispute()
    {
        var (match, aUserId, bUserId, _, _) = await CreateActiveMatchAsync();
        // B is already terminal (timed out) — the "opponent already
        // terminal" half of the known, accepted consequence this reopen
        // rule exists for.
        await _connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: false, FixedNow.UtcDateTime);
        var firstCandidate = await SeedPlayerAsync("First Attempt Player");
        var retryCandidate = await SeedPlayerAsync("Retry Attempt Player");
        var chainStepService = new ConnectChainStepService(
            _connectMatchRepository, new FakePlayerCareerOverlapService(), _playerRepository,
            _connectMatchLifecycleService, new FixedTimeProvider(FixedNow));
        await chainStepService.SubmitChainStepAsync(match.Id, aUserId, "First Attempt Player");
        var bustResult = await chainStepService.SubmitChainStepAsync(match.Id, aUserId, "Retry Attempt Player");
        Assert.That(bustResult.Outcome, Is.EqualTo(SubmitChainStepOutcome.Busted));

        var resolvedMatch = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(resolvedMatch!.Status, Is.EqualTo(ConnectMatchStatus.Resolved),
            "sanity check: both players are now terminal, so resolution already fired synchronously (the known consequence this reopen rule exists for)");

        var service = BuildService(FixedNow.AddMinutes(5));
        var disputeResult = await service.RaiseDisputeAsync(match.Id, bustResult.ChainStep!.Id, aUserId, "Arsenal");

        Assert.That(disputeResult.Outcome, Is.EqualTo(RaiseChainStepDisputeOutcome.Raised));
        var reopenedMatch = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(reopenedMatch!.Status, Is.EqualTo(ConnectMatchStatus.Active), "reopened, not left Resolved");
        Assert.That(reopenedMatch.Outcome, Is.EqualTo(ConnectMatchOutcome.Pending));
        Assert.That(reopenedMatch.ResolvedAt, Is.Null);
        Assert.That(reopenedMatch.PlayerAScore, Is.Null);
        Assert.That(reopenedMatch.PlayerBScore, Is.Null);
        Assert.That(reopenedMatch.StartedAt, Is.EqualTo(match.StartedAt), "REQ-1405's original clock is untouched");
        Assert.That(reopenedMatch.DeadlineUtc, Is.EqualTo(match.DeadlineUtc), "REQ-1405's original clock is untouched");
    }

    // ---- REQ-1413: opponent review — approve --------------------------------

    [Test]
    public async Task REQ1413_ReviewDisputeAsync_ApprovedFirstFailure_PromotesStepToValid_ClearsBust_ScoresAsCleanPass()
    {
        var (match, aUserId, bUserId, _, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        var step = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, FixedNow.UtcDateTime);
        var disputeService = BuildService(FixedNow);
        var raiseResult = await disputeService.RaiseDisputeAsync(match.Id, step.Id, aUserId, "Arsenal");

        var result = await disputeService.ReviewDisputeAsync(match.Id, raiseResult.Dispute!.Id, bUserId, approve: true);

        Assert.That(result.Outcome, Is.EqualTo(ReviewChainStepDisputeOutcome.Approved));
        var storedStep = (await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId)).Single(s => s.Id == step.Id);
        Assert.That(storedStep.IsValid, Is.True);
        Assert.That(storedStep.MatchedClubName, Is.EqualTo("Arsenal"));
        Assert.That(storedStep.HasPendingDispute, Is.False);

        var storedMatch = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(storedMatch!.PlayerABustedAt, Is.Null, "an approved dispute means the player did NOT actually bust");

        // REQ-1408: zero new scoring logic — an approved first-attempt
        // dispute is a clean pass, exactly like an ordinary successful
        // validation.
        var steps = await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId);
        Assert.That(new ConnectScoringService().CalculateScore(steps), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ1413_ReviewDisputeAsync_ApprovedBustCausingFailure_KeepsExistingPenalty_ScoresLikeOrdinaryRetry()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        var firstCandidate = await SeedPlayerAsync("First Attempt Player");
        var retryCandidate = await SeedPlayerAsync("Retry Attempt Player");
        await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, firstCandidate.Id, FixedNow.UtcDateTime);
        var bustStep = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 2, retryCandidate.Id, FixedNow.UtcDateTime);
        await _connectMatchRepository.MarkPlayerBustedAsync(match.Id, isPlayerA: true, FixedNow.UtcDateTime);
        var disputeService = BuildService(FixedNow);
        var raiseResult = await disputeService.RaiseDisputeAsync(match.Id, bustStep.Id, aUserId, "Arsenal");

        var approveResult = await disputeService.ReviewDisputeAsync(match.Id, raiseResult.Dispute!.Id, match.PlayerBUserId!.Value, approve: true);

        Assert.That(approveResult.Outcome, Is.EqualTo(ReviewChainStepDisputeOutcome.Approved));
        var storedMatch = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(storedMatch!.PlayerABustedAt, Is.Null);

        var steps = await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId);
        Assert.That(new ConnectScoringService().CalculateScore(steps), Is.EqualTo(2),
            "1 valid connector + the +1 penalty already incurred from the first failure at this position — the same as an ordinary successful retry");
    }

    // ---- REQ-1413: opponent review — deny, and the retry-consumption rule ---

    [Test]
    public async Task REQ1413_ReviewDisputeAsync_DeniedFirstFailureDispute_ResultsInBust_NoRestoredRetry()
    {
        var (match, aUserId, bUserId, _, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        var step = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, FixedNow.UtcDateTime);
        var disputeService = BuildService(FixedNow);
        var raiseResult = await disputeService.RaiseDisputeAsync(match.Id, step.Id, aUserId, "Arsenal");

        var denyResult = await disputeService.ReviewDisputeAsync(match.Id, raiseResult.Dispute!.Id, bUserId, approve: false);

        Assert.That(denyResult.Outcome, Is.EqualTo(ReviewChainStepDisputeOutcome.Denied));
        var storedMatch = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(storedMatch!.PlayerABustedAt, Is.EqualTo(FixedNow.UtcDateTime),
            "product-owner confirmation (2026-09-05): a denied dispute of a FIRST failure always results in an immediate bust");

        // No unused retry: submitting again must be rejected as forfeited,
        // never accepted as a fresh attempt 2.
        var chainStepService = new ConnectChainStepService(
            _connectMatchRepository, new FakePlayerCareerOverlapService(), _playerRepository,
            _connectMatchLifecycleService, new FixedTimeProvider(FixedNow));
        var retryAttempt = await chainStepService.SubmitChainStepAsync(match.Id, aUserId, "Anyone");
        Assert.That(retryAttempt.Outcome, Is.EqualTo(SubmitChainStepOutcome.AlreadyForfeited));
    }

    [Test]
    public async Task REQ1413_ReviewDisputeAsync_DeniedDispute_DiscardsTheStep_AndEverythingBuiltAfterItInTheSameChain()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        var firstCandidate = await SeedPlayerAsync("First Position Player");
        await AddValidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, firstCandidate.Id, closesChain: false, FixedNow.UtcDateTime);
        var disputedCandidate = await SeedPlayerAsync("Disputed Position Player");
        var disputedStep = await AddInvalidStepAsync(match.Id, aUserId, position: 2, attemptNumber: 1, disputedCandidate.Id, FixedNow.UtcDateTime);
        var disputeService = BuildService(FixedNow);
        var raiseResult = await disputeService.RaiseDisputeAsync(match.Id, disputedStep.Id, aUserId, "Arsenal");

        // The player continued building on top of the (provisionally valid)
        // disputed step — position 3 is a REAL, ordinarily-valid step.
        var laterCandidate = await SeedPlayerAsync("Later Position Player");
        var laterStep = await AddValidStepAsync(match.Id, aUserId, position: 3, attemptNumber: 1, laterCandidate.Id, closesChain: false, FixedNow.UtcDateTime);

        var denyResult = await disputeService.ReviewDisputeAsync(match.Id, raiseResult.Dispute!.Id, match.PlayerBUserId!.Value, approve: false);

        Assert.That(denyResult.Outcome, Is.EqualTo(ReviewChainStepDisputeOutcome.Denied));
        var storedSteps = await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId);
        Assert.That(storedSteps.Single(s => s.Id == disputedStep.Id).IsValid, Is.False);
        var storedLaterStep = storedSteps.Single(s => s.Id == laterStep.Id);
        Assert.That(storedLaterStep.IsValid, Is.False, "every step the player built AFTER the denied one is discarded too");
        Assert.That(storedLaterStep.ClosesChain, Is.False);
        // Position 1 (BEFORE the disputed one) is untouched.
        Assert.That(storedSteps.Single(s => s.Position == 1).IsValid, Is.True);
    }

    [Test]
    public async Task REQ1413_ReviewDisputeAsync_DeniedDispute_CascadesToDenyAnyStillPendingDisputeOnADiscardedLaterStep()
    {
        var (match, aUserId, bUserId, _, _) = await CreateActiveMatchAsync();
        var disputedCandidate = await SeedPlayerAsync("First Disputed Player");
        var firstDisputedStep = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, disputedCandidate.Id, FixedNow.UtcDateTime);
        var disputeService = BuildService(FixedNow);
        var firstDispute = await disputeService.RaiseDisputeAsync(match.Id, firstDisputedStep.Id, aUserId, "Arsenal");

        // A SECOND, independent dispute at a LATER position — still Pending
        // when the first one below gets Denied.
        var secondDisputedCandidate = await SeedPlayerAsync("Second Disputed Player");
        var secondDisputedStep = await AddInvalidStepAsync(match.Id, aUserId, position: 2, attemptNumber: 1, secondDisputedCandidate.Id, FixedNow.UtcDateTime);
        var secondDispute = await disputeService.RaiseDisputeAsync(match.Id, secondDisputedStep.Id, aUserId, "Chelsea");
        Assert.That(secondDispute.Outcome, Is.EqualTo(RaiseChainStepDisputeOutcome.Raised));

        await disputeService.ReviewDisputeAsync(match.Id, firstDispute.Dispute!.Id, bUserId, approve: false);

        var cascadedDispute = await _connectMatchRepository.GetDisputeByIdAsync(secondDispute.Dispute!.Id);
        Assert.That(cascadedDispute!.Status, Is.EqualTo(ConnectChainStepDisputeStatus.Denied),
            "a dispute on a step that just got discarded must never be left dangling Pending");
        var storedSecondStep = (await _connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, aUserId)).Single(s => s.Id == secondDisputedStep.Id);
        Assert.That(storedSecondStep.HasPendingDispute, Is.False);
    }

    // ---- REQ-1413: only the opponent may review ------------------------------

    [Test]
    public async Task REQ1413_ReviewDisputeAsync_DisputingPlayerCannotReviewTheirOwnDispute()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        var step = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, FixedNow.UtcDateTime);
        var disputeService = BuildService(FixedNow);
        var raiseResult = await disputeService.RaiseDisputeAsync(match.Id, step.Id, aUserId, "Arsenal");

        var result = await disputeService.ReviewDisputeAsync(match.Id, raiseResult.Dispute!.Id, aUserId, approve: true);

        Assert.That(result.Outcome, Is.EqualTo(ReviewChainStepDisputeOutcome.CannotReviewOwnDispute));
    }

    [Test]
    public async Task REQ1413_ReviewDisputeAsync_ThirdPartyNotAParticipant_ReturnsNotAParticipant()
    {
        var (match, aUserId, _, _, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        var step = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, FixedNow.UtcDateTime);
        var disputeService = BuildService(FixedNow);
        var raiseResult = await disputeService.RaiseDisputeAsync(match.Id, step.Id, aUserId, "Arsenal");

        var outsider = Guid.NewGuid();
        var result = await disputeService.ReviewDisputeAsync(match.Id, raiseResult.Dispute!.Id, outsider, approve: true);

        Assert.That(result.Outcome, Is.EqualTo(ReviewChainStepDisputeOutcome.NotAParticipant));
    }

    [Test]
    public async Task REQ1413_ReviewDisputeAsync_AlreadyReviewed_ReturnsAlreadyReviewed()
    {
        var (match, aUserId, bUserId, _, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        var step = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, FixedNow.UtcDateTime);
        var disputeService = BuildService(FixedNow);
        var raiseResult = await disputeService.RaiseDisputeAsync(match.Id, step.Id, aUserId, "Arsenal");
        await disputeService.ReviewDisputeAsync(match.Id, raiseResult.Dispute!.Id, bUserId, approve: true);

        var result = await disputeService.ReviewDisputeAsync(match.Id, raiseResult.Dispute!.Id, bUserId, approve: false);

        Assert.That(result.Outcome, Is.EqualTo(ReviewChainStepDisputeOutcome.AlreadyReviewed));
    }

    // ---- REQ-1414: the data-correction suggestion by-product ----------------

    [Test]
    public async Task REQ1414_ReviewDisputeAsync_Approved_RecordsDataCorrectionSuggestion()
    {
        var (match, aUserId, bUserId, aTargetPlayerId, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        var step = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, FixedNow.UtcDateTime);
        var disputeService = BuildService(FixedNow);
        var raiseResult = await disputeService.RaiseDisputeAsync(match.Id, step.Id, aUserId, "Arsenal");

        await disputeService.ReviewDisputeAsync(match.Id, raiseResult.Dispute!.Id, bUserId, approve: true);

        var suggestions = await _connectMatchRepository.GetAllDataCorrectionSuggestionsAsync();
        Assert.That(suggestions, Has.Count.EqualTo(1));
        var suggestion = suggestions[0];
        Assert.That(suggestion.ConnectMatchId, Is.EqualTo(match.Id));
        Assert.That(suggestion.ConnectChainStepId, Is.EqualTo(step.Id));
        Assert.That(suggestion.ConnectChainStepDisputeId, Is.EqualTo(raiseResult.Dispute.Id));
        Assert.That(suggestion.CandidatePlayerId, Is.EqualTo(candidate.Id));
        Assert.That(suggestion.PrecedingPlayerId, Is.EqualTo(aTargetPlayerId), "position 1's preceding player is the caller's own target pick");
        Assert.That(suggestion.ClaimedClubName, Is.EqualTo("Arsenal"));
    }

    [Test]
    public async Task REQ1414_ReviewDisputeAsync_Denied_NeverRecordsASuggestion()
    {
        var (match, aUserId, bUserId, _, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        var step = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, FixedNow.UtcDateTime);
        var disputeService = BuildService(FixedNow);
        var raiseResult = await disputeService.RaiseDisputeAsync(match.Id, step.Id, aUserId, "Arsenal");

        await disputeService.ReviewDisputeAsync(match.Id, raiseResult.Dispute!.Id, bUserId, approve: false);

        Assert.That(await _connectMatchRepository.GetAllDataCorrectionSuggestionsAsync(), Is.Empty);
    }

    // ---- GetDisputesForMatchAsync ---------------------------------------------

    [Test]
    public async Task REQ1412_GetDisputesForMatchAsync_ReturnsEveryDisputeInMatch_WithRaisedByMePerspective()
    {
        var (match, aUserId, bUserId, _, _) = await CreateActiveMatchAsync();
        var candidate = await SeedPlayerAsync("Middle Link Player");
        var step = await AddInvalidStepAsync(match.Id, aUserId, position: 1, attemptNumber: 1, candidate.Id, FixedNow.UtcDateTime);
        var disputeService = BuildService(FixedNow);
        await disputeService.RaiseDisputeAsync(match.Id, step.Id, aUserId, "Arsenal");

        var asDisputer = await disputeService.GetDisputesForMatchAsync(match.Id, aUserId);
        var asOpponent = await disputeService.GetDisputesForMatchAsync(match.Id, bUserId);

        Assert.That(asDisputer.Outcome, Is.EqualTo(GetChainStepDisputesOutcome.Found));
        Assert.That(asDisputer.Disputes, Has.Count.EqualTo(1));
        Assert.That(asDisputer.Disputes[0].RaisedByMe, Is.True);
        Assert.That(asOpponent.Disputes[0].RaisedByMe, Is.False);
    }
}

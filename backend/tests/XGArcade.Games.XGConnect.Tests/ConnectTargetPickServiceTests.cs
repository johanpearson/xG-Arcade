using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect.Tests;

// REQ-1404 (docs/requirements-document.md §4.15): xG Connect's target-pick
// selection business logic. Same no-mocking-framework, real-InMemory-backed-
// repository pattern as ChallengeServiceTests/MatchmakingSweepServiceTests —
// IConnectMatchRepository is exercised through the real ConnectMatchRepository
// against an InMemory-backed XGArcadeDbContext; IPlayerCareerOverlapService is
// hand-rolled-faked (FakePlayerCareerOverlapService) since its own overlap-
// detection logic gets dedicated, direct coverage in
// PlayerCareerOverlapServiceTests.cs — this file is only concerned with
// SubmitTargetPickAsync's own orchestration around that check's result.
public class ConnectTargetPickServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private XGArcadeDbContext _dbContext = null!;
    private IConnectMatchRepository _connectMatchRepository = null!;
    private FakePlayerCareerOverlapService _overlapService = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _connectMatchRepository = new ConnectMatchRepository(_dbContext);
        _overlapService = new FakePlayerCareerOverlapService();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private ConnectTargetPickService BuildService(DateTimeOffset now) =>
        new(_connectMatchRepository, _overlapService, new FixedTimeProvider(now));

    private async Task<ConnectMatch> CreateMatchAsync(Guid playerAUserId, Guid playerBUserId)
    {
        var match = new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = playerAUserId,
            PlayerBUserId = playerBUserId,
            CreatedAt = FixedNow.UtcDateTime,
        };
        return await _connectMatchRepository.AddMatchAsync(match);
    }

    // ---- REQ-1404 GWT#1: a fresh selection is recorded for that player only,
    // ---- not visible to or constraining the other player's own independent
    // ---- selection ----------------------------------------------------------

    [Test]
    public async Task REQ1404_SubmitTargetPickAsync_FirstSelection_RecordsForCallerOnly_AwaitingOther()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var targetPlayerId = Guid.NewGuid();
        var service = BuildService(FixedNow);

        var result = await service.SubmitTargetPickAsync(match.Id, playerA, targetPlayerId);

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.RecordedAwaitingOther));
        Assert.That(result.TargetPick, Is.Not.Null);
        Assert.That(result.TargetPick!.TargetPlayerId, Is.EqualTo(targetPlayerId));
        Assert.That(result.TargetPick.IsLocked, Is.False);
        // Not visible to or constraining the other player's own independent
        // selection — no row exists for player B, and no overlap check ran
        // (nothing yet to compare against).
        Assert.That(await _connectMatchRepository.GetTargetPickAsync(match.Id, playerB), Is.Null);
        Assert.That(_overlapService.Calls, Is.Empty);
    }

    // ---- REQ-1404 GWT#2: free pre-lock resubmission — replacing an unlocked
    // ---- pick before the other player has picked is unrestricted -----------

    [Test]
    public async Task REQ1404_SubmitTargetPickAsync_ResubmissionBeforeOtherPlayerPicks_FreelyReplacesPriorPick()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var firstPick = Guid.NewGuid();
        var secondPick = Guid.NewGuid();
        var firstService = BuildService(FixedNow);
        await firstService.SubmitTargetPickAsync(match.Id, playerA, firstPick);

        var laterNow = FixedNow.AddMinutes(5);
        var secondService = BuildService(laterNow);
        var result = await secondService.SubmitTargetPickAsync(match.Id, playerA, secondPick);

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.RecordedAwaitingOther));
        Assert.That(result.TargetPick!.TargetPlayerId, Is.EqualTo(secondPick));
        Assert.That(result.TargetPick.IsLocked, Is.False);

        // No lock, no penalty: exactly one row for player A, now holding the
        // replaced pick — never a second row for the same match/user pair.
        var picks = await _connectMatchRepository.GetTargetPicksForMatchAsync(match.Id);
        Assert.That(picks, Has.Count.EqualTo(1));
        Assert.That(picks[0].TargetPlayerId, Is.EqualTo(secondPick));
        Assert.That(picks[0].SelectedAt, Is.EqualTo(laterNow.UtcDateTime));
        Assert.That(picks[0].IsLocked, Is.False);
    }

    // ---- REQ-1404 GWT#3: the completing (second) selection triggers the
    // ---- shared-club-overlap check between the two target picks -----------

    [Test]
    public async Task REQ1404_SubmitTargetPickAsync_CompletingSelection_ChecksOverlapBetweenBothTargetPicks()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var aTarget = Guid.NewGuid();
        var bTarget = Guid.NewGuid();
        var service = BuildService(FixedNow);
        await service.SubmitTargetPickAsync(match.Id, playerA, aTarget);

        await service.SubmitTargetPickAsync(match.Id, playerB, bTarget);

        Assert.That(_overlapService.Calls, Has.Count.EqualTo(1));
        Assert.That(_overlapService.Calls[0], Is.EqualTo((bTarget, aTarget)),
            "the completing submission's own candidate target must be checked against the other participant's already-stored pick");
    }

    // ---- REQ-1404 GWT#4: a trivially-connected pair rejects the completing
    // ---- selection, leaves the first player's own pick completely
    // ---- unaffected, and the match does not start --------------------------

    [Test]
    public async Task REQ1404_SubmitTargetPickAsync_TriviallyConnectedCompletingPick_RejectsAndLeavesFirstPlayersPickUnchanged()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var aTarget = Guid.NewGuid();
        var bTarget = Guid.NewGuid();
        var firstService = BuildService(FixedNow);
        await firstService.SubmitTargetPickAsync(match.Id, playerA, aTarget);
        _overlapService.SetOverlap(aTarget, bTarget, overlaps: true);

        var laterNow = FixedNow.AddMinutes(5);
        var secondService = BuildService(laterNow);
        var result = await secondService.SubmitTargetPickAsync(match.Id, playerB, bTarget);

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.TriviallyConnected));
        Assert.That(result.TargetPick, Is.Null);

        // The first player's own selection survives a rejected second pick,
        // completely unchanged — same value, same original timestamp, still
        // unlocked.
        var playerAPick = await _connectMatchRepository.GetTargetPickAsync(match.Id, playerA);
        Assert.That(playerAPick, Is.Not.Null);
        Assert.That(playerAPick!.TargetPlayerId, Is.EqualTo(aTarget));
        Assert.That(playerAPick.SelectedAt, Is.EqualTo(FixedNow.UtcDateTime));
        Assert.That(playerAPick.IsLocked, Is.False);

        // Nothing was ever persisted for the rejected completing submission.
        Assert.That(await _connectMatchRepository.GetTargetPickAsync(match.Id, playerB), Is.Null);
        var picks = await _connectMatchRepository.GetTargetPicksForMatchAsync(match.Id);
        Assert.That(picks, Has.Count.EqualTo(1), "a rejected trivial-pair completing pick must never be persisted");
    }

    // ---- REQ-1404 GWT#5: a non-trivial pair locks both selections in -------

    [Test]
    public async Task REQ1404_SubmitTargetPickAsync_NonTriviallyConnectedCompletingPick_LocksBothTargetPicks()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var aTarget = Guid.NewGuid();
        var bTarget = Guid.NewGuid();
        var service = BuildService(FixedNow);
        await service.SubmitTargetPickAsync(match.Id, playerA, aTarget);
        // No SetOverlap call — FakePlayerCareerOverlapService defaults an
        // unconfigured pair to "no overlap."

        var result = await service.SubmitTargetPickAsync(match.Id, playerB, bTarget);

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.RecordedAndLocked));
        Assert.That(result.TargetPick!.IsLocked, Is.True);

        // Both rows, re-read from the repository (not just the returned
        // object), must end up locked — the puzzle is fixed for both
        // participants, not just the caller.
        var picks = await _connectMatchRepository.GetTargetPicksForMatchAsync(match.Id);
        Assert.That(picks, Has.Count.EqualTo(2));
        Assert.That(picks.All(p => p.IsLocked), Is.True);
    }

    // ---- Mechanical branches beyond REQ-1404's literal Given/When/Then -----

    [Test]
    public async Task REQ1404_SubmitTargetPickAsync_MatchNotFound_ReturnsMatchNotFoundOutcome()
    {
        var service = BuildService(FixedNow);

        var result = await service.SubmitTargetPickAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.MatchNotFound));
        Assert.That(result.TargetPick, Is.Null);
    }

    [Test]
    public async Task REQ1404_SubmitTargetPickAsync_CallerNotAParticipant_ReturnsNotAParticipantOutcome()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var outsider = Guid.NewGuid();
        var service = BuildService(FixedNow);

        var result = await service.SubmitTargetPickAsync(match.Id, outsider, Guid.NewGuid());

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.NotAParticipant));
        Assert.That(result.TargetPick, Is.Null);
        Assert.That(await _connectMatchRepository.GetTargetPicksForMatchAsync(match.Id), Is.Empty);
    }

    [Test]
    public async Task REQ1404_SubmitTargetPickAsync_CallerAlreadyLocked_ReturnsAlreadyLockedOutcome_DoesNotOverwrite()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var aTarget = Guid.NewGuid();
        var bTarget = Guid.NewGuid();
        var service = BuildService(FixedNow);
        await service.SubmitTargetPickAsync(match.Id, playerA, aTarget);
        await service.SubmitTargetPickAsync(match.Id, playerB, bTarget); // locks both, no overlap configured.

        var attemptedReplacement = Guid.NewGuid();
        var result = await service.SubmitTargetPickAsync(match.Id, playerA, attemptedReplacement);

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.AlreadyLocked));
        Assert.That(result.TargetPick, Is.Null);

        var playerAPick = await _connectMatchRepository.GetTargetPickAsync(match.Id, playerA);
        Assert.That(playerAPick!.TargetPlayerId, Is.EqualTo(aTarget), "an already-locked pick can never be replaced");
        // Only the one completing-selection overlap check from setup — the
        // already-locked attempt must short-circuit before ever reaching the
        // overlap check again.
        Assert.That(_overlapService.Calls, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task REQ1404_SubmitTargetPickAsync_LiveLookupUnavailable_PersistsNothing()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var aTarget = Guid.NewGuid();
        var bTarget = Guid.NewGuid();
        var firstService = BuildService(FixedNow);
        await firstService.SubmitTargetPickAsync(match.Id, playerA, aTarget);
        _overlapService.SetLiveLookupUnavailable(aTarget, bTarget);

        var secondService = BuildService(FixedNow.AddMinutes(5));
        var result = await secondService.SubmitTargetPickAsync(match.Id, playerB, bTarget);

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.LiveLookupUnavailable));
        Assert.That(result.TargetPick, Is.Null);

        // Nothing persisted for the caller (player B) — the timeout means
        // this pair's connectivity is genuinely unknown, not a rejection of
        // anything the player did, so no repository write happened at all.
        Assert.That(await _connectMatchRepository.GetTargetPickAsync(match.Id, playerB), Is.Null);
        var picks = await _connectMatchRepository.GetTargetPicksForMatchAsync(match.Id);
        Assert.That(picks, Has.Count.EqualTo(1), "only player A's original, untouched pick must exist after a live-lookup-unavailable rejection");
        Assert.That(picks[0].TargetPlayerId, Is.EqualTo(aTarget));
        Assert.That(picks[0].IsLocked, Is.False);
    }
}

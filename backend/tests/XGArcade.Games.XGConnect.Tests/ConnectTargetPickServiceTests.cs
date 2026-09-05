using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.TestSupport;

namespace XGArcade.Games.XGConnect.Tests;

// REQ-1404 (docs/requirements-document.md §4.15): xG Connect's target-pick
// selection business logic. Same no-mocking-framework, real-InMemory-backed-
// repository pattern as ChallengeServiceTests/MatchmakingSweepServiceTests —
// IConnectMatchRepository/IPlayerRepository are exercised through their real
// implementations (ConnectMatchRepository/PlayerRepository) against an
// InMemory-backed XGArcadeDbContext; IPlayerCareerOverlapService is
// hand-rolled-faked (FakePlayerCareerOverlapService) since its own overlap-
// detection logic gets dedicated, direct coverage in
// PlayerCareerOverlapServiceTests.cs — this file is only concerned with
// SubmitTargetPickAsync's own orchestration around that check's result.
//
// Bug fix (S-218 prep, ADR-0007): SubmitTargetPickAsync now takes a player
// NAME, resolved via IPlayerRepository the same way
// ConnectChainStepServiceTests already seeds/resolves candidatePlayerName —
// every test below seeds a real Player row via SeedPlayerAsync and submits
// its FullName, never a bare Guid.
public class ConnectTargetPickServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private XGArcadeDbContext _dbContext = null!;
    private IConnectMatchRepository _connectMatchRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private FakePlayerCareerOverlapService _overlapService = null!;

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
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    // REQ-1405/S-212: a real ConnectMatchLifecycleService, backed by the
    // same InMemory _connectMatchRepository as the rest of this fixture and
    // a real ConnectScoringService (S-214's own pure calculation, no fake
    // needed for the same "real service, no external dependency" reasoning)
    // — sharing the same `now` as the ConnectTargetPickService instance
    // under test so the match-start timestamp this test asserts on is
    // deterministic.
    private ConnectTargetPickService BuildService(DateTimeOffset now) =>
        new(_connectMatchRepository, _overlapService, _playerRepository,
            new ConnectMatchLifecycleService(_connectMatchRepository, new ConnectScoringService(), new FixedTimeProvider(now)),
            new FixedTimeProvider(now));

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

    private async Task<Player> SeedPlayerAsync(string fullName) =>
        await _playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = fullName });

    // ---- REQ-1404 GWT#1: a fresh selection is recorded for that player only,
    // ---- not visible to or constraining the other player's own independent
    // ---- selection ----------------------------------------------------------

    [Test]
    public async Task REQ1404_SubmitTargetPickAsync_FirstSelection_RecordsForCallerOnly_AwaitingOther()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var targetPlayer = await SeedPlayerAsync("Target Player");
        var service = BuildService(FixedNow);

        var result = await service.SubmitTargetPickAsync(match.Id, playerA, targetPlayer.FullName);

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.RecordedAwaitingOther));
        Assert.That(result.TargetPick, Is.Not.Null);
        Assert.That(result.TargetPick!.TargetPlayerId, Is.EqualTo(targetPlayer.Id));
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
        var firstPick = await SeedPlayerAsync("First Pick");
        var secondPick = await SeedPlayerAsync("Second Pick");
        var firstService = BuildService(FixedNow);
        await firstService.SubmitTargetPickAsync(match.Id, playerA, firstPick.FullName);

        var laterNow = FixedNow.AddMinutes(5);
        var secondService = BuildService(laterNow);
        var result = await secondService.SubmitTargetPickAsync(match.Id, playerA, secondPick.FullName);

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.RecordedAwaitingOther));
        Assert.That(result.TargetPick!.TargetPlayerId, Is.EqualTo(secondPick.Id));
        Assert.That(result.TargetPick.IsLocked, Is.False);

        // No lock, no penalty: exactly one row for player A, now holding the
        // replaced pick — never a second row for the same match/user pair.
        var picks = await _connectMatchRepository.GetTargetPicksForMatchAsync(match.Id);
        Assert.That(picks, Has.Count.EqualTo(1));
        Assert.That(picks[0].TargetPlayerId, Is.EqualTo(secondPick.Id));
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
        var aTarget = await SeedPlayerAsync("A Target");
        var bTarget = await SeedPlayerAsync("B Target");
        var service = BuildService(FixedNow);
        await service.SubmitTargetPickAsync(match.Id, playerA, aTarget.FullName);

        await service.SubmitTargetPickAsync(match.Id, playerB, bTarget.FullName);

        Assert.That(_overlapService.Calls, Has.Count.EqualTo(1));
        Assert.That(_overlapService.Calls[0], Is.EqualTo((bTarget.Id, aTarget.Id)),
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
        var aTarget = await SeedPlayerAsync("A Target");
        var bTarget = await SeedPlayerAsync("B Target");
        var firstService = BuildService(FixedNow);
        await firstService.SubmitTargetPickAsync(match.Id, playerA, aTarget.FullName);
        _overlapService.SetOverlap(aTarget.Id, bTarget.Id, overlaps: true);

        var laterNow = FixedNow.AddMinutes(5);
        var secondService = BuildService(laterNow);
        var result = await secondService.SubmitTargetPickAsync(match.Id, playerB, bTarget.FullName);

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.TriviallyConnected));
        Assert.That(result.TargetPick, Is.Null);

        // The first player's own selection survives a rejected second pick,
        // completely unchanged — same value, same original timestamp, still
        // unlocked.
        var playerAPick = await _connectMatchRepository.GetTargetPickAsync(match.Id, playerA);
        Assert.That(playerAPick, Is.Not.Null);
        Assert.That(playerAPick!.TargetPlayerId, Is.EqualTo(aTarget.Id));
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
        var aTarget = await SeedPlayerAsync("A Target");
        var bTarget = await SeedPlayerAsync("B Target");
        var service = BuildService(FixedNow);
        await service.SubmitTargetPickAsync(match.Id, playerA, aTarget.FullName);
        // No SetOverlap call — FakePlayerCareerOverlapService defaults an
        // unconfigured pair to "no overlap."

        var result = await service.SubmitTargetPickAsync(match.Id, playerB, bTarget.FullName);

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.RecordedAndLocked));
        Assert.That(result.TargetPick!.IsLocked, Is.True);

        // Both rows, re-read from the repository (not just the returned
        // object), must end up locked — the puzzle is fixed for both
        // participants, not just the caller.
        var picks = await _connectMatchRepository.GetTargetPicksForMatchAsync(match.Id);
        Assert.That(picks, Has.Count.EqualTo(2));
        Assert.That(picks.All(p => p.IsLocked), Is.True);
    }

    // ---- REQ-1405: the completing pick's lock also starts the match's
    // ---- shared 6h forfeit clock, from that same instant -------------------

    [Test]
    public async Task REQ1405_SubmitTargetPickAsync_NonTriviallyConnectedCompletingPick_StartsMatchWithSixHourDeadline()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var aTarget = await SeedPlayerAsync("A Target");
        var bTarget = await SeedPlayerAsync("B Target");
        var firstService = BuildService(FixedNow);
        await firstService.SubmitTargetPickAsync(match.Id, playerA, aTarget.FullName);

        var completingAt = FixedNow.AddMinutes(5);
        var secondService = BuildService(completingAt);
        var result = await secondService.SubmitTargetPickAsync(match.Id, playerB, bTarget.FullName);

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.RecordedAndLocked));
        var storedMatch = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(storedMatch, Is.Not.Null);
        Assert.That(storedMatch!.Status, Is.EqualTo(ConnectMatchStatus.Active));
        Assert.That(storedMatch.StartedAt, Is.EqualTo(completingAt.UtcDateTime));
        Assert.That(storedMatch.DeadlineUtc, Is.EqualTo(completingAt.UtcDateTime.AddHours(6)));
    }

    // ---- REQ-1405: a first-selection-only pick (no completing pick yet)
    // ---- never starts the match ---------------------------------------------

    [Test]
    public async Task REQ1405_SubmitTargetPickAsync_OnlyOnePickSoFar_DoesNotStartMatch()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var targetPlayer = await SeedPlayerAsync("Target Player");
        var service = BuildService(FixedNow);

        await service.SubmitTargetPickAsync(match.Id, playerA, targetPlayer.FullName);

        var storedMatch = await _connectMatchRepository.GetMatchByIdAsync(match.Id);
        Assert.That(storedMatch!.Status, Is.EqualTo(ConnectMatchStatus.AwaitingTargetPicks));
        Assert.That(storedMatch.StartedAt, Is.Null);
        Assert.That(storedMatch.DeadlineUtc, Is.Null);
    }

    // ---- Mechanical branches beyond REQ-1404's literal Given/When/Then -----

    [Test]
    public async Task REQ1404_SubmitTargetPickAsync_MatchNotFound_ReturnsMatchNotFoundOutcome()
    {
        var service = BuildService(FixedNow);

        var result = await service.SubmitTargetPickAsync(Guid.NewGuid(), Guid.NewGuid(), "Anyone");

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

        var result = await service.SubmitTargetPickAsync(match.Id, outsider, "Anyone");

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
        var aTarget = await SeedPlayerAsync("A Target");
        var bTarget = await SeedPlayerAsync("B Target");
        var service = BuildService(FixedNow);
        await service.SubmitTargetPickAsync(match.Id, playerA, aTarget.FullName);
        await service.SubmitTargetPickAsync(match.Id, playerB, bTarget.FullName); // locks both, no overlap configured.

        // The already-locked short-circuit runs BEFORE name resolution, so
        // an unresolvable name here proves that ordering rather than just
        // being incidental.
        var result = await service.SubmitTargetPickAsync(match.Id, playerA, "Nobody Real");

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.AlreadyLocked));
        Assert.That(result.TargetPick, Is.Null);

        var playerAPick = await _connectMatchRepository.GetTargetPickAsync(match.Id, playerA);
        Assert.That(playerAPick!.TargetPlayerId, Is.EqualTo(aTarget.Id), "an already-locked pick can never be replaced");
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
        var aTarget = await SeedPlayerAsync("A Target");
        var bTarget = await SeedPlayerAsync("B Target");
        var firstService = BuildService(FixedNow);
        await firstService.SubmitTargetPickAsync(match.Id, playerA, aTarget.FullName);
        _overlapService.SetLiveLookupUnavailable(aTarget.Id, bTarget.Id);

        var secondService = BuildService(FixedNow.AddMinutes(5));
        var result = await secondService.SubmitTargetPickAsync(match.Id, playerB, bTarget.FullName);

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.LiveLookupUnavailable));
        Assert.That(result.TargetPick, Is.Null);

        // Nothing persisted for the caller (player B) — the timeout means
        // this pair's connectivity is genuinely unknown, not a rejection of
        // anything the player did, so no repository write happened at all.
        Assert.That(await _connectMatchRepository.GetTargetPickAsync(match.Id, playerB), Is.Null);
        var picks = await _connectMatchRepository.GetTargetPicksForMatchAsync(match.Id);
        Assert.That(picks, Has.Count.EqualTo(1), "only player A's original, untouched pick must exist after a live-lookup-unavailable rejection");
        Assert.That(picks[0].TargetPlayerId, Is.EqualTo(aTarget.Id));
        Assert.That(picks[0].IsLocked, Is.False);
    }

    // ---- Bug fix (S-218 prep, ADR-0007): name resolution branches, mirroring
    // ---- ConnectChainStepServiceTests' own CandidateNotFound coverage ------

    [Test]
    public async Task REQ1404_SubmitTargetPickAsync_TargetPlayerNameDoesNotResolveToAnyPlayer_ReturnsTargetPlayerNotFound_PersistsNothing()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var service = BuildService(FixedNow);

        var result = await service.SubmitTargetPickAsync(match.Id, playerA, "Nobody Real");

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.TargetPlayerNotFound));
        Assert.That(result.TargetPick, Is.Null);
        Assert.That(await _connectMatchRepository.GetTargetPicksForMatchAsync(match.Id), Is.Empty);
        Assert.That(_overlapService.Calls, Is.Empty);
    }

    // Same "no client-supplied disambiguation id, deterministically pick the
    // lowest Id" fallback ConnectCandidateResolver still applies when no
    // targetWikidataQid is supplied (see ADR-0107, and the tests just below
    // for the QID-supplied case that supersedes this whenever it can) —
    // proven here via two same-named Player rows, asserting the LOWER Id
    // wins regardless of insertion order.
    [Test]
    public async Task REQ1404_SubmitTargetPickAsync_NameResolvesToMultiplePlayers_PicksLowestIdDeterministically()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var higherId = await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), FullName = "Same Name Player" });
        var lowerId = await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), FullName = "Same Name Player" });
        var service = BuildService(FixedNow);

        var result = await service.SubmitTargetPickAsync(match.Id, playerA, "Same Name Player");

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.RecordedAwaitingOther));
        Assert.That(result.TargetPick!.TargetPlayerId, Is.EqualTo(lowerId.Id));
        Assert.That(result.TargetPick.TargetPlayerId, Is.Not.EqualTo(higherId.Id));
    }

    // ---- Bug fix (2026-09-05, ADR-0107): targetWikidataQid resolves the
    // ---- exact real person unambiguously on a same-name collision ---------

    [Test]
    public async Task ADR0107_SubmitTargetPickAsync_SameNameCollision_TargetWikidataQidResolvesTheCorrectPlayer_NotTheLowestId()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var wrongPlayer = await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), FullName = "Jonas Olsson", WikidataQid = "Q1" });
        var correctPlayer = await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), FullName = "Jonas Olsson", WikidataQid = "Q2" });
        Assert.That(wrongPlayer.Id, Is.LessThan(correctPlayer.Id));
        var service = BuildService(FixedNow);

        var result = await service.SubmitTargetPickAsync(match.Id, playerA, "Jonas Olsson", "Q2");

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.RecordedAwaitingOther));
        Assert.That(result.TargetPick!.TargetPlayerId, Is.EqualTo(correctPlayer.Id));
    }

    [Test]
    public async Task ADR0107_SubmitTargetPickAsync_TargetWikidataQid_ForPlayerNeverBeforeReferenced_GetOrCreatesTheRealPlayerRow()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var service = BuildService(FixedNow);

        var result = await service.SubmitTargetPickAsync(match.Id, playerA, "Never Referenced Player", "Q999");

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.RecordedAwaitingOther));
        var createdPlayer = await _playerRepository.GetPlayerByWikidataQidAsync("Q999");
        Assert.That(createdPlayer, Is.Not.Null);
        Assert.That(result.TargetPick!.TargetPlayerId, Is.EqualTo(createdPlayer!.Id));
    }

    [Test]
    public async Task ADR0107_SubmitTargetPickAsync_MalformedTargetWikidataQid_FallsBackToNameOnlyResolution_NeverErrors()
    {
        // Mirrors ConnectChainStepServiceTests's own malformed-QID coverage
        // — a malformed value (never sent by this codebase's own frontend,
        // but the request body is client-controlled) must not be trusted
        // blindly or crash the request — it degrades to the same fallback
        // as "none supplied," which still has a working resolution path.
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var match = await CreateMatchAsync(playerA, playerB);
        var target = await SeedPlayerAsync("Alpha Target");
        var service = BuildService(FixedNow);

        var result = await service.SubmitTargetPickAsync(match.Id, playerA, target.FullName, "not-a-real-qid");

        Assert.That(result.Outcome, Is.EqualTo(SubmitTargetPickOutcome.RecordedAwaitingOther));
        Assert.That(result.TargetPick!.TargetPlayerId, Is.EqualTo(target.Id));
    }
}

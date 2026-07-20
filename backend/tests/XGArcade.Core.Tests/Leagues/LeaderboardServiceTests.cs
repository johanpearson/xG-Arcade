using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Core.Leagues;
using XGArcade.Core.Scoring;
using XGArcade.Core.Tests.Rounds;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Tests.Leagues;

// REQ-401/404 (docs/requirements-document.md §4.4): Core.Leagues' first real
// code (S-011) — global league auto-membership plus its leaderboard read.
// REQ-607/S-034 added pagination (docs/backlog.md S-034) — the same tests
// were updated in place for the now-paginated response shape rather than
// duplicated, per this repo's convention.
// REQ-406/407/408 (2026-07-19, ADR-0031/backlog S-053/S-054) added the
// active-round live contribution (folded into this same method for REQ-406,
// and its own standalone scope for REQ-407) and past-closed-round browsing
// (REQ-408) — updated the existing constructor/method signature in place and
// added new REQ406/REQ407/REQ408-named cases below, rather than duplicating
// this whole file.
// Same no-mocking-framework, real-InMemory-backed-repository pattern as
// RoundCloseServiceScoringTests. Reuses FakeGameModule from
// XGArcade.Core.Tests.Rounds (internal, same-assembly-visible) rather than
// inventing a second game-module fake.
public class LeaderboardServiceTests
{
    private const string GameKey = "xg-grid";

    private XGArcadeDbContext _dbContext = null!;
    private ILeagueRepository _leagueRepository = null!;
    private IUserRepository _userRepository = null!;
    private IGuessRepository _guessRepository = null!;
    private IRoundRepository _roundRepository = null!;
    private FakeGameModule _fakeGameModule = null!;
    private LeaderboardService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _leagueRepository = new LeagueRepository(_dbContext);
        _userRepository = new UserRepository(_dbContext);
        _guessRepository = new GuessRepository(_dbContext);
        _roundRepository = new RoundRepository(_dbContext);
        // Defaults to no cells — tests exercising the live contribution set
        // GetCellIdsResult explicitly (same convention as
        // RoundCloseServiceScoringTests).
        _fakeGameModule = new FakeGameModule(GameKey);
        var gameModuleResolver = new GameModuleResolver([_fakeGameModule]);
        var liveRoundContributionService = new LiveRoundContributionService(_guessRepository, gameModuleResolver);
        _service = new LeaderboardService(_leagueRepository, _userRepository, _guessRepository, _roundRepository, liveRoundContributionService);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private async Task<User> SeedMemberAsync(string displayName)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = $"{Guid.NewGuid()}@example.com",
            DisplayName = displayName,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var globalLeague = await _leagueRepository.GetOrCreateGlobalLeagueAsync();
        await _leagueRepository.AddMembershipAsync(globalLeague.Id, user.Id);
        return user;
    }

    private async Task SeedLockedGuessAsync(Guid userId, int finalPoints)
    {
        _dbContext.Guesses.Add(new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = Guid.NewGuid(),
            UserId = userId,
            CellId = Guid.NewGuid(),
            SubmittedName = "Someone",
            IsCorrect = true,
            AttemptCount = 1,
            FinalUniquenessScore = finalPoints / 100.0,
            FinalPoints = finalPoints,
            CreatedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task<Round> SeedRoundAsync(DateTime startTime, DateTime endTime, DateTime? closedAt = null)
    {
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GameKey,
            GameInstanceId = Guid.NewGuid(),
            StartTime = startTime,
            EndTime = endTime,
            AllowGuessChange = true,
            ClosedAt = closedAt,
        };
        _dbContext.Rounds.Add(round);
        await _dbContext.SaveChangesAsync();
        return round;
    }

    private async Task<Guess> SeedGuessAsync(
        Guid roundId, Guid userId, Guid cellId, bool isCorrect, int attemptCount, Guid? playerAnswerId = null, int? finalPoints = null)
    {
        var guess = new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = roundId,
            UserId = userId,
            CellId = cellId,
            SubmittedName = "Someone",
            PlayerAnswerId = playerAnswerId,
            IsCorrect = isCorrect,
            AttemptCount = attemptCount,
            FinalPoints = finalPoints,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Guesses.Add(guess);
        await _dbContext.SaveChangesAsync();
        return guess;
    }

    [Test]
    public async Task REQ401_GetGlobalLeaderboardAsync_NewMemberWithNoGuessesEver_ExcludedEntirelyFromRankedList()
    {
        // 2026-07-20 (REQ-401/404 status note): a member for whom no Guess
        // row has ever existed must be excluded from the ranked list
        // entirely, not shown ranked with a default total of 0 (which
        // ADR-0021's lowest-wins model would otherwise treat as the BEST
        // possible score, letting a never-played member rank #1).
        var member = await SeedMemberAsync("Alex");

        var page = await _service.GetGlobalLeaderboardAsync(member.Id, cursor: 0, pageSize: 50, activeRound: null);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.RequestingUserEntry, Is.Null);
        Assert.That(page.HasMore, Is.False);
        Assert.That(page.NextCursor, Is.Null);
    }

    [Test]
    public async Task REQ404_GetGlobalLeaderboardAsync_MemberWithLockedGuessTotalingZero_StillRankedNormallyNotExcluded()
    {
        // Distinguishes "has played, total happens to be 0" (a real,
        // ranked-normally case — e.g. the rarest possible correct guess
        // scores 0, ADR-0021) from "never played at all" (excluded, REQ-401/
        // 404's 2026-07-20 change) — both currently compute to the same
        // TotalPoints of 0, so only presence/absence from Rows tells them
        // apart.
        var neverPlayed = await SeedMemberAsync("NeverPlayed");
        var playedForZero = await SeedMemberAsync("PlayedForZero");
        await SeedLockedGuessAsync(playedForZero.Id, finalPoints: 0);

        var page = await _service.GetGlobalLeaderboardAsync(playedForZero.Id, cursor: 0, pageSize: 50, activeRound: null);

        Assert.That(page.Rows, Has.Count.EqualTo(1));
        Assert.That(page.Rows[0].UserId, Is.EqualTo(playedForZero.Id));
        Assert.That(page.Rows[0].Rank, Is.EqualTo(1));
        Assert.That(page.Rows[0].TotalPoints, Is.EqualTo(0));
        Assert.That(page.Rows.Any(r => r.UserId == neverPlayed.Id), Is.False);
    }

    [Test]
    public async Task REQ404_GetGlobalLeaderboardAsync_MultipleMembers_SortedAscendingByTotalPoints()
    {
        // ADR-0021: xG Arcade is scored like golf — lowest total wins.
        var alex = await SeedMemberAsync("Alex");
        var sam = await SeedMemberAsync("Sam");
        var you = await SeedMemberAsync("You");
        await SeedLockedGuessAsync(alex.Id, 142);
        await SeedLockedGuessAsync(sam.Id, 120);
        await SeedLockedGuessAsync(you.Id, 50);
        await SeedLockedGuessAsync(you.Id, 88); // a second closed round's points, same player

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50, activeRound: null);

        // Ascending by TotalPoints: Sam (120) < You (50 + 88 = 138) < Alex (142).
        Assert.That(page.Rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "Sam", "You", "Alex" }));
        Assert.That(page.Rows.Select(r => r.TotalPoints), Is.EqualTo(new[] { 120, 138, 142 }));
        Assert.That(page.Rows.Select(r => r.Rank), Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(page.Rows.Single(r => r.DisplayName == "You").IsRequestingUser, Is.True);
        Assert.That(page.Rows.Where(r => r.DisplayName != "You").All(r => !r.IsRequestingUser), Is.True);
        Assert.That(page.RequestingUserEntry?.DisplayName, Is.EqualTo("You"));
        Assert.That(page.HasMore, Is.False);
    }

    [Test]
    public async Task REQ607_GetGlobalLeaderboardAsync_PageSizeSmallerThanMembership_CapsResponseAtPageSize()
    {
        var members = new List<User>();
        for (var i = 0; i < 5; i++)
            members.Add(await SeedMemberAsync($"Member{i}"));
        foreach (var member in members)
            await SeedLockedGuessAsync(member.Id, members.IndexOf(member) * 10);

        var page = await _service.GetGlobalLeaderboardAsync(members[0].Id, cursor: 0, pageSize: 2, activeRound: null);

        Assert.That(page.Rows, Has.Count.EqualTo(2));
        Assert.That(page.HasMore, Is.True);
        Assert.That(page.NextCursor, Is.EqualTo(2));
    }

    [Test]
    public async Task REQ607_GetGlobalLeaderboardAsync_SecondPageViaCursor_ReturnsNextDistinctSliceNoOverlapOrGap()
    {
        var members = new List<User>();
        for (var i = 0; i < 5; i++)
            members.Add(await SeedMemberAsync($"Member{i}"));
        foreach (var member in members)
            await SeedLockedGuessAsync(member.Id, members.IndexOf(member) * 10);

        var firstPage = await _service.GetGlobalLeaderboardAsync(members[0].Id, cursor: 0, pageSize: 2, activeRound: null);
        var secondPage = await _service.GetGlobalLeaderboardAsync(members[0].Id, cursor: firstPage.NextCursor!.Value, pageSize: 2, activeRound: null);
        var thirdPage = await _service.GetGlobalLeaderboardAsync(members[0].Id, cursor: secondPage.NextCursor!.Value, pageSize: 2, activeRound: null);

        Assert.That(firstPage.Rows.Select(r => r.Rank), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(secondPage.Rows.Select(r => r.Rank), Is.EqualTo(new[] { 3, 4 }));
        Assert.That(thirdPage.Rows.Select(r => r.Rank), Is.EqualTo(new[] { 5 }));
        Assert.That(thirdPage.HasMore, Is.False);
        Assert.That(thirdPage.NextCursor, Is.Null);

        var allRanksAcrossPages = firstPage.Rows.Concat(secondPage.Rows).Concat(thirdPage.Rows).Select(r => r.Rank).ToList();
        Assert.That(allRanksAcrossPages, Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public async Task REQ607_GetGlobalLeaderboardAsync_RequestingUserOffPage_StillReturnsTheirOwnRow()
    {
        var members = new List<User>();
        for (var i = 0; i < 5; i++)
            members.Add(await SeedMemberAsync($"Member{i}"));
        foreach (var member in members)
            await SeedLockedGuessAsync(member.Id, members.IndexOf(member) * 10);

        // Member4 has the highest TotalPoints, so ranks last (5th) —
        // outside a pageSize=2 first page.
        var page = await _service.GetGlobalLeaderboardAsync(members[4].Id, cursor: 0, pageSize: 2, activeRound: null);

        Assert.That(page.Rows.Any(r => r.IsRequestingUser), Is.False);
        Assert.That(page.RequestingUserEntry, Is.Not.Null);
        Assert.That(page.RequestingUserEntry!.UserId, Is.EqualTo(members[4].Id));
        Assert.That(page.RequestingUserEntry.Rank, Is.EqualTo(5));
    }

    [Test]
    public async Task REQ607_GetGlobalLeaderboardAsync_CursorBeyondMembership_ReturnsEmptyPageNotError()
    {
        // REQ-401/404 (2026-07-20): a locked guess is seeded so this member
        // is a real, ranked ("ever played") entry — otherwise they'd be
        // excluded from the ranked list entirely and RequestingUserEntry
        // would be null for that reason instead of the cursor-paging reason
        // this test actually targets.
        var member = await SeedMemberAsync("Alex");
        await SeedLockedGuessAsync(member.Id, finalPoints: 10);

        var page = await _service.GetGlobalLeaderboardAsync(member.Id, cursor: 50, pageSize: 10, activeRound: null);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.HasMore, Is.False);
        Assert.That(page.NextCursor, Is.Null);
        Assert.That(page.RequestingUserEntry, Is.Not.Null);
    }

    // ---- REQ-406: live active-round contribution folded into the shared total ----

    [Test]
    public async Task REQ406_GetGlobalLeaderboardAsync_CorrectlyGuessedActiveRoundCell_AddsCurrentLivePointsToTotal()
    {
        var you = await SeedMemberAsync("You");
        var cellId = Guid.NewGuid();
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        _fakeGameModule.GetCellIdsResult = _ => [cellId];
        var playerAnswerId = Guid.NewGuid();
        // Lone correct guesser on this cell -> ADR-0020: vacuously 100%
        // unique -> ADR-0021: LivePoints 0 (the best score).
        await SeedGuessAsync(round.Id, you.Id, cellId, isCorrect: true, attemptCount: 1, playerAnswerId: playerAnswerId);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50, activeRound: round);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ406_GetGlobalLeaderboardAsync_LockedIncorrectActiveRoundCell_AddsMaxPointsPerCellToTotal()
    {
        var you = await SeedMemberAsync("You");
        var cellId = Guid.NewGuid();
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        _fakeGameModule.GetCellIdsResult = _ => [cellId];
        await SeedGuessAsync(round.Id, you.Id, cellId, isCorrect: false, attemptCount: GuessRules.MaxAttemptsPerCell);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50, activeRound: round);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell));
    }

    [Test]
    public async Task REQ406_GetGlobalLeaderboardAsync_OneAttemptUsedUnresolvedActiveRoundCell_ContributesNothing()
    {
        // A cell with one of two attempts used and still one remaining
        // (REQ-210) is a genuinely unresolved state, distinct from a
        // zero-guess cell (see the 2026-07-20 test immediately below) — it
        // must keep contributing nothing.
        var you = await SeedMemberAsync("You");
        var cellId = Guid.NewGuid();
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        _fakeGameModule.GetCellIdsResult = _ => [cellId];
        await SeedGuessAsync(round.Id, you.Id, cellId, isCorrect: false, attemptCount: 1);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50, activeRound: round);

        // Not correct, not locked-incorrect (only one of two attempts used)
        // -> total stays 0: nothing was added for this cell at all.
        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ406_GetGlobalLeaderboardAsync_ParticipantZeroGuessCellInActiveRound_ContributesMaxPointsPerCell()
    {
        // 2026-07-20 status note: for a participant (>=1 guess anywhere in
        // this round), a cell with ZERO guesses at all now contributes
        // MaxPointsPerCell — same as a locked-incorrect cell — so a
        // freshly-initiated grid's live total starts near the theoretical
        // max rather than sitting near zero until every cell is attempted.
        var you = await SeedMemberAsync("You");
        var attemptedCellId = Guid.NewGuid();
        var zeroGuessCellId = Guid.NewGuid();
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        _fakeGameModule.GetCellIdsResult = _ => [attemptedCellId, zeroGuessCellId];
        // Lone correct guesser on the attempted cell -> LivePoints 0 (the
        // best score) — isolates the zero-guess cell's own MaxPointsPerCell
        // contribution as the only thing this total can come from.
        await SeedGuessAsync(round.Id, you.Id, attemptedCellId, isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid());

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50, activeRound: round);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell));
    }

    [Test]
    public async Task REQ406_GetGlobalLeaderboardAsync_MemberWithZeroGuessesInActiveRound_UnaffectedByActiveRound()
    {
        // The 2026-07-20 zero-guess-CELL rule only applies once a player is
        // a participant (>=1 guess anywhere in the round) — a player with
        // ZERO guesses anywhere in the active round (a non-participant) is
        // still entirely unaffected by this REQ, unchanged.
        var you = await SeedMemberAsync("You");
        var other = await SeedMemberAsync("Other");
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        var cellId = Guid.NewGuid();
        _fakeGameModule.GetCellIdsResult = _ => [cellId];
        // Only "Other" participates in the active round; "You" has no
        // guesses in it at all.
        await SeedGuessAsync(round.Id, other.Id, cellId, isCorrect: false, attemptCount: GuessRules.MaxAttemptsPerCell);
        await SeedLockedGuessAsync(you.Id, 42); // You's only points are from a closed round.

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50, activeRound: round);

        Assert.That(page.Rows.Single(r => r.DisplayName == "You").TotalPoints, Is.EqualTo(42));
    }

    [Test]
    public async Task REQ406_GetGlobalLeaderboardAsync_CombinesClosedRoundLockedTotalWithActiveRoundLiveContribution()
    {
        var you = await SeedMemberAsync("You");
        await SeedLockedGuessAsync(you.Id, 30); // a closed round's locked points.

        var cellId = Guid.NewGuid();
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        _fakeGameModule.GetCellIdsResult = _ => [cellId];
        await SeedGuessAsync(round.Id, you.Id, cellId, isCorrect: false, attemptCount: GuessRules.MaxAttemptsPerCell);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50, activeRound: round);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(30 + ScoringRules.MaxPointsPerCell));
    }

    [Test]
    public async Task REQ406_GetGlobalLeaderboardAsync_RecomputesAfterUnderlyingGuessChanges_WithNoInvalidationStep()
    {
        // ADR-0031: no cache/snapshot anywhere in this path — a second read
        // after a change to the underlying data must reflect it immediately.
        var you = await SeedMemberAsync("You");
        var sharesAnswer = await SeedMemberAsync("SharesAnswer");
        var distinctAnswerer = await SeedMemberAsync("DistinctAnswerer");
        var cellId = Guid.NewGuid();
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        _fakeGameModule.GetCellIdsResult = _ => [cellId];
        var yourAnswerId = Guid.NewGuid();
        await SeedGuessAsync(round.Id, you.Id, cellId, isCorrect: true, attemptCount: 1, playerAnswerId: yourAnswerId);
        // A distinct correct answer on the same cell, present from the
        // start, so the second read below (2 others: 1 sharing, 1 distinct)
        // lands on exactly 0.5, not 0 (which 1-sharing-out-of-1-other would
        // give) — same three-correct-guesser shape as
        // RoundCloseServiceScoringTests' "TwoOfThreeCorrectGuessesShareAnAnswer" case.
        await SeedGuessAsync(round.Id, distinctAnswerer.Id, cellId, isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid());

        var firstRead = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50, activeRound: round);
        Assert.That(firstRead.Rows.Single(r => r.DisplayName == "You").TotalPoints, Is.EqualTo(0),
            "neither other correct guesser shares your answer yet -> fully unique (ADR-0020)");

        // A third correct guesser now shares your exact answer, taking your
        // uniqueness from 1.0 (0 of 2 others share) to 0.5 (1 of 2 others share).
        await SeedGuessAsync(round.Id, sharesAnswer.Id, cellId, isCorrect: true, attemptCount: 1, playerAnswerId: yourAnswerId);

        var secondRead = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50, activeRound: round);
        Assert.That(secondRead.Rows.Single(r => r.DisplayName == "You").TotalPoints, Is.EqualTo(ScoringRules.PointsFromUniqueScore(0.5)),
            "recomputed fresh on this second read, no explicit invalidation step");
    }

    // ---- REQ-407: standalone active-round-scoped live leaderboard ----

    [Test]
    public async Task REQ407_GetActiveRoundLeaderboardAsync_ParticipantOnly_NonParticipantExcludedEntirely()
    {
        var you = await SeedMemberAsync("You");
        await SeedMemberAsync("NeverPlayed"); // a global-league member, but not a round participant.
        var cellId = Guid.NewGuid();
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        _fakeGameModule.GetCellIdsResult = _ => [cellId];
        await SeedGuessAsync(round.Id, you.Id, cellId, isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid());

        var page = await _service.GetActiveRoundLeaderboardAsync(you.Id, round, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Has.Count.EqualTo(1));
        Assert.That(page.Rows.Single().DisplayName, Is.EqualTo("You"));
    }

    [Test]
    public async Task REQ407_GetActiveRoundLeaderboardAsync_RanksAscendingByTotalPoints()
    {
        var alex = await SeedMemberAsync("Alex");
        var sam = await SeedMemberAsync("Sam");
        var cellA = Guid.NewGuid();
        var cellB = Guid.NewGuid();
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        _fakeGameModule.GetCellIdsResult = _ => [cellA, cellB];
        // Alex: locked-incorrect on both cells (worst). Sam: one attempt
        // still unresolved on cellA, correct lone guesser on cellB (best).
        // Both users have at least one guess on every cell in the round, so
        // the 2026-07-20 zero-guess-cell rule (REQ-406/407) never applies
        // here — this test isolates ordering alone, unaffected by that
        // change.
        await SeedGuessAsync(round.Id, alex.Id, cellA, isCorrect: false, attemptCount: GuessRules.MaxAttemptsPerCell);
        await SeedGuessAsync(round.Id, alex.Id, cellB, isCorrect: false, attemptCount: GuessRules.MaxAttemptsPerCell);
        await SeedGuessAsync(round.Id, sam.Id, cellA, isCorrect: false, attemptCount: 1);
        await SeedGuessAsync(round.Id, sam.Id, cellB, isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid());

        var page = await _service.GetActiveRoundLeaderboardAsync(alex.Id, round, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "Sam", "Alex" }));
        Assert.That(page.Rows.Select(r => r.TotalPoints), Is.EqualTo(new[] { 0, 2 * ScoringRules.MaxPointsPerCell }));
    }

    [Test]
    public async Task REQ407_GetActiveRoundLeaderboardAsync_TiedTotalPoints_TieBreaksByDisplayNameAscending()
    {
        var zoe = await SeedMemberAsync("Zoe");
        var amy = await SeedMemberAsync("Amy");
        var cellA = Guid.NewGuid();
        var cellB = Guid.NewGuid();
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        _fakeGameModule.GetCellIdsResult = _ => [cellA, cellB];
        // Both locked-incorrect on their own cell -> identical TotalPoints.
        await SeedGuessAsync(round.Id, zoe.Id, cellA, isCorrect: false, attemptCount: GuessRules.MaxAttemptsPerCell);
        await SeedGuessAsync(round.Id, amy.Id, cellB, isCorrect: false, attemptCount: GuessRules.MaxAttemptsPerCell);

        var page = await _service.GetActiveRoundLeaderboardAsync(zoe.Id, round, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "Amy", "Zoe" }), "REQ-404's display-name tie-break, reused here");
    }

    [Test]
    public async Task REQ407_GetActiveRoundLeaderboardAsync_ParticipantZeroGuessCell_ContributesMaxPointsPerCell()
    {
        // 2026-07-20 status note: REQ-407 consumes the same
        // ILiveRoundContributionService computation REQ-406 does, so every
        // participant shown here (zero-guess players never appear at all —
        // see the NonParticipantExcludedEntirely test above) picks up
        // MaxPointsPerCell for any cell they've made zero guesses on.
        var you = await SeedMemberAsync("You");
        var attemptedCellId = Guid.NewGuid();
        var zeroGuessCellId = Guid.NewGuid();
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        _fakeGameModule.GetCellIdsResult = _ => [attemptedCellId, zeroGuessCellId];
        await SeedGuessAsync(round.Id, you.Id, attemptedCellId, isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid());

        var page = await _service.GetActiveRoundLeaderboardAsync(you.Id, round, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell));
    }

    [Test]
    public async Task REQ407_GetActiveRoundLeaderboardAsync_NoParticipantsAtAll_ReturnsEmptyPage()
    {
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        _fakeGameModule.GetCellIdsResult = _ => [Guid.NewGuid()];

        var page = await _service.GetActiveRoundLeaderboardAsync(Guid.NewGuid(), round, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.RequestingUserEntry, Is.Null);
        Assert.That(page.HasMore, Is.False);
    }

    // ---- REQ-408: browsable past closed-round leaderboards ----

    [Test]
    public async Task REQ408_GetClosedRoundsAsync_ReturnsOnlyClosedRoundsMostRecentlyClosedFirst()
    {
        var now = DateTime.UtcNow;
        var closedEarlier = await SeedRoundAsync(now.AddDays(-4), now.AddDays(-3), closedAt: now.AddDays(-3));
        var closedLater = await SeedRoundAsync(now.AddDays(-2), now.AddDays(-1), closedAt: now.AddDays(-1));
        var stillActive = await SeedRoundAsync(now.AddHours(-1), now.AddHours(1)); // ClosedAt null.

        var page = await _service.GetClosedRoundsAsync(GameKey, cursor: 0, pageSize: 50);

        Assert.That(page.Rounds.Select(r => r.RoundId), Is.EqualTo(new[] { closedLater.Id, closedEarlier.Id }));
        Assert.That(page.Rounds.Any(r => r.RoundId == stillActive.Id), Is.False);
    }

    [Test]
    public async Task REQ408_GetClosedRoundsAsync_PageSizeSmallerThanCount_ReturnsCappedPageWithUsableCursor()
    {
        var now = DateTime.UtcNow;
        var rounds = new List<Round>();
        for (var i = 0; i < 3; i++)
            rounds.Add(await SeedRoundAsync(now.AddDays(-i - 2), now.AddDays(-i - 1), closedAt: now.AddDays(-i - 1)));

        var firstPage = await _service.GetClosedRoundsAsync(GameKey, cursor: 0, pageSize: 2);
        Assert.That(firstPage.Rounds, Has.Count.EqualTo(2));
        Assert.That(firstPage.HasMore, Is.True);
        Assert.That(firstPage.NextCursor, Is.EqualTo(2));

        var secondPage = await _service.GetClosedRoundsAsync(GameKey, cursor: firstPage.NextCursor!.Value, pageSize: 2);
        Assert.That(secondPage.Rounds, Has.Count.EqualTo(1));
        Assert.That(secondPage.HasMore, Is.False);
        Assert.That(secondPage.NextCursor, Is.Null);

        var allRoundIds = firstPage.Rounds.Concat(secondPage.Rounds).Select(r => r.RoundId).ToList();
        Assert.That(allRoundIds, Is.EquivalentTo(rounds.Select(r => r.Id)));
    }

    [Test]
    public async Task REQ408_GetClosedRoundLeaderboardAsync_UnknownRoundId_ReturnsRoundNotFound()
    {
        var result = await _service.GetClosedRoundLeaderboardAsync(Guid.NewGuid(), Guid.NewGuid(), cursor: 0, pageSize: 50);

        Assert.That(result.Status, Is.EqualTo(ClosedRoundLeaderboardStatus.RoundNotFound));
        Assert.That(result.Page, Is.Null);
    }

    [Test]
    public async Task REQ408_GetClosedRoundLeaderboardAsync_RoundExistsButNotClosedYet_ReturnsRoundNotClosedYetDistinctFromNotFound()
    {
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)); // ClosedAt null.

        var result = await _service.GetClosedRoundLeaderboardAsync(round.Id, Guid.NewGuid(), cursor: 0, pageSize: 50);

        Assert.That(result.Status, Is.EqualTo(ClosedRoundLeaderboardStatus.RoundNotClosedYet));
        Assert.That(result.Page, Is.Null);
    }

    [Test]
    public async Task REQ408_GetClosedRoundLeaderboardAsync_ClosedRound_TotalMatchesReq206LockedFormulaExactlyAndNeverRecomputes()
    {
        var you = await SeedMemberAsync("You");
        var alex = await SeedMemberAsync("Alex");
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1), closedAt: DateTime.UtcNow.AddDays(-1));
        var cellA = Guid.NewGuid();
        var cellB = Guid.NewGuid();
        // Two locked guesses for "You" in this round, summing to 30.
        await SeedGuessAsync(round.Id, you.Id, cellA, isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 10);
        await SeedGuessAsync(round.Id, you.Id, cellB, isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 20);
        await SeedGuessAsync(round.Id, alex.Id, cellA, isCorrect: false, attemptCount: GuessRules.MaxAttemptsPerCell, finalPoints: ScoringRules.MaxPointsPerCell);
        // A locked guess from a DIFFERENT round for "You" must never bleed
        // into this round-scoped total.
        await SeedLockedGuessAsync(you.Id, 999);

        var result = await _service.GetClosedRoundLeaderboardAsync(round.Id, you.Id, cursor: 0, pageSize: 50);

        Assert.That(result.Status, Is.EqualTo(ClosedRoundLeaderboardStatus.Found));
        Assert.That(result.Page!.Rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "You", "Alex" }));
        Assert.That(result.Page.Rows.Select(r => r.TotalPoints), Is.EqualTo(new[] { 30, ScoringRules.MaxPointsPerCell }));
    }

    // Pagination *within* one closed round's participant list — distinct
    // from REQ408_GetClosedRoundsAsync_PageSizeSmallerThanCount_ above, which
    // pages the round-list itself. Goes through the same already-tested
    // private Paginate helper as every other scope in this service, but
    // hadn't been exercised directly for this method.
    [Test]
    public async Task REQ408_GetClosedRoundLeaderboardAsync_PageSizeSmallerThanParticipantCount_ReturnsCappedPageWithUsableCursor()
    {
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1), closedAt: DateTime.UtcNow.AddDays(-1));
        var participants = new List<User>();
        for (var i = 0; i < 3; i++)
        {
            var participant = await SeedMemberAsync($"Participant{i}");
            participants.Add(participant);
            await SeedGuessAsync(round.Id, participant.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: i * 10);
        }

        var firstPage = await _service.GetClosedRoundLeaderboardAsync(round.Id, participants[0].Id, cursor: 0, pageSize: 2);
        Assert.That(firstPage.Status, Is.EqualTo(ClosedRoundLeaderboardStatus.Found));
        Assert.That(firstPage.Page!.Rows, Has.Count.EqualTo(2));
        Assert.That(firstPage.Page.HasMore, Is.True);
        Assert.That(firstPage.Page.NextCursor, Is.EqualTo(2));

        var secondPage = await _service.GetClosedRoundLeaderboardAsync(round.Id, participants[0].Id, cursor: firstPage.Page.NextCursor!.Value, pageSize: 2);
        Assert.That(secondPage.Status, Is.EqualTo(ClosedRoundLeaderboardStatus.Found));
        Assert.That(secondPage.Page!.Rows, Has.Count.EqualTo(1));
        Assert.That(secondPage.Page.HasMore, Is.False);
        Assert.That(secondPage.Page.NextCursor, Is.Null);

        var allUserIds = firstPage.Page.Rows.Concat(secondPage.Page.Rows).Select(r => r.UserId).ToList();
        Assert.That(allUserIds, Is.EquivalentTo(participants.Select(p => p.Id)));
    }
}

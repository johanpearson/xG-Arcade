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
// REQ-409 (2026-07-20, backlog S-060) REPLACED GetGlobalLeaderboardAsync's
// ranking outright: the old REQ401/404-named sum tests and REQ406-named
// live-fold tests targeting this method were removed (that formula/live-fold
// no longer exists on this method at all, not merely renamed), and new
// REQ409-named cases added in their place. REQ-407/408's own tests below are
// unaffected — they exercise different methods this REQ doesn't touch.
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

    // REQ-409 (2026-07-20): each call now also persists a real, already-
    // closed Round row backing the seeded Guess — previously this only ever
    // set a random, unbacked RoundId, which was harmless while the
    // leaderboard's all-time total was a plain SUM across every Guess
    // (GetTotalFinalPointsByUserIdsAsync didn't care whether a Round row
    // existed). REQ-409's GetPerRoundFinalPointsByUserIdsAsync joins against
    // Rounds and requires ClosedAt != null, so a "qualifying round" needs a
    // genuine closed Round row now. Every existing caller already treated
    // each call as "a[nother] closed round's locked points, same player"
    // (see e.g. the pre-existing "a second closed round's points" comment
    // below) — this just makes that literally true instead of only summing
    // as if it were.
    private async Task SeedLockedGuessAsync(Guid userId, int finalPoints)
    {
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GameKey,
            GameInstanceId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddDays(-2),
            EndTime = DateTime.UtcNow.AddDays(-1),
            AllowGuessChange = true,
            ClosedAt = DateTime.UtcNow.AddDays(-1),
        };
        _dbContext.Rounds.Add(round);
        _dbContext.Guesses.Add(new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = round.Id,
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

    // REQ-409: convenience for building a player's qualifying-round history
    // for the median ranking — one call per qualifying round, in the order
    // given. Each entry becomes its own closed round via SeedLockedGuessAsync
    // above.
    private async Task SeedQualifyingRoundsAsync(Guid userId, params int[] finalPointsPerRound)
    {
        foreach (var finalPoints in finalPointsPerRound)
            await SeedLockedGuessAsync(userId, finalPoints);
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
        // 2026-07-20 (REQ-401/404 status note, subsumed by REQ-409): a
        // member for whom no Guess row has ever existed has 0 qualifying
        // rounds — always fewer than REQ-409's 5-round minimum — so they're
        // excluded from the ranked list entirely, not shown ranked with a
        // default score of 0 (which ADR-0021's lowest-wins model would
        // otherwise treat as the BEST possible score, letting a
        // never-played member rank #1).
        var member = await SeedMemberAsync("Alex");

        var page = await _service.GetGlobalLeaderboardAsync(member.Id, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.RequestingUserEntry, Is.Null);
        Assert.That(page.HasMore, Is.False);
        Assert.That(page.NextCursor, Is.Null);
    }

    // ---- REQ-409: median, participation-gated all-time ranking (2026-07-20) ----
    // Replaces REQ-401/404's old SUM(FinalPoints ?? 0) ranking outright (not
    // a new tab) — see ILeaderboardService's own doc comment. The REQ-406
    // live-fold tests that previously lived in this section were removed
    // rather than adapted: REQ-409 explicitly has no live component, so that
    // behavior no longer exists on this method at all (see
    // GetActiveRoundLeaderboardAsync/REQ-407 below for the still-live scope).

    [Test]
    public async Task REQ409_GetGlobalLeaderboardAsync_OddQualifyingRoundCount_RanksByMiddleValue()
    {
        var you = await SeedMemberAsync("You");
        // Sorted: 10, 20, 30, 40, 50 -> odd count (5), middle value is 30.
        await SeedQualifyingRoundsAsync(you.Id, 50, 10, 30, 20, 40);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(30));
    }

    [Test]
    public async Task REQ409_GetGlobalLeaderboardAsync_EvenQualifyingRoundCount_RanksByRoundedAverageOfTwoMiddleValues()
    {
        var you = await SeedMemberAsync("You");
        // Sorted: 10, 20, 29, 30, 50, 60 -> even count (6), middle two are
        // 29 and 30 -> average 29.5, rounds to 30 (MidpointRounding.AwayFromZero).
        await SeedQualifyingRoundsAsync(you.Id, 60, 10, 30, 50, 29, 20);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(30));
    }

    [Test]
    public async Task REQ409_GetGlobalLeaderboardAsync_ExactlyFourQualifyingRounds_ExcludedFromRankedList()
    {
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 10, 20, 30, 40);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.RequestingUserEntry, Is.Null);
    }

    [Test]
    public async Task REQ409_GetGlobalLeaderboardAsync_ExactlyFiveQualifyingRounds_IncludedAndRanked()
    {
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 10, 20, 30, 40, 50);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Has.Count.EqualTo(1));
        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(30));
        Assert.That(page.Rows.Single().Rank, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ409_GetGlobalLeaderboardAsync_MultipleMembers_SortedAscendingByMedian()
    {
        // ADR-0021: xG Arcade is scored like golf — lowest median wins.
        var alex = await SeedMemberAsync("Alex");
        var sam = await SeedMemberAsync("Sam");
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(alex.Id, 60, 70, 80, 90, 100); // median 80
        await SeedQualifyingRoundsAsync(sam.Id, 10, 20, 30, 40, 50);   // median 30
        await SeedQualifyingRoundsAsync(you.Id, 40, 45, 50, 55, 60);   // median 50

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "Sam", "You", "Alex" }));
        Assert.That(page.Rows.Select(r => r.TotalPoints), Is.EqualTo(new[] { 30, 50, 80 }));
        Assert.That(page.Rows.Select(r => r.Rank), Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(page.Rows.Single(r => r.DisplayName == "You").IsRequestingUser, Is.True);
        Assert.That(page.Rows.Where(r => r.DisplayName != "You").All(r => !r.IsRequestingUser), Is.True);
        Assert.That(page.RequestingUserEntry?.DisplayName, Is.EqualTo("You"));
        Assert.That(page.HasMore, Is.False);
    }

    [Test]
    public async Task REQ409_GetGlobalLeaderboardAsync_TiedMedians_TieBreaksByDisplayNameOrdinalIgnoreCase()
    {
        var zoe = await SeedMemberAsync("Zoe");
        var amy = await SeedMemberAsync("Amy");
        await SeedQualifyingRoundsAsync(zoe.Id, 10, 20, 30, 40, 50);
        await SeedQualifyingRoundsAsync(amy.Id, 10, 20, 30, 40, 50);

        var page = await _service.GetGlobalLeaderboardAsync(zoe.Id, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "Amy", "Zoe" }), "REQ-404's display-name tie-break, reused here");
    }

    [Test]
    public async Task REQ409_GetGlobalLeaderboardAsync_MedianUsesEveryQualifyingRoundNotJustTheMostRecentFive()
    {
        // The 5-round minimum is a qualification floor, not a rolling
        // window — 7 qualifying rounds seeded in this order: a "most
        // recent 5" implementation would wrongly drop the first two
        // (values 1, 2) and compute median 5 (middle of 3,4,5,6,100); the
        // correct all-7 median is 4 (middle of 1,2,3,4,5,6,100).
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 1, 2, 3, 4, 5, 6, 100);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(4));
    }

    [Test]
    public async Task REQ409_GetGlobalLeaderboardAsync_ActiveUnlockedRoundGuesses_NeverCountTowardQualifyingRoundThreshold()
    {
        // Only 4 real closed qualifying rounds, plus a 5th round's worth of
        // guesses in a round that's still active (unlocked) — REQ-409 is
        // explicit this must not count toward the 5-round minimum, so this
        // member stays excluded exactly as the 4-round test above.
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 10, 20, 30, 40);
        var cellId = Guid.NewGuid();
        var activeRound = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        await SeedGuessAsync(activeRound.Id, you.Id, cellId, isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid());

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Is.Empty);
    }

    [Test]
    public async Task REQ409_GetGlobalLeaderboardAsync_ActiveUnlockedRoundGuesses_NeverContributeToMedian()
    {
        // 5 real closed qualifying rounds (median 10) plus a guess in a
        // still-active round carrying a defensively-set, deliberately
        // extreme FinalPoints value — if the active round were wrongly
        // folded in, the median would shift; it must not.
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 10, 10, 10, 10, 10);
        var cellId = Guid.NewGuid();
        var activeRound = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        await SeedGuessAsync(activeRound.Id, you.Id, cellId, isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 999);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(10));
    }

    [Test]
    public async Task REQ607_GetGlobalLeaderboardAsync_PageSizeSmallerThanMembership_CapsResponseAtPageSize()
    {
        var members = new List<User>();
        for (var i = 0; i < 5; i++)
            members.Add(await SeedMemberAsync($"Member{i}"));
        // Each member gets 5 identical-valued qualifying rounds — trivially
        // meets REQ-409's minimum with a median equal to that same value,
        // so this test's original pagination assertions (based on plain
        // ascending TotalPoints) still hold unchanged.
        foreach (var member in members)
        {
            var value = members.IndexOf(member) * 10;
            await SeedQualifyingRoundsAsync(member.Id, value, value, value, value, value);
        }

        var page = await _service.GetGlobalLeaderboardAsync(members[0].Id, cursor: 0, pageSize: 2);

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
        {
            var value = members.IndexOf(member) * 10;
            await SeedQualifyingRoundsAsync(member.Id, value, value, value, value, value);
        }

        var firstPage = await _service.GetGlobalLeaderboardAsync(members[0].Id, cursor: 0, pageSize: 2);
        var secondPage = await _service.GetGlobalLeaderboardAsync(members[0].Id, cursor: firstPage.NextCursor!.Value, pageSize: 2);
        var thirdPage = await _service.GetGlobalLeaderboardAsync(members[0].Id, cursor: secondPage.NextCursor!.Value, pageSize: 2);

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
        {
            var value = members.IndexOf(member) * 10;
            await SeedQualifyingRoundsAsync(member.Id, value, value, value, value, value);
        }

        // Member4 has the highest median, so ranks last (5th) — outside a
        // pageSize=2 first page.
        var page = await _service.GetGlobalLeaderboardAsync(members[4].Id, cursor: 0, pageSize: 2);

        Assert.That(page.Rows.Any(r => r.IsRequestingUser), Is.False);
        Assert.That(page.RequestingUserEntry, Is.Not.Null);
        Assert.That(page.RequestingUserEntry!.UserId, Is.EqualTo(members[4].Id));
        Assert.That(page.RequestingUserEntry.Rank, Is.EqualTo(5));
    }

    [Test]
    public async Task REQ607_GetGlobalLeaderboardAsync_CursorBeyondMembership_ReturnsEmptyPageNotError()
    {
        // REQ-409: 5 qualifying rounds are seeded so this member is a real,
        // ranked entry — otherwise they'd be excluded from the ranked list
        // entirely and RequestingUserEntry would be null for that reason
        // instead of the cursor-paging reason this test actually targets.
        var member = await SeedMemberAsync("Alex");
        await SeedQualifyingRoundsAsync(member.Id, 10, 10, 10, 10, 10);

        var page = await _service.GetGlobalLeaderboardAsync(member.Id, cursor: 50, pageSize: 10);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.HasMore, Is.False);
        Assert.That(page.NextCursor, Is.Null);
        Assert.That(page.RequestingUserEntry, Is.Not.Null);
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

    // ---- REQ-405: round/week/month/year time-window resolutions ----

    [Test]
    public async Task REQ405_GetWindowedLeaderboardAsync_RoundResolution_UsesSingleMostRecentlyClosedRoundOnly()
    {
        var you = await SeedMemberAsync("You");
        var alex = await SeedMemberAsync("Alex");
        var now = DateTime.UtcNow;
        var olderClosedRound = await SeedRoundAsync(now.AddDays(-4), now.AddDays(-3), closedAt: now.AddDays(-3));
        var mostRecentlyClosedRound = await SeedRoundAsync(now.AddDays(-2), now.AddDays(-1), closedAt: now.AddDays(-1));
        await SeedGuessAsync(olderClosedRound.Id, you.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 999);
        await SeedGuessAsync(mostRecentlyClosedRound.Id, you.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 10);
        await SeedGuessAsync(mostRecentlyClosedRound.Id, alex.Id, Guid.NewGuid(), isCorrect: false, attemptCount: GuessRules.MaxAttemptsPerCell, finalPoints: ScoringRules.MaxPointsPerCell);

        var page = await _service.GetWindowedLeaderboardAsync(you.Id, GameKey, LeaderboardWindowResolution.Round, now, cursor: 0, pageSize: 50);

        // Only the most-recently-closed round's points count (10), never the
        // older closed round's 999.
        Assert.That(page.Rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "You", "Alex" }));
        Assert.That(page.Rows.Select(r => r.TotalPoints), Is.EqualTo(new[] { 10, ScoringRules.MaxPointsPerCell }));
    }

    [Test]
    public async Task REQ405_GetWindowedLeaderboardAsync_RoundResolution_NoClosedRoundExists_ReturnsEmptyPage()
    {
        await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)); // still active, ClosedAt null.

        var page = await _service.GetWindowedLeaderboardAsync(Guid.NewGuid(), GameKey, LeaderboardWindowResolution.Round, DateTime.UtcNow, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.RequestingUserEntry, Is.Null);
        Assert.That(page.HasMore, Is.False);
        Assert.That(page.NextCursor, Is.Null);
    }

    [Test]
    public async Task REQ405_GetWindowedLeaderboardAsync_RoundResolution_ActiveRoundGuessesNeverContribute()
    {
        var you = await SeedMemberAsync("You");
        var now = DateTime.UtcNow;
        var closedRound = await SeedRoundAsync(now.AddDays(-2), now.AddDays(-1), closedAt: now.AddDays(-1));
        var activeRound = await SeedRoundAsync(now.AddHours(-1), now.AddHours(1)); // ClosedAt null.
        await SeedGuessAsync(closedRound.Id, you.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 15);
        // An active/unlocked round's guess must never contribute, even
        // though it would otherwise carry a FinalPoints value.
        await SeedGuessAsync(activeRound.Id, you.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 5);

        var page = await _service.GetWindowedLeaderboardAsync(you.Id, GameKey, LeaderboardWindowResolution.Round, now, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(15));
    }

    [Test]
    public async Task REQ405_GetWindowedLeaderboardAsync_WeekResolution_BucketsRoundsInsideCurrentIsoWeekOnly()
    {
        var you = await SeedMemberAsync("You");
        // Wednesday 2026-07-15 12:00 UTC -> ISO week is Mon 2026-07-13 through
        // (exclusive) Mon 2026-07-20.
        var nowUtc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var insideWeekRound = await SeedRoundAsync(
            new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 14, 1, 0, 0, DateTimeKind.Utc),
            closedAt: new DateTime(2026, 7, 14, 1, 0, 0, DateTimeKind.Utc));
        var beforeWeekRound = await SeedRoundAsync(
            new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 12, 23, 59, 0, DateTimeKind.Utc), // Sunday, before Monday 2026-07-13 -> outside.
            closedAt: new DateTime(2026, 7, 12, 23, 59, 0, DateTimeKind.Utc));
        await SeedGuessAsync(insideWeekRound.Id, you.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 10);
        await SeedGuessAsync(beforeWeekRound.Id, you.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 999);

        var page = await _service.GetWindowedLeaderboardAsync(you.Id, GameKey, LeaderboardWindowResolution.Week, nowUtc, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(10));
    }

    [Test]
    public async Task REQ405_GetWindowedLeaderboardAsync_MonthResolution_RoundEndingExactlyAtMonthBoundary_ExcludedFromEarlierMonth()
    {
        var you = await SeedMemberAsync("You");
        var nowUtc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        // Ends exactly at 2026-08-01T00:00:00Z, the start of the *next*
        // month — the half-open [start, end) range for July must exclude
        // this boundary instant.
        var atBoundaryRound = await SeedRoundAsync(
            new DateTime(2026, 7, 31, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            closedAt: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        var withinJulyRound = await SeedRoundAsync(
            new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 10, 1, 0, 0, DateTimeKind.Utc),
            closedAt: new DateTime(2026, 7, 10, 1, 0, 0, DateTimeKind.Utc));
        await SeedGuessAsync(atBoundaryRound.Id, you.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 999);
        await SeedGuessAsync(withinJulyRound.Id, you.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 20);

        var page = await _service.GetWindowedLeaderboardAsync(you.Id, GameKey, LeaderboardWindowResolution.Month, nowUtc, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(20));
    }

    [Test]
    public async Task REQ405_GetWindowedLeaderboardAsync_YearResolution_BucketsRoundsInsideCurrentCalendarYearOnly()
    {
        var you = await SeedMemberAsync("You");
        var nowUtc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var withinYearRound = await SeedRoundAsync(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc),
            closedAt: new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc));
        var lastYearRound = await SeedRoundAsync(
            new DateTime(2025, 12, 31, 22, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 12, 31, 23, 59, 0, DateTimeKind.Utc),
            closedAt: new DateTime(2025, 12, 31, 23, 59, 0, DateTimeKind.Utc));
        await SeedGuessAsync(withinYearRound.Id, you.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 30);
        await SeedGuessAsync(lastYearRound.Id, you.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 999);

        var page = await _service.GetWindowedLeaderboardAsync(you.Id, GameKey, LeaderboardWindowResolution.Year, nowUtc, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(30));
    }

    [Test]
    public async Task REQ405_GetWindowedLeaderboardAsync_NoParticipantsInWindow_ReturnsEmptyRankedListNotError()
    {
        var you = await SeedMemberAsync("You");
        var nowUtc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        // A closed round exists, but entirely outside this month's window —
        // so the month window has zero participating rounds/guesses.
        var lastMonthRound = await SeedRoundAsync(
            new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 10, 1, 0, 0, DateTimeKind.Utc),
            closedAt: new DateTime(2026, 6, 10, 1, 0, 0, DateTimeKind.Utc));
        await SeedGuessAsync(lastMonthRound.Id, you.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 5);

        var page = await _service.GetWindowedLeaderboardAsync(you.Id, GameKey, LeaderboardWindowResolution.Month, nowUtc, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.RequestingUserEntry, Is.Null);
        Assert.That(page.HasMore, Is.False);
        Assert.That(page.NextCursor, Is.Null);
    }

    [Test]
    public async Task REQ405_GetWindowedLeaderboardAsync_MultipleMembersInWindow_SortedAscendingByTotalPoints()
    {
        var alex = await SeedMemberAsync("Alex");
        var sam = await SeedMemberAsync("Sam");
        var nowUtc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var round = await SeedRoundAsync(
            new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 10, 1, 0, 0, DateTimeKind.Utc),
            closedAt: new DateTime(2026, 7, 10, 1, 0, 0, DateTimeKind.Utc));
        await SeedGuessAsync(round.Id, alex.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 90);
        await SeedGuessAsync(round.Id, sam.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 40);

        var page = await _service.GetWindowedLeaderboardAsync(alex.Id, GameKey, LeaderboardWindowResolution.Month, nowUtc, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "Sam", "Alex" }));
        Assert.That(page.Rows.Select(r => r.TotalPoints), Is.EqualTo(new[] { 40, 90 }));
    }
}

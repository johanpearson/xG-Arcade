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
// REQ-410 (2026-07-27, backlog S-078, ADR-0043) added a required gameKey
// parameter to GetGlobalLeaderboardAsync — every existing REQ401/409/717/607
// call in this file now passes the shared `GameKey` constant below
// explicitly (every seeded Round already carries that same GameKey, so this
// is a same-behavior compile fix, not a new scoping test). The dedicated
// REQ410-named cross-game-isolation tests (a second, real "xg-path" GameKey,
// confirming rankings never blend) live in their own section below, using a
// second SeedQualifyingRoundsAsync overload and SeedLockedGuessAsync's new
// optional gameKey parameter.
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
    // REQ-410 (2026-07-27, backlog S-078, ADR-0043): optional trailing
    // gameKey parameter, defaulting to the shared GameKey constant so every
    // pre-existing call site (all implicitly "xg-grid") is unchanged — only
    // the new REQ410-named cross-game-isolation tests below pass a second,
    // different GameKey ("xg-path") explicitly.
    private async Task SeedLockedGuessAsync(Guid userId, int finalPoints, string? gameKey = null)
    {
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = gameKey ?? GameKey,
            GameInstanceId = Guid.NewGuid(),
            SequenceNumber = 1,
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

    // REQ-410: same as SeedQualifyingRoundsAsync above, but for a
    // caller-chosen GameKey other than the file's default — lets a
    // cross-game-isolation test build a player's qualifying-round history
    // under a second game without touching the many existing single-game
    // callers of the overload above.
    private async Task SeedQualifyingRoundsAsync(Guid userId, string gameKey, params int[] finalPointsPerRound)
    {
        foreach (var finalPoints in finalPointsPerRound)
            await SeedLockedGuessAsync(userId, finalPoints, gameKey);
    }

    // REQ-717/ADR-0036: same shape as SeedLockedGuessAsync above, but with a
    // caller-chosen ClosedAt instead of a fixed "yesterday" — needed to test
    // the claim cutoff (a round closed before vs. after User.ClaimedAt)
    // precisely, which SeedLockedGuessAsync's fixed offset can't express.
    private async Task SeedLockedGuessAtAsync(Guid userId, int finalPoints, DateTime closedAt)
    {
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GameKey,
            GameInstanceId = Guid.NewGuid(),
            SequenceNumber = 1,
            StartTime = closedAt.AddDays(-1),
            EndTime = closedAt,
            AllowGuessChange = true,
            ClosedAt = closedAt,
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

    // REQ-717/ADR-0036: a guest Global-league member — no email, IsGuest =
    // true, same auto-enrollment every other member here gets.
    private async Task<User> SeedGuestMemberAsync(string displayName)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = null,
            DisplayName = displayName,
            EmailConfirmed = false,
            IsGuest = true,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var globalLeague = await _leagueRepository.GetOrCreateGlobalLeagueAsync();
        await _leagueRepository.AddMembershipAsync(globalLeague.Id, user.Id);
        return user;
    }

    // REQ-717/ADR-0036: a formerly-guest member who has already claimed a
    // real email/password — IsGuest false, ClaimedAt set to the caller's
    // chosen instant, same as UserRepository.ClaimGuestAsync would produce.
    private async Task<User> SeedClaimedMemberAsync(string displayName, DateTime claimedAt)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = $"{Guid.NewGuid()}@example.com",
            DisplayName = displayName,
            EmailConfirmed = true,
            IsGuest = false,
            ClaimedAt = claimedAt,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var globalLeague = await _leagueRepository.GetOrCreateGlobalLeagueAsync();
        await _leagueRepository.AddMembershipAsync(globalLeague.Id, user.Id);
        return user;
    }

    private async Task<Round> SeedRoundAsync(DateTime startTime, DateTime endTime, DateTime? closedAt = null)
    {
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GameKey,
            GameInstanceId = Guid.NewGuid(),
            SequenceNumber = 1,
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

        var page = await _service.GetGlobalLeaderboardAsync(member.Id, GameKey, cursor: 0, pageSize: 50);

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

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(30));
    }

    [Test]
    public async Task REQ409_GetGlobalLeaderboardAsync_EvenQualifyingRoundCount_RanksByRoundedAverageOfTwoMiddleValues()
    {
        var you = await SeedMemberAsync("You");
        // Sorted: 10, 20, 29, 30, 50, 60 -> even count (6), middle two are
        // 29 and 30 -> average 29.5, rounds to 30 (MidpointRounding.AwayFromZero).
        await SeedQualifyingRoundsAsync(you.Id, 60, 10, 30, 50, 29, 20);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(30));
    }

    [Test]
    public async Task REQ409_GetGlobalLeaderboardAsync_ExactlyFourQualifyingRounds_ExcludedFromRankedList()
    {
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 10, 20, 30, 40);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.RequestingUserEntry, Is.Null);
    }

    [Test]
    public async Task REQ409_GetGlobalLeaderboardAsync_ExactlyFiveQualifyingRounds_IncludedAndRanked()
    {
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 10, 20, 30, 40, 50);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);

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

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);

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

        var page = await _service.GetGlobalLeaderboardAsync(zoe.Id, GameKey, cursor: 0, pageSize: 50);

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

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);

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

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);

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

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);

        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(10));
    }

    // ---- REQ-410/ADR-0043: all-time ranking scoped per game (2026-07-27) ----
    // GetGlobalLeaderboardAsync/GetPerRoundFinalPointsByUserIdsAsync gained a
    // required gameKey parameter — every round seeded via
    // SeedQualifyingRoundsAsync's second overload above carries an explicit,
    // caller-chosen GameKey (distinct from the file's "xg-grid" default),
    // exercised through the real EF InMemory provider's round.GameKey ==
    // gameKey filter (GuessRepository.GetPerRoundFinalPointsByUserIdsAsync),
    // not a fake/mock. GetGlobalLeaderboardAsync itself never touches
    // GameModuleResolver/FakeGameModule, so no second FakeGameModule
    // registration is needed for these tests — only the Guess-Round join's
    // GameKey column matters here.
    private const string OtherGameKey = "xg-path";

    [Test]
    public async Task REQ410_GetGlobalLeaderboardAsync_QualifyingRoundsInAnotherGame_NeverCountTowardThisGamesRanking()
    {
        // 5 qualifying xg-grid rounds (clears REQ-409's floor), zero
        // xg-path rounds — requesting xg-grid's ranking must include this
        // player; requesting xg-path's ranking must not, since none of
        // those rounds carry GameKey == "xg-path".
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 10, 20, 30, 40, 50);

        var xgGridPage = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);
        var xgPathPage = await _service.GetGlobalLeaderboardAsync(you.Id, OtherGameKey, cursor: 0, pageSize: 50);

        Assert.That(xgGridPage.Rows, Has.Count.EqualTo(1));
        Assert.That(xgGridPage.Rows.Single().TotalPoints, Is.EqualTo(30));
        Assert.That(xgPathPage.Rows, Is.Empty);
        Assert.That(xgPathPage.RequestingUserEntry, Is.Null);
    }

    [Test]
    public async Task REQ410_GetGlobalLeaderboardAsync_FiveQualifyingRoundsInOneGame_RankedForThatGameAloneNeverInTheOther()
    {
        // Mirrors the acceptance criterion's own example: 5+ qualifying
        // rounds in game A, zero in game B — ranked (or excluded, per
        // REQ-409) independently per game, never combined into one number.
        var alex = await SeedMemberAsync("Alex");
        await SeedQualifyingRoundsAsync(alex.Id, 60, 70, 80, 90, 100); // xg-grid median 80.
        var sam = await SeedMemberAsync("Sam");
        await SeedQualifyingRoundsAsync(sam.Id, OtherGameKey, 10, 20, 30, 40, 50); // xg-path median 30.

        var xgGridPage = await _service.GetGlobalLeaderboardAsync(alex.Id, GameKey, cursor: 0, pageSize: 50);
        var xgPathPage = await _service.GetGlobalLeaderboardAsync(sam.Id, OtherGameKey, cursor: 0, pageSize: 50);

        Assert.That(xgGridPage.Rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "Alex" }));
        Assert.That(xgGridPage.Rows.Single().TotalPoints, Is.EqualTo(80));
        Assert.That(xgPathPage.Rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "Sam" }));
        Assert.That(xgPathPage.Rows.Single().TotalPoints, Is.EqualTo(30));
    }

    [Test]
    public async Task REQ410_GetGlobalLeaderboardAsync_FiveQualifyingRoundsInGameA_ThreeSubThresholdRoundsInGameB_RankedInAOnlyExcludedFromB()
    {
        // The REQ-409 5-round minimum applies independently per game: this
        // player clears it for xg-grid (5 rounds) but not for xg-path (only
        // 3 rounds) — must be ranked for xg-grid and entirely absent from
        // xg-path's response, not present there with a placeholder/zero.
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 10, 20, 30, 40, 50); // xg-grid: 5 rounds, median 30.
        await SeedQualifyingRoundsAsync(you.Id, OtherGameKey, 900, 900, 900); // xg-path: only 3 rounds.

        var xgGridPage = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);
        var xgPathPage = await _service.GetGlobalLeaderboardAsync(you.Id, OtherGameKey, cursor: 0, pageSize: 50);

        Assert.That(xgGridPage.Rows, Has.Count.EqualTo(1));
        Assert.That(xgGridPage.Rows.Single().TotalPoints, Is.EqualTo(30));
        Assert.That(xgGridPage.Rows.Single().Rank, Is.EqualTo(1));
        Assert.That(xgPathPage.Rows, Is.Empty);
        Assert.That(xgPathPage.RequestingUserEntry, Is.Null);
    }

    [Test]
    public async Task REQ410_GetGlobalLeaderboardAsync_SamePlayerQualifiesInBothGames_MediansComputedIndependentlyNeverBlended()
    {
        // If the two games' rounds were ever combined into one median, this
        // player's 10-round pool (5 low xg-grid values + 5 high xg-path
        // values) would compute a single blended median that matches
        // neither game's own, independently-correct median. Asserting both
        // per-game medians separately proves no blending occurred.
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 10, 10, 10, 10, 10); // xg-grid median 10.
        await SeedQualifyingRoundsAsync(you.Id, OtherGameKey, 90, 90, 90, 90, 90); // xg-path median 90.

        var xgGridPage = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);
        var xgPathPage = await _service.GetGlobalLeaderboardAsync(you.Id, OtherGameKey, cursor: 0, pageSize: 50);

        Assert.That(xgGridPage.Rows.Single().TotalPoints, Is.EqualTo(10));
        Assert.That(xgPathPage.Rows.Single().TotalPoints, Is.EqualTo(90));
    }

    // ---- REQ-717/ADR-0036: guest exclusion + claimed-account cutoff ----

    [Test]
    public async Task REQ717_GetGlobalLeaderboardAsync_GuestMember_ExcludedFromRankedList_RegardlessOfQualifyingRoundCount()
    {
        var guest = await SeedGuestMemberAsync("GuestPlayer");
        // Would easily clear REQ-409's 5-round floor if IsGuest weren't
        // checked at all.
        await SeedQualifyingRoundsAsync(guest.Id, 10, 20, 30, 40, 50);

        var page = await _service.GetGlobalLeaderboardAsync(guest.Id, GameKey, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.RequestingUserEntry, Is.Null);
    }

    [Test]
    public async Task REQ717_GetGlobalLeaderboardAsync_ClaimedAccount_RoundsClosedBeforeClaimingNeverCountTowardQualification()
    {
        var claimedAt = DateTime.UtcNow.AddDays(-5);
        var you = await SeedClaimedMemberAsync("You", claimedAt);
        // All 5 rounds closed BEFORE the claim moment — none should count,
        // even though there are enough of them to otherwise clear REQ-409's
        // 5-round floor.
        for (var i = 0; i < 5; i++)
            await SeedLockedGuessAtAsync(you.Id, 10 * (i + 1), claimedAt.AddDays(-1 - i));

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.RequestingUserEntry, Is.Null);
    }

    [Test]
    public async Task REQ717_GetGlobalLeaderboardAsync_ClaimedAccount_OnlyRoundsClosedAfterClaimingCountTowardMedian()
    {
        var claimedAt = DateTime.UtcNow.AddDays(-5);
        var you = await SeedClaimedMemberAsync("You", claimedAt);
        // Two rounds closed BEFORE claiming, carrying a deliberately extreme
        // FinalPoints — must never contribute, whether to the qualification
        // floor or the median.
        await SeedLockedGuessAtAsync(you.Id, 999, claimedAt.AddDays(-1));
        await SeedLockedGuessAtAsync(you.Id, 999, claimedAt.AddDays(-2));
        // Five rounds closed AFTER claiming, all worth 10 — these alone
        // must both qualify and set the median.
        for (var i = 0; i < 5; i++)
            await SeedLockedGuessAtAsync(you.Id, 10, claimedAt.AddDays(1 + i));

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Has.Count.EqualTo(1));
        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(10));
    }

    [Test]
    public async Task REQ717_GetGlobalLeaderboardAsync_NeverClaimedAccount_ClaimedAtNullNeverExcludesAnyRound()
    {
        // The default shape for every account that was never a guest at
        // all: ClaimedAt is null from creation, so the "closed after
        // claiming" narrowing must never exclude anything for it — same
        // qualification behavior as before REQ-717 existed.
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 10, 20, 30, 40, 50);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Has.Count.EqualTo(1));
        Assert.That(page.Rows.Single().TotalPoints, Is.EqualTo(30));
    }

    // The cutoff is strictly-after (GuessRepository
    // .GetPerRoundFinalPointsByUserIdsAsync's `round.ClosedAt >
    // user.ClaimedAt`, not `>=`) — a round that closes at the exact same
    // instant claiming happened must still be excluded, not treated as
    // "already after." Pins down the precise boundary rather than only the
    // clearly-before/clearly-after cases the two tests above already cover:
    // 4 genuinely-qualifying rounds (closed strictly after claiming) plus 1
    // closed at the exact ClaimedAt instant. If that boundary round wrongly
    // counted, this member would clear REQ-409's 5-round floor and be
    // ranked; since it doesn't, only 4 real qualifying rounds exist and the
    // member stays excluded.
    [Test]
    public async Task REQ717_GetGlobalLeaderboardAsync_ClaimedAccount_RoundClosedExactlyAtClaimedAtInstant_ExcludedNotIncluded()
    {
        var claimedAt = DateTime.UtcNow.AddDays(-5);
        var you = await SeedClaimedMemberAsync("You", claimedAt);
        for (var i = 0; i < 4; i++)
            await SeedLockedGuessAtAsync(you.Id, 10 * (i + 1), claimedAt.AddDays(1 + i));
        await SeedLockedGuessAtAsync(you.Id, 999, claimedAt);

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.RequestingUserEntry, Is.Null);
    }

    // Distinct from OnlyRoundsClosedAfterClaimingCountTowardMedian above
    // (which seeds exactly 5 post-claim rounds, clearing the floor): here
    // there are only 3 real post-claim qualifying rounds, so the member must
    // stay excluded even though 3 pre-claim rounds also exist (6 total
    // closed rounds — enough to clear REQ-409's 5-round floor if pre-claim
    // rounds wrongly counted toward it). Pins down that the floor is
    // computed only from post-claim qualifying rounds, not "enough rounds
    // exist in total."
    [Test]
    public async Task REQ717_GetGlobalLeaderboardAsync_ClaimedAccount_FewerThanFiveQualifyingPostClaimRounds_ExcludedEvenWithPreClaimRoundsPresent()
    {
        var claimedAt = DateTime.UtcNow.AddDays(-10);
        var you = await SeedClaimedMemberAsync("You", claimedAt);
        for (var i = 0; i < 3; i++)
            await SeedLockedGuessAtAsync(you.Id, 999, claimedAt.AddDays(-1 - i));
        for (var i = 0; i < 3; i++)
            await SeedLockedGuessAtAsync(you.Id, 10 * (i + 1), claimedAt.AddDays(1 + i));

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.RequestingUserEntry, Is.Null);
    }

    // ---- REQ-717/ADR-0036: guest participation in round-scoped leaderboards ----
    // REQ-407/408's own methods never check IsGuest at all (only REQ-409's
    // GetGlobalLeaderboardAsync/qualifying-rounds query does — see the
    // exclusion tests above), so every other test in this file already
    // exercises these two methods with plain, unlabeled Guid-based UserIds,
    // which implicitly covers a guest identity too (there's no code path
    // that could tell the difference). These two tests instead name a guest
    // explicitly, tying that implicit coverage to REQ-717's literal
    // acceptance criterion ("the guest appears ranked exactly like any other
    // participant... no new query logic") rather than leaving it to
    // inference.

    [Test]
    public async Task REQ717_GetActiveRoundLeaderboardAsync_GuestParticipant_AppearsRankedExactlyLikeAnyOtherParticipant()
    {
        var guest = await SeedGuestMemberAsync("GuestPlayer");
        var cellId = Guid.NewGuid();
        var round = await SeedRoundAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        _fakeGameModule.GetCellIdsResult = _ => [cellId];
        await SeedGuessAsync(round.Id, guest.Id, cellId, isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid());

        var page = await _service.GetActiveRoundLeaderboardAsync(guest.Id, round, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Has.Count.EqualTo(1));
        Assert.That(page.Rows.Single().DisplayName, Is.EqualTo("GuestPlayer"));
        Assert.That(page.Rows.Single().IsRequestingUser, Is.True);
    }

    [Test]
    public async Task REQ717_GetClosedRoundLeaderboardAsync_GuestParticipant_AppearsRankedExactlyLikeAnyOtherParticipant()
    {
        var guest = await SeedGuestMemberAsync("GuestPlayer");
        var realMember = await SeedMemberAsync("RealPlayer");
        var round = await SeedRoundAsync(DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1), closedAt: DateTime.UtcNow.AddDays(-1));
        await SeedGuessAsync(round.Id, guest.Id, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 10);
        await SeedGuessAsync(round.Id, realMember.Id, Guid.NewGuid(), isCorrect: false, attemptCount: FakeGameModule.DefaultMaxAttempts, finalPoints: ScoringRules.MaxPointsPerCell);

        var result = await _service.GetClosedRoundLeaderboardAsync(round.Id, guest.Id, cursor: 0, pageSize: 50);

        Assert.That(result.Status, Is.EqualTo(ClosedRoundLeaderboardStatus.Found));
        Assert.That(result.Page!.Rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "GuestPlayer", "RealPlayer" }));
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

        var page = await _service.GetGlobalLeaderboardAsync(members[0].Id, GameKey, cursor: 0, pageSize: 2);

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

        var firstPage = await _service.GetGlobalLeaderboardAsync(members[0].Id, GameKey, cursor: 0, pageSize: 2);
        var secondPage = await _service.GetGlobalLeaderboardAsync(members[0].Id, GameKey, cursor: firstPage.NextCursor!.Value, pageSize: 2);
        var thirdPage = await _service.GetGlobalLeaderboardAsync(members[0].Id, GameKey, cursor: secondPage.NextCursor!.Value, pageSize: 2);

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
        var page = await _service.GetGlobalLeaderboardAsync(members[4].Id, GameKey, cursor: 0, pageSize: 2);

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

        var page = await _service.GetGlobalLeaderboardAsync(member.Id, GameKey, cursor: 50, pageSize: 10);

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
        await SeedGuessAsync(round.Id, alex.Id, cellA, isCorrect: false, attemptCount: FakeGameModule.DefaultMaxAttempts);
        await SeedGuessAsync(round.Id, alex.Id, cellB, isCorrect: false, attemptCount: FakeGameModule.DefaultMaxAttempts);
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
        await SeedGuessAsync(round.Id, zoe.Id, cellA, isCorrect: false, attemptCount: FakeGameModule.DefaultMaxAttempts);
        await SeedGuessAsync(round.Id, amy.Id, cellB, isCorrect: false, attemptCount: FakeGameModule.DefaultMaxAttempts);

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
        await SeedGuessAsync(round.Id, alex.Id, cellA, isCorrect: false, attemptCount: FakeGameModule.DefaultMaxAttempts, finalPoints: ScoringRules.MaxPointsPerCell);
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
        await SeedGuessAsync(mostRecentlyClosedRound.Id, alex.Id, Guid.NewGuid(), isCorrect: false, attemptCount: FakeGameModule.DefaultMaxAttempts, finalPoints: ScoringRules.MaxPointsPerCell);

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

    // ---- REQ-411/S-178: single player's stats/profile view -----------------
    // GetUserStatsAsync reuses GetPerRoundFinalPointsByUserIdsAsync (REQ-408/
    // 409's existing query) and the private GetRankedMembersAsync helper
    // GetGlobalLeaderboardAsync itself uses (extracted, not reimplemented) —
    // these tests exercise the one new method directly, own id/other-id
    // symmetry and the API-layer shape/401/404 concerns are covered instead
    // by XGArcade.Api.Tests/UserEndpointTests.cs per the REQ's own "Test
    // level" split (Unit here, API there).

    [Test]
    public async Task REQ411_GetUserStatsAsync_ZeroQualifyingRounds_ReturnsNoRoundsPlayedShapeNotZeroFilled()
    {
        var member = await SeedMemberAsync("Alex");

        var stats = await _service.GetUserStatsAsync(member.Id, GameKey);

        Assert.That(stats.HasRoundsPlayed, Is.False);
        Assert.That(stats.RoundsPlayed, Is.EqualTo(0));
        Assert.That(stats.BestFinalPoints, Is.Null);
        Assert.That(stats.AverageFinalPoints, Is.Null);
        Assert.That(stats.Rank, Is.Null);
    }

    [Test]
    public async Task REQ411_GetUserStatsAsync_FewerThanFiveQualifyingRounds_StatsPresentButRankOmitted()
    {
        var you = await SeedMemberAsync("You");
        // Sorted: 10, 20, 30, 40 -> best (min) 10, average 25. Only 4
        // qualifying rounds, below REQ-409's 5-round ranking minimum.
        await SeedQualifyingRoundsAsync(you.Id, 40, 10, 30, 20);

        var stats = await _service.GetUserStatsAsync(you.Id, GameKey);

        Assert.That(stats.HasRoundsPlayed, Is.True);
        Assert.That(stats.RoundsPlayed, Is.EqualTo(4));
        Assert.That(stats.BestFinalPoints, Is.EqualTo(10));
        Assert.That(stats.AverageFinalPoints, Is.EqualTo(25.0));
        Assert.That(stats.Rank, Is.Null, "below REQ-409's 5-round minimum: omitted, not a placeholder rank");
    }

    [Test]
    public async Task REQ411_GetUserStatsAsync_BestIsMinimumFinalPointsNotMedianOrSum()
    {
        // ADR-0021 (golf scoring, lowest wins): Best must be the minimum per-
        // round total. Sorted: 10, 20, 30, 40, 100 -> min 10, median 30, sum
        // 200 — three different values, so an implementation that wrongly
        // used the median or a sum would be caught here.
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 100, 10, 30, 40, 20);

        var stats = await _service.GetUserStatsAsync(you.Id, GameKey);

        Assert.That(stats.BestFinalPoints, Is.EqualTo(10));
    }

    [Test]
    public async Task REQ411_GetUserStatsAsync_AverageIsArithmeticMeanNotMedian()
    {
        // Same 5 values as above: mean = (10+20+30+40+100)/5 = 40, which
        // differs from both the median (30) and the min/Best (10) — proves
        // Average specifically uses the mean, not a reused median formula.
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 100, 10, 30, 40, 20);

        var stats = await _service.GetUserStatsAsync(you.Id, GameKey);

        Assert.That(stats.AverageFinalPoints, Is.EqualTo(40.0));
    }

    [Test]
    public async Task REQ411_GetUserStatsAsync_FiveOrMoreQualifyingRounds_RankMatchesGetGlobalLeaderboardAsyncsOwnRanking()
    {
        // Cross-checks against GetGlobalLeaderboardAsync's own ranked output
        // for the same GameKey/membership, rather than asserting an
        // arbitrary expected number — proves the reused GetRankedMembersAsync
        // helper actually produces the same rank the leaderboard itself would
        // show, not just a plausible-looking one.
        var alex = await SeedMemberAsync("Alex");
        var sam = await SeedMemberAsync("Sam");
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(alex.Id, 60, 70, 80, 90, 100); // median 80
        await SeedQualifyingRoundsAsync(sam.Id, 10, 20, 30, 40, 50);   // median 30
        await SeedQualifyingRoundsAsync(you.Id, 40, 45, 50, 55, 60);   // median 50

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, GameKey, cursor: 0, pageSize: 50);
        var expectedRankByUserId = page.Rows.ToDictionary(r => r.UserId, r => r.Rank);

        var alexStats = await _service.GetUserStatsAsync(alex.Id, GameKey);
        var samStats = await _service.GetUserStatsAsync(sam.Id, GameKey);
        var youStats = await _service.GetUserStatsAsync(you.Id, GameKey);

        Assert.That(alexStats.Rank, Is.EqualTo(expectedRankByUserId[alex.Id]));
        Assert.That(samStats.Rank, Is.EqualTo(expectedRankByUserId[sam.Id]));
        Assert.That(youStats.Rank, Is.EqualTo(expectedRankByUserId[you.Id]));
        // Pin the concrete values too, so this test still fails clearly if
        // GetGlobalLeaderboardAsync's own ranking ever regresses alongside it.
        Assert.That(samStats.Rank, Is.EqualTo(1));
        Assert.That(youStats.Rank, Is.EqualTo(2));
        Assert.That(alexStats.Rank, Is.EqualTo(3));
    }

    [Test]
    public async Task REQ411_GetUserStatsAsync_QualifyingRoundsInAnotherGame_NeverCountTowardThisGamesStats()
    {
        // Same cross-game-isolation shape as the REQ-410 tests above: 5
        // qualifying xg-grid rounds (clears REQ-409's floor, ranked) plus 3
        // xg-path rounds (below the floor, unranked) for the same player —
        // each GameKey's stats must reflect only its own rounds.
        var you = await SeedMemberAsync("You");
        await SeedQualifyingRoundsAsync(you.Id, 10, 20, 30, 40, 50); // xg-grid: 5 rounds, best 10, avg 30.
        await SeedQualifyingRoundsAsync(you.Id, OtherGameKey, 900, 900, 900); // xg-path: only 3 rounds.

        var xgGridStats = await _service.GetUserStatsAsync(you.Id, GameKey);
        var xgPathStats = await _service.GetUserStatsAsync(you.Id, OtherGameKey);

        Assert.That(xgGridStats.HasRoundsPlayed, Is.True);
        Assert.That(xgGridStats.RoundsPlayed, Is.EqualTo(5));
        Assert.That(xgGridStats.BestFinalPoints, Is.EqualTo(10));
        Assert.That(xgGridStats.AverageFinalPoints, Is.EqualTo(30.0));
        Assert.That(xgGridStats.Rank, Is.EqualTo(1), "the only xg-grid qualifier, so ranked #1");

        Assert.That(xgPathStats.HasRoundsPlayed, Is.True);
        Assert.That(xgPathStats.RoundsPlayed, Is.EqualTo(3));
        Assert.That(xgPathStats.BestFinalPoints, Is.EqualTo(900));
        Assert.That(xgPathStats.AverageFinalPoints, Is.EqualTo(900.0));
        Assert.That(xgPathStats.Rank, Is.Null, "only 3 xg-path rounds, below REQ-409's 5-round minimum");
    }

    [Test]
    public async Task REQ411_GetUserStatsAsync_GuestAccount_StatsFiguresIncludedButRankStillExcluded()
    {
        // REQ-411's own "Out of scope" text is explicit: a guest's
        // rounds-played/best/average are shown the same as a claimed
        // account's — only the Rank figure still inherits REQ-409/717's
        // existing guest-eligibility gate (GetRankedMembersAsync, unchanged
        // by this fix). 5 qualifying rounds so this also proves Rank stays
        // excluded even once the 5-round minimum would otherwise be cleared,
        // not just "guest with too few rounds anyway".
        var guest = await SeedGuestMemberAsync("GuestPlayer");
        await SeedQualifyingRoundsAsync(guest.Id, 10, 20, 30, 40, 50); // sorted: best 10, average 30.

        var stats = await _service.GetUserStatsAsync(guest.Id, GameKey);

        Assert.That(stats.HasRoundsPlayed, Is.True, "a guest's rounds-played must not be zeroed out");
        Assert.That(stats.RoundsPlayed, Is.EqualTo(5));
        Assert.That(stats.BestFinalPoints, Is.EqualTo(10));
        Assert.That(stats.AverageFinalPoints, Is.EqualTo(30.0));
        Assert.That(stats.Rank, Is.Null, "REQ-409/717's guest ranking exclusion is deliberately unchanged");
    }

    [Test]
    public async Task REQ411_GetUserStatsAsync_ClaimedAccountRoundsClosedBeforeClaiming_StatsFiguresIncluded()
    {
        // Mirrors REQ717_GetGlobalLeaderboardAsync_ClaimedAccount_RoundsClosedBeforeClaimingNeverCountTowardQualification
        // above, but proves the opposite outcome now applies to
        // GetUserStatsAsync's stats figures specifically: before this REQ-411
        // fix, GetPerRoundFinalPointsByUserIdsAsync unconditionally excluded
        // a claimed account's pre-claim rounds, which GetUserStatsAsync
        // inherited for RoundsPlayed/Best/Average too. With
        // applyGuestEligibilityRules: false, these 5 pre-claim rounds must
        // now count toward the stats figures — while Rank must still be null,
        // since GetRankedMembersAsync (unchanged) still excludes them from
        // ranking, leaving this account with 0 *ranking*-qualifying rounds.
        var claimedAt = DateTime.UtcNow.AddDays(-5);
        var you = await SeedClaimedMemberAsync("You", claimedAt);
        for (var i = 0; i < 5; i++)
            await SeedLockedGuessAtAsync(you.Id, 10 * (i + 1), claimedAt.AddDays(-1 - i)); // all closed BEFORE claiming.

        var stats = await _service.GetUserStatsAsync(you.Id, GameKey);

        Assert.That(stats.HasRoundsPlayed, Is.True, "pre-claim rounds must count toward stats figures now, unlike ranking");
        Assert.That(stats.RoundsPlayed, Is.EqualTo(5));
        Assert.That(stats.BestFinalPoints, Is.EqualTo(10));
        Assert.That(stats.AverageFinalPoints, Is.EqualTo(30.0));
        Assert.That(stats.Rank, Is.Null, "GetRankedMembersAsync still excludes pre-claim rounds, so this account has 0 ranking-qualifying rounds");
    }
}

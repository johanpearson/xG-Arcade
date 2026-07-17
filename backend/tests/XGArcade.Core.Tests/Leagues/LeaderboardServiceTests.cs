using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Leagues;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Tests.Leagues;

// REQ-401/404 (docs/requirements-document.md §4.4): Core.Leagues' first real
// code (S-011) — global league auto-membership plus its leaderboard read.
// REQ-607/S-034 added pagination (docs/backlog.md S-034) — the same tests
// were updated in place for the now-paginated response shape rather than
// duplicated, per this repo's convention.
// Same no-mocking-framework, real-InMemory-backed-repository pattern as
// RoundCloseServiceScoringTests.
public class LeaderboardServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private ILeagueRepository _leagueRepository = null!;
    private IUserRepository _userRepository = null!;
    private IGuessRepository _guessRepository = null!;
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
        _service = new LeaderboardService(_leagueRepository, _userRepository, _guessRepository);
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

    [Test]
    public async Task REQ401_GetGlobalLeaderboardAsync_NewMemberWithNoGuesses_AppearsWithZeroPoints()
    {
        // ADR-0021: 0 is the BEST possible score under the lowest-wins
        // model — a brand-new member legitimately starts in first place,
        // not last.
        var member = await SeedMemberAsync("Alex");

        var page = await _service.GetGlobalLeaderboardAsync(member.Id, cursor: 0, pageSize: 50);

        Assert.That(page.Rows, Has.Count.EqualTo(1));
        Assert.That(page.Rows[0].Rank, Is.EqualTo(1));
        Assert.That(page.Rows[0].UserId, Is.EqualTo(member.Id));
        Assert.That(page.Rows[0].TotalPoints, Is.EqualTo(0));
        Assert.That(page.Rows[0].IsRequestingUser, Is.True);
        Assert.That(page.RequestingUserEntry?.UserId, Is.EqualTo(member.Id));
        Assert.That(page.HasMore, Is.False);
        Assert.That(page.NextCursor, Is.Null);
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

        var page = await _service.GetGlobalLeaderboardAsync(you.Id, cursor: 0, pageSize: 50);

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
            await SeedLockedGuessAsync(member.Id, members.IndexOf(member) * 10);

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
            await SeedLockedGuessAsync(member.Id, members.IndexOf(member) * 10);

        // Member4 has the highest TotalPoints, so ranks last (5th) —
        // outside a pageSize=2 first page.
        var page = await _service.GetGlobalLeaderboardAsync(members[4].Id, cursor: 0, pageSize: 2);

        Assert.That(page.Rows.Any(r => r.IsRequestingUser), Is.False);
        Assert.That(page.RequestingUserEntry, Is.Not.Null);
        Assert.That(page.RequestingUserEntry!.UserId, Is.EqualTo(members[4].Id));
        Assert.That(page.RequestingUserEntry.Rank, Is.EqualTo(5));
    }

    [Test]
    public async Task REQ607_GetGlobalLeaderboardAsync_CursorBeyondMembership_ReturnsEmptyPageNotError()
    {
        var member = await SeedMemberAsync("Alex");

        var page = await _service.GetGlobalLeaderboardAsync(member.Id, cursor: 50, pageSize: 10);

        Assert.That(page.Rows, Is.Empty);
        Assert.That(page.HasMore, Is.False);
        Assert.That(page.NextCursor, Is.Null);
        Assert.That(page.RequestingUserEntry, Is.Not.Null);
    }
}

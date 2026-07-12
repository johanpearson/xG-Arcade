using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Leagues;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Tests.Leagues;

// REQ-401/404 (docs/requirements-document.md §4.4): Core.Leagues' first real
// code (S-011) — global league auto-membership plus its leaderboard read.
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

        var rows = await _service.GetGlobalLeaderboardAsync(member.Id);

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].UserId, Is.EqualTo(member.Id));
        Assert.That(rows[0].TotalPoints, Is.EqualTo(0));
        Assert.That(rows[0].IsRequestingUser, Is.True);
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

        var rows = await _service.GetGlobalLeaderboardAsync(you.Id);

        // Ascending by TotalPoints: Sam (120) < You (50 + 88 = 138) < Alex (142).
        Assert.That(rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "Sam", "You", "Alex" }));
        Assert.That(rows.Select(r => r.TotalPoints), Is.EqualTo(new[] { 120, 138, 142 }));
        Assert.That(rows.Single(r => r.DisplayName == "You").IsRequestingUser, Is.True);
        Assert.That(rows.Where(r => r.DisplayName != "You").All(r => !r.IsRequestingUser), Is.True);
    }
}

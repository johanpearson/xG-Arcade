using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Leagues;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Tests.Leagues;

// REQ-402/403 (docs/requirements-document.md): custom league create/join.
// Same no-mocking-framework, real-InMemory-backed-repository pattern as
// LeaderboardServiceTests — ILeagueRepository is exercised through the real
// LeagueRepository against an InMemory-backed XGArcadeDbContext; only
// IInviteCodeGenerator is faked (FakeInviteCodeGenerator, this folder),
// since a real random generator can't be made to collide on demand, which
// is exactly what the collision-retry tests below need to force.
public class LeagueServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private ILeagueRepository _leagueRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _leagueRepository = new LeagueRepository(_dbContext);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private LeagueService CreateService(params string[] inviteCodes) =>
        new(_leagueRepository, new FakeInviteCodeGenerator(inviteCodes));

    [Test]
    public async Task REQ402_CreateCustomLeagueAsync_CreatesLeagueWithGivenNameTypeCustomAndGeneratedInviteCode()
    {
        var creatorId = Guid.NewGuid();
        var service = CreateService("ABC123");

        var league = await service.CreateCustomLeagueAsync("Friends League", creatorId);

        Assert.That(league.Name, Is.EqualTo("Friends League"));
        Assert.That(league.Type, Is.EqualTo(LeagueTypes.Custom));
        Assert.That(league.InviteCode, Is.EqualTo("ABC123"));
        Assert.That(league.CreatedByUserId, Is.EqualTo(creatorId));
    }

    [Test]
    public async Task REQ402_CreateCustomLeagueAsync_AutomaticallyAddsCreatorAsMember()
    {
        var creatorId = Guid.NewGuid();
        var service = CreateService("ABC123");

        var league = await service.CreateCustomLeagueAsync("Friends League", creatorId);

        var isMember = await _leagueRepository.IsMemberAsync(league.Id, creatorId);
        Assert.That(isMember, Is.True);
    }

    [Test]
    public async Task REQ402_CreateCustomLeagueAsync_RetriesWithANewCode_WhenTheFirstGeneratedCodeIsAlreadyInUse()
    {
        // Seed an existing custom league already using "TAKEN1" — this
        // proves InviteCodeExistsAsync's pre-check, not just the generator,
        // is what drives the retry.
        _dbContext.Leagues.Add(new League
        {
            Id = Guid.NewGuid(),
            Name = "Existing League",
            Type = LeagueTypes.Custom,
            InviteCode = "TAKEN1",
            CreatedByUserId = Guid.NewGuid(),
        });
        await _dbContext.SaveChangesAsync();

        var service = CreateService("TAKEN1", "FRESH2");

        var league = await service.CreateCustomLeagueAsync("New League", Guid.NewGuid());

        Assert.That(league.InviteCode, Is.EqualTo("FRESH2"));
    }

    [Test]
    public void REQ402_CreateCustomLeagueAsync_ThrowsAfterExhaustingRetryAttempts_WhenEveryGeneratedCodeCollides()
    {
        const string existingCode = "DUP0001";
        _dbContext.Leagues.Add(new League
        {
            Id = Guid.NewGuid(),
            Name = "Existing League",
            Type = LeagueTypes.Custom,
            InviteCode = existingCode,
            CreatedByUserId = Guid.NewGuid(),
        });
        _dbContext.SaveChanges();

        // LeagueService's own MaxInviteCodeAttempts is 5 — every one of
        // these 5 generated codes collides, so this must throw rather than
        // loop forever or silently create a league with a duplicate code.
        var service = CreateService(existingCode, existingCode, existingCode, existingCode, existingCode);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateCustomLeagueAsync("New League", Guid.NewGuid()));
    }

    [Test]
    public async Task REQ403_JoinByInviteCodeAsync_ValidCode_AddsCallerAsMemberAndReturnsJoinedOutcome()
    {
        var service = CreateService("ABC123");
        var league = await service.CreateCustomLeagueAsync("Friends League", Guid.NewGuid());
        var joinerId = Guid.NewGuid();

        var result = await service.JoinByInviteCodeAsync("ABC123", joinerId);

        Assert.That(result.Outcome, Is.EqualTo(JoinLeagueOutcome.Joined));
        Assert.That(result.League!.Id, Is.EqualTo(league.Id));
        Assert.That(await _leagueRepository.IsMemberAsync(league.Id, joinerId), Is.True);
    }

    [Test]
    public async Task REQ403_JoinByInviteCodeAsync_InvalidCode_ReturnsInvalidCodeOutcomeAndCreatesNoMembership()
    {
        var service = CreateService("ABC123");
        await service.CreateCustomLeagueAsync("Friends League", Guid.NewGuid());
        var joinerId = Guid.NewGuid();

        var result = await service.JoinByInviteCodeAsync("DOESNOTEXIST", joinerId);

        Assert.That(result.Outcome, Is.EqualTo(JoinLeagueOutcome.InvalidCode));
        Assert.That(result.League, Is.Null);
        Assert.That(await _dbContext.LeagueMemberships.AnyAsync(m => m.UserId == joinerId), Is.False);
    }

    [Test]
    public async Task REQ403_JoinByInviteCodeAsync_AlreadyMember_ReturnsAlreadyMemberOutcomeWithoutDuplicateMembership()
    {
        var service = CreateService("ABC123");
        var creatorId = Guid.NewGuid();
        var league = await service.CreateCustomLeagueAsync("Friends League", creatorId);

        // The creator is already a member (auto-added on create) — rejoining
        // with the same code must be idempotent, not a duplicate-key error.
        var result = await service.JoinByInviteCodeAsync("ABC123", creatorId);

        Assert.That(result.Outcome, Is.EqualTo(JoinLeagueOutcome.AlreadyMember));
        Assert.That(result.League!.Id, Is.EqualTo(league.Id));
        var membershipCount = await _dbContext.LeagueMemberships
            .CountAsync(m => m.LeagueId == league.Id && m.UserId == creatorId);
        Assert.That(membershipCount, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ402_GetMemberLeaguesAsync_ReturnsOnlyTheGivenUsersCustomLeagues_ExcludingGlobalAndOtherUsersLeagues()
    {
        var service = CreateService("MINE001", "MINE002", "OTHER01");
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var myFirstLeague = await service.CreateCustomLeagueAsync("Mine 1", userId);
        var mySecondLeague = await service.CreateCustomLeagueAsync("Mine 2", userId);
        await service.CreateCustomLeagueAsync("Someone Else's League", otherUserId);

        // Also a member of the global league (REQ-401), which must never
        // appear in this "my custom leagues" list.
        var globalLeague = await _leagueRepository.GetOrCreateGlobalLeagueAsync();
        await _leagueRepository.AddMembershipAsync(globalLeague.Id, userId);

        var myLeagues = await service.GetMemberLeaguesAsync(userId);

        Assert.That(myLeagues.Select(l => l.Id), Is.EquivalentTo(new[] { myFirstLeague.Id, mySecondLeague.Id }));
        Assert.That(myLeagues.All(l => l.Type == LeagueTypes.Custom), Is.True);
    }
}

using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Auth;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Tests.Auth;

// REQ-710 (docs/requirements-document.md §4.9): AccountDeletionService's own
// unit coverage. Same no-mocking-framework, real-InMemory-backed-repository
// pattern as LeaderboardServiceTests — the only fake here is
// ISupabaseAuthClient, since a real HTTP call to Supabase's Admin API is
// exactly what unit tests must never do (docs/coding-guidelines.md).
public class AccountDeletionServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IUserRepository _userRepository = null!;
    private IGuessRepository _guessRepository = null!;
    private ILeagueRepository _leagueRepository = null!;
    private FakeSupabaseAuthClient _fakeAuthClient = null!;
    private AccountDeletionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _userRepository = new UserRepository(_dbContext);
        _guessRepository = new GuessRepository(_dbContext);
        _leagueRepository = new LeagueRepository(_dbContext);
        _fakeAuthClient = new FakeSupabaseAuthClient();
        _service = new AccountDeletionService(_userRepository, _guessRepository, _leagueRepository, _fakeAuthClient);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private async Task<User> SeedUserAsync(Guid? authProviderUserId = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = authProviderUserId ?? Guid.NewGuid(),
            Email = $"{Guid.NewGuid()}@example.com",
            DisplayName = $"Player-{Guid.NewGuid():N}",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user;
    }

    private async Task<Guess> SeedGuessAsync(Guid userId)
    {
        var guess = new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = Guid.NewGuid(),
            UserId = userId,
            CellId = Guid.NewGuid(),
            SubmittedName = "Someone",
            IsCorrect = true,
            AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Guesses.Add(guess);
        await _dbContext.SaveChangesAsync();
        return guess;
    }

    [Test]
    public async Task REQ710_DeleteAccountAsync_AnonymizesGuessRows_SeversLinkWithoutDeletingRows()
    {
        var user = await SeedUserAsync();
        var guessOne = await SeedGuessAsync(user.Id);
        var guessTwo = await SeedGuessAsync(user.Id);

        var result = await _service.DeleteAccountAsync(user.Id);

        Assert.That(result.Success, Is.True);
        // The rows themselves must survive — other players' historical
        // uniqueness scores and leaderboard totals depend on the total
        // guess count staying intact (REQ-710).
        var remainingGuesses = await _dbContext.Guesses.AsNoTracking().ToListAsync();
        Assert.That(remainingGuesses, Has.Count.EqualTo(2));
        Assert.That(remainingGuesses.Select(g => g.Id), Is.EquivalentTo(new[] { guessOne.Id, guessTwo.Id }));
        // No reversible link back to the deleted user remains on any of them.
        Assert.That(remainingGuesses.All(g => g.UserId == null), Is.True);
    }

    [Test]
    public async Task REQ710_DeleteAccountAsync_RemovesLeagueMembershipAndUserRow()
    {
        var user = await SeedUserAsync();
        var globalLeague = await _leagueRepository.GetOrCreateGlobalLeagueAsync();
        await _leagueRepository.AddMembershipAsync(globalLeague.Id, user.Id);

        var result = await _service.DeleteAccountAsync(user.Id);

        Assert.That(result.Success, Is.True);
        var remainingUser = await _dbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == user.Id);
        Assert.That(remainingUser, Is.Null);
        var remainingMemberships = await _dbContext.LeagueMemberships.AsNoTracking().Where(m => m.UserId == user.Id).ToListAsync();
        Assert.That(remainingMemberships, Is.Empty);
    }

    [Test]
    public async Task REQ710_DeleteAccountAsync_CallsSupabaseDeleteWithTheUsersAuthProviderUserId()
    {
        var authProviderUserId = Guid.NewGuid();
        var user = await SeedUserAsync(authProviderUserId);

        var result = await _service.DeleteAccountAsync(user.Id);

        Assert.That(result.Success, Is.True);
        Assert.That(_fakeAuthClient.DeleteUserCalledWith, Is.EqualTo(authProviderUserId));
    }

    [Test]
    public async Task REQ710_DeleteAccountAsync_UnknownUserId_ReturnsFailureWithoutSideEffects()
    {
        var result = await _service.DeleteAccountAsync(Guid.NewGuid());

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
        // Never even reaches Supabase for a user that doesn't exist locally.
        Assert.That(_fakeAuthClient.DeleteUserCalledWith, Is.Null);
    }

    [Test]
    public async Task REQ710_DeleteAccountAsync_SupabaseDeleteFails_ReturnsFailureAfterLocalDataAlreadyRemoved()
    {
        var user = await SeedUserAsync();
        await SeedGuessAsync(user.Id);
        _fakeAuthClient.DeleteUserResult = _ => false;

        var result = await _service.DeleteAccountAsync(user.Id);

        // Documented, deliberate ordering (AccountDeletionService's own doc
        // comment) — not a bug: local writes are not part of the same
        // transaction as the external Supabase call, so a failure there
        // still leaves local data gone.
        Assert.That(result.Success, Is.False);
        var remainingUser = await _dbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == user.Id);
        Assert.That(remainingUser, Is.Null);
        var remainingGuesses = await _dbContext.Guesses.AsNoTracking().ToListAsync();
        Assert.That(remainingGuesses.Single().UserId, Is.Null);
    }

    // Test double for ISupabaseAuthClient — never makes a real HTTP call.
    // SignUpAsync/SignInWithPasswordAsync are no-op stubs since
    // AccountDeletionService never calls them; only DeleteUserAsync matters
    // here.
    private class FakeSupabaseAuthClient : ISupabaseAuthClient
    {
        public Guid? DeleteUserCalledWith { get; private set; }

        public Func<Guid, bool> DeleteUserResult { get; set; } = _ => true;

        // AccountDeletionService itself never calls these two — the
        // confirmation-step re-verification (REQ-710) is the calling
        // endpoint's job (AuthController.DeleteAccount), not this service's
        // — kept as harmless no-op stubs rather than omitted, so this fake
        // still fully implements ISupabaseAuthClient.
        public Task<SupabaseAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() });

        public Task<SupabaseAuthResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() });

        // REQ-715: AccountDeletionService never calls this either — same
        // harmless no-op stub reasoning as the two above.
        public Task<SupabaseAuthResult> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() });

        public Task<bool> DeleteUserAsync(Guid authProviderUserId, CancellationToken cancellationToken = default)
        {
            DeleteUserCalledWith = authProviderUserId;
            return Task.FromResult(DeleteUserResult(authProviderUserId));
        }
    }
}

using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Auth;
using XGArcade.Core.Games;
using XGArcade.Core.Tests.Rounds;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.FootballData;
using XGArcade.Games.XGPredict;

namespace XGArcade.Core.Tests.Auth;

// REQ-710 (docs/requirements-document.md §4.9): AccountDeletionService's own
// unit coverage. Same no-mocking-framework, real-InMemory-backed-repository
// pattern as LeaderboardServiceTests — the only fake here is
// ISupabaseAuthClient, since a real HTTP call to Supabase's Admin API is
// exactly what unit tests must never do (docs/coding-guidelines.md).
//
// S-201 (quality-gate fix): AccountDeletionService now depends on
// IEnumerable<IGameModule> instead of IPredictInstanceRepository directly
// (ADR-0003 — see AccountDeletionService's own doc comment). The
// IGameModule[] passed to it below uses a REAL XGPredictGameModule (backed
// by the same real, InMemory-backed _predictInstanceRepository this file
// already used) so the two DB-assertion tests below keep proving actual
// anonymize/hard-delete behavior, not just that a method was called.
// xG Grid/xG Path use FakeGameModule (XGArcade.Core.Tests/Rounds/
// FakeGameModule.cs) instead of a real GridGameModule/XGPathGameModule —
// both are genuine no-op implementations of PurgeUserDataAsync (see each
// module's own doc comment on that method), so a fake is behaviorally
// identical here and avoids pulling their Wikidata/football-data-client
// test infrastructure (private to their own test projects) into this one.
public class AccountDeletionServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IUserRepository _userRepository = null!;
    private IGuessRepository _guessRepository = null!;
    private IPredictInstanceRepository _predictInstanceRepository = null!;
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
        _predictInstanceRepository = new PredictInstanceRepository(_dbContext);
        _leagueRepository = new LeagueRepository(_dbContext);
        _fakeAuthClient = new FakeSupabaseAuthClient();

        // S-201: PurgeUserDataAsync never touches IFootballDataClient (it's
        // only used by XGPredictGameModule.GenerateInstanceAsync, never
        // called from here) — NeverCalledFootballDataClient below throws if
        // that assumption is ever wrong.
        var predictModule = new XGPredictGameModule(_predictInstanceRepository, new NeverCalledFootballDataClient());
        var gameModules = new IGameModule[]
        {
            new FakeGameModule(GridGameKeyForTests),
            new FakeGameModule(PathGameKeyForTests),
            predictModule,
        };
        _service = new AccountDeletionService(
            _userRepository, _guessRepository, gameModules, _leagueRepository, _fakeAuthClient);
    }

    // S-201: mirrors GridGameModule.XGGridGameKey/XGPathGameModule.XGPathGameKey's
    // real values without referencing those projects (see this file's own
    // doc comment for why xG Grid/xG Path use FakeGameModule here) — the
    // exact GameKey string doesn't matter to any assertion in this file,
    // only that every registered module is looped over.
    private const string GridGameKeyForTests = "xg-grid";
    private const string PathGameKeyForTests = "xg-path";

    // S-201: a trivial IFootballDataClient stub — XGPredictGameModule's
    // constructor requires one, but PurgeUserDataAsync (the only method this
    // test file's real predictModule instance ever calls) never touches it.
    // Throws if that ever changes, rather than silently returning an empty
    // result that could mask a real behavior change.
    private sealed class NeverCalledFootballDataClient : IFootballDataClient
    {
        public Task<IReadOnlyList<FootballDataFixture>> GetUpcomingGameweekFixturesAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("Not exercised by AccountDeletionServiceTests — PurgeUserDataAsync never calls this.");

        public Task<FootballDataFixtureResult> GetFixtureResultAsync(int fixtureId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("Not exercised by AccountDeletionServiceTests — PurgeUserDataAsync never calls this.");
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

    private async Task<PredictMatchPrediction> SeedPredictPredictionAsync(Guid userId)
    {
        var prediction = new PredictMatchPrediction
        {
            Id = Guid.NewGuid(),
            PredictMatchId = Guid.NewGuid(),
            UserId = userId,
            HomeGoals = 2,
            AwayGoals = 1,
            SubmittedAt = DateTime.UtcNow,
        };
        _dbContext.PredictMatchPredictions.Add(prediction);
        await _dbContext.SaveChangesAsync();
        return prediction;
    }

    private async Task<PredictPlayerLock> SeedPredictPlayerLockAsync(Guid userId, Guid? predictInstanceId = null)
    {
        var predictPlayerLock = new PredictPlayerLock
        {
            PredictInstanceId = predictInstanceId ?? Guid.NewGuid(),
            UserId = userId,
            LockedAt = DateTime.UtcNow,
        };
        _dbContext.PredictPlayerLocks.Add(predictPlayerLock);
        await _dbContext.SaveChangesAsync();
        return predictPlayerLock;
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
    public async Task REQ710_DeleteAccountAsync_AnonymizesPredictMatchPredictionRows_SeversLinkWithoutDeletingRows()
    {
        var user = await SeedUserAsync();
        var otherUser = await SeedUserAsync();
        var ownPrediction = await SeedPredictPredictionAsync(user.Id);
        var otherPrediction = await SeedPredictPredictionAsync(otherUser.Id);

        var result = await _service.DeleteAccountAsync(user.Id);

        Assert.That(result.Success, Is.True);
        // The row itself must survive — other users' PredictInstance point
        // totals (IPredictInstanceRepository.GetTotalPointsByInstanceIdAsync)
        // depend on it, same reasoning as Guess (REQ-710).
        var remainingOwnPrediction = await _dbContext.PredictMatchPredictions
            .AsNoTracking().SingleAsync(p => p.Id == ownPrediction.Id);
        Assert.That(remainingOwnPrediction.UserId, Is.Null);
        // A different user's prediction in the same seed data must be
        // completely untouched (proves scoping, not an over-broad update).
        var remainingOtherPrediction = await _dbContext.PredictMatchPredictions
            .AsNoTracking().SingleAsync(p => p.Id == otherPrediction.Id);
        Assert.That(remainingOtherPrediction.UserId, Is.EqualTo(otherUser.Id));
    }

    [Test]
    public async Task REQ710_DeleteAccountAsync_HardDeletesPredictPlayerLockRows_ForDeletedUserOnly()
    {
        var user = await SeedUserAsync();
        var otherUser = await SeedUserAsync();
        await SeedPredictPlayerLockAsync(user.Id);
        var otherLock = await SeedPredictPlayerLockAsync(otherUser.Id);

        var result = await _service.DeleteAccountAsync(user.Id);

        Assert.That(result.Success, Is.True);
        // Unlike Guess/PredictMatchPrediction, PredictPlayerLock.UserId is
        // non-nullable (half of its composite primary key) — the row is
        // hard-deleted rather than anonymized (XGArcadeDbContext's own
        // OnModelCreating comment on PredictPlayerLock).
        var remaining = await _dbContext.PredictPlayerLocks
            .AsNoTracking().Where(l => l.UserId == user.Id).ToListAsync();
        Assert.That(remaining, Is.Empty);
        // A different user's lock row in the same seed data must survive.
        var remainingOtherLock = await _dbContext.PredictPlayerLocks
            .AsNoTracking()
            .SingleOrDefaultAsync(l => l.PredictInstanceId == otherLock.PredictInstanceId && l.UserId == otherLock.UserId);
        Assert.That(remainingOtherLock, Is.Not.Null);
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
        var prediction = await SeedPredictPredictionAsync(user.Id);
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
        // S-201 quality-gate fix: proves the "committed before the Supabase
        // failure" guarantee extends through IGameModule.PurgeUserDataAsync
        // to xG Predict's own PredictMatchPrediction table too, not just
        // Guess — same ordering, same non-transactional boundary.
        var remainingPrediction = await _dbContext.PredictMatchPredictions
            .AsNoTracking().SingleAsync(p => p.Id == prediction.Id);
        Assert.That(remainingPrediction.UserId, Is.Null);
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
        // still fully implements ISupabaseAuthClient. captchaToken
        // (REQ-701/REQ-710's 2026-07-25 additions / ADR-0037's amendments)
        // is likewise unused here for the same reason.
        public Task<SupabaseAuthResult> SignUpAsync(string email, string password, string captchaToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() });

        public Task<SupabaseAuthResult> SignInWithPasswordAsync(string email, string password, string captchaToken, CancellationToken cancellationToken = default) =>
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

        // REQ-717: AccountDeletionService never calls either of these —
        // same harmless no-op stub reasoning as SignUp/SignIn above.
        public Task<SupabaseAuthResult> SignInAnonymouslyAsync(string captchaToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() });

        public Task<SupabaseAuthResult> LinkEmailPasswordAsync(string accessToken, string email, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() });
    }
}

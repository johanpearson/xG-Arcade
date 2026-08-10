using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Admin;
using XGArcade.Api.Auth;
using XGArcade.Core.Auth;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Tests;

// API-level coverage for AdminAccountsEndpoints.cs — REQ-507 (GET
// /admin/accounts/metrics) and REQ-508 (GET /admin/accounts/guests/count,
// POST /admin/accounts/guests/clear). Same "Admin" authorization policy
// (Admin__UserIds) as AdminEndpointTests/AdminManagementEndpointTests, but
// unlike AdminManagementEndpointTests' REQ-505/506 endpoints, these three
// are registered unconditionally — including in Production — so this file's
// Production tests assert the opposite of AdminManagementEndpointTests'
// "IsNeverRegistered...ReturnsNotFound" pattern: they must NOT 404 there.
public class AdminAccountsEndpointTests
{
    // Fixed so every test can configure the same "this is an admin" identity
    // via Admin:UserIds without re-creating the factory per test. Distinct
    // from AdminEndpointTests'/AdminManagementEndpointTests' own constants
    // purely so a future refactor that merges these constants doesn't hide
    // an accidental collision.
    private static readonly Guid AdminAuthProviderUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Same in-process HS256 signer/validator as
                // AdminEndpointTests/AdminManagementEndpointTests — see those
                // files' SetUp comments for why (ADR-0017).
                builder.UseSetting("Auth:Mode", "local-e2e");
                builder.UseSetting("Admin:UserIds", AdminAuthProviderUserId.ToString());

                builder.ConfigureServices(services =>
                {
                    // Same in-memory-DbContext swap as every other
                    // XGArcade.Api.Tests file — see AuthEndpointTests' SetUp
                    // comment for why every XGArcadeDbContext-closed
                    // descriptor must be removed, not just the two obvious ones.
                    var xgArcadeDbContextDescriptors = services
                        .Where(d => d.ServiceType == typeof(XGArcadeDbContext)
                            || (d.ServiceType.IsGenericType && d.ServiceType.GetGenericArguments().Contains(typeof(XGArcadeDbContext))))
                        .ToList();
                    foreach (var descriptor in xgArcadeDbContextDescriptors)
                    {
                        services.Remove(descriptor);
                    }

                    var inMemoryDatabaseName = Guid.NewGuid().ToString();
                    services.AddDbContext<XGArcadeDbContext>(options =>
                        options.UseInMemoryDatabase(inMemoryDatabaseName));
                });
            });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    // ---- Seeding helpers ------------------------------------------------

    private async Task<Guid> SeedUserAsync(bool isGuest, DateTime? claimedAt, string? email = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = email,
            DisplayName = $"Player-{Guid.NewGuid():N}",
            EmailConfirmed = email is not null,
            IsGuest = isGuest,
            ClaimedAt = claimedAt,
            CreatedAt = DateTime.UtcNow,
            LastActiveAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user.Id;
    }

    private HttpClient CreateAdminClient() => CreateAuthenticatedClient(AdminAuthProviderUserId);

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // Sets process environment variables for the duration of one test,
    // restoring each to its original value (including "unset") on dispose —
    // same helper as RoundEndpointTests/AdminManagementEndpointTests,
    // duplicated here rather than shared since those files must not be
    // modified for this story.
    private static IDisposable TemporaryEnvironmentVariables(params (string Name, string Value)[] variables)
    {
        var originalValues = variables.Select(v => (v.Name, Original: Environment.GetEnvironmentVariable(v.Name))).ToList();
        foreach (var (name, value) in variables)
            Environment.SetEnvironmentVariable(name, value);

        return new RestoreEnvironmentVariables(originalValues);
    }

    private sealed class RestoreEnvironmentVariables(List<(string Name, string? Original)> originalValues) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (name, original) in originalValues)
                Environment.SetEnvironmentVariable(name, original);
        }
    }

    // Program.cs reads several required config values (connection string,
    // Supabase settings) eagerly, before WebApplicationFactory's
    // ConfigureAppConfiguration/UseEnvironment hooks can take effect — see
    // RoundEndpointTests' own equivalent for the full explanation. Real
    // process environment variables are the only override visible early
    // enough to genuinely flip which environment this host starts under.
    private IDisposable EnterProductionEnvironment() =>
        TemporaryEnvironmentVariables(
            ("ASPNETCORE_ENVIRONMENT", "Production"),
            ("ConnectionStrings__Database", "Host=localhost;Database=unused-in-tests;Username=postgres;Password=postgres"),
            ("Supabase__Url", "http://localhost:54321"),
            ("Supabase__AnonKey", "test-placeholder-anon-key"),
            ("Supabase__ServiceRoleKey", "test-placeholder-service-role-key"));

    // ---- REQ-507: GET /admin/accounts/metrics --------------------------

    [Test]
    public async Task REQ507_Metrics_Get_ReturnsCorrectTotalCurrentGuestAndClaimedGuestCounts_ForAnAdmin()
    {
        await SeedUserAsync(isGuest: true, claimedAt: null);
        await SeedUserAsync(isGuest: true, claimedAt: null);
        await SeedUserAsync(isGuest: false, claimedAt: DateTime.UtcNow, email: "claimed@example.com");
        await SeedUserAsync(isGuest: false, claimedAt: null, email: "regular@example.com");
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/accounts/metrics");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AdminAccountMetricsResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.TotalUserCount, Is.EqualTo(4), "every User row, regardless of IsGuest/ClaimedAt");
        Assert.That(body.CurrentGuestCount, Is.EqualTo(2), "IsGuest = true rows only");
        Assert.That(body.ClaimedGuestCount, Is.EqualTo(1), "ClaimedAt IS NOT NULL rows only");
    }

    [Test]
    public async Task Metrics_Get_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync("/admin/accounts/metrics");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // REQ-507's own "Scope note": unlike REQ-505/506, this view is visible in
    // every environment, gated only by the "Admin" policy, never an
    // environment check. Proven here without a valid Production JWT (none of
    // the real-Supabase-JWKS machinery is exercised by this suite) by
    // asserting an unauthenticated request gets a 401 challenge from a route
    // that *is* mapped — not the 404 a genuinely-absent route (as
    // REQ-505/506's endpoints are in Production) would produce. See
    // AdminEndpointTests' ReturnsUnauthorized_WithoutBearerToken for the same
    // "no route match" vs. "route matched, auth required" distinction this
    // relies on.
    [Test]
    public async Task REQ507_Metrics_Get_RemainsRegistered_WhenEnvironmentIsProduction()
    {
        using var _ = EnterProductionEnvironment();

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.GetAsync("/admin/accounts/metrics");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "the route must be mapped (triggering the normal auth challenge), not absent (which would 404)");
    }

    // ---- REQ-508: GET /admin/accounts/guests/count -----------------------

    [Test]
    public async Task REQ508_GuestsCount_Get_ReturnsCurrentGuestCount_ForAnAdmin()
    {
        await SeedUserAsync(isGuest: true, claimedAt: null);
        await SeedUserAsync(isGuest: true, claimedAt: null);
        await SeedUserAsync(isGuest: false, claimedAt: DateTime.UtcNow, email: "claimed@example.com");
        await SeedUserAsync(isGuest: false, claimedAt: null, email: "regular@example.com");
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/accounts/guests/count");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<GuestAccountCountResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Count, Is.EqualTo(2), "the exact dry-run count of IsGuest = true rows before anything is deleted");
    }

    [Test]
    public async Task GuestsCount_Get_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync("/admin/accounts/guests/count");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // REQ-508's own "Given ASPNETCORE_ENVIRONMENT == Production" criterion —
    // same "not absent" proof as REQ-507's metrics test above.
    [Test]
    public async Task REQ508_GuestsCount_Get_RemainsRegistered_WhenEnvironmentIsProduction()
    {
        using var _ = EnterProductionEnvironment();

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.GetAsync("/admin/accounts/guests/count");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "the route must be mapped (triggering the normal auth challenge), not absent (which would 404)");
    }

    // ---- REQ-508: POST /admin/accounts/guests/clear ----------------------

    [Test]
    public async Task REQ508_GuestsClear_Post_DeletesOnlyGuestAccountsAndReportsSucceededPerGuest_ForAnAdmin()
    {
        var guestOneId = await SeedUserAsync(isGuest: true, claimedAt: null);
        var guestTwoId = await SeedUserAsync(isGuest: true, claimedAt: null);
        var claimedId = await SeedUserAsync(isGuest: false, claimedAt: DateTime.UtcNow, email: "claimed@example.com");
        var regularId = await SeedUserAsync(isGuest: false, claimedAt: null, email: "regular@example.com");
        var client = CreateAdminClient();

        var response = await client.PostAsync("/admin/accounts/guests/clear", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ClearGuestAccountsResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Results, Has.Count.EqualTo(2), "only the two current guests are selected for clearing");
        Assert.That(body.Results.Select(r => r.UserId), Is.EquivalentTo(new[] { guestOneId, guestTwoId }));
        Assert.That(body.Results, Has.All.Matches<GuestAccountClearResult>(r => r.Outcome == "Succeeded" && r.ErrorMessage == null));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var remainingIds = (await dbContext.Users.AsNoTracking().Select(u => u.Id).ToListAsync()).ToHashSet();
        Assert.That(remainingIds, Does.Not.Contain(guestOneId));
        Assert.That(remainingIds, Does.Not.Contain(guestTwoId));
        Assert.That(remainingIds, Does.Contain(claimedId), "a claimed account must never be force-cleared");
        Assert.That(remainingIds, Does.Contain(regularId), "an account that was never a guest must never be force-cleared");
    }

    [Test]
    public async Task REQ508_GuestsClear_Post_ReturnsEmptyResults_WhenNoGuestsExist()
    {
        await SeedUserAsync(isGuest: false, claimedAt: null, email: "regular@example.com");
        var client = CreateAdminClient();

        var response = await client.PostAsync("/admin/accounts/guests/clear", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ClearGuestAccountsResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Results, Is.Empty);
    }

    [Test]
    public async Task GuestsClear_Post_ReturnsForbidden_ForAuthenticatedNonAdminUser_AndDeletesNothing()
    {
        await SeedUserAsync(isGuest: true, claimedAt: null);
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.PostAsync("/admin/accounts/guests/clear", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var remainingGuestCount = await dbContext.Users.AsNoTracking().CountAsync(u => u.IsGuest);
        Assert.That(remainingGuestCount, Is.EqualTo(1), "a forbidden request must not delete anything");
    }

    // REQ-508's own "Given ASPNETCORE_ENVIRONMENT == Production" criterion —
    // same "not absent" proof as the two GET endpoints above. No guests are
    // seeded for this test: an unauthenticated request never reaches the
    // handler at all (it's challenged for credentials first), so this proves
    // the route is mapped without ever attempting a real Supabase Admin API
    // call against the placeholder URL EnterProductionEnvironment sets.
    [Test]
    public async Task REQ508_GuestsClear_Post_RemainsRegistered_WhenEnvironmentIsProduction()
    {
        using var _ = EnterProductionEnvironment();

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.PostAsync("/admin/accounts/guests/clear", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "the route must be mapped (triggering the normal auth challenge), not absent (which would 404)");
    }

    // REQ-508: "the action reports a per-account outcome (succeeded / not
    // found / failed)" — the Failed branch, exercised by swapping in a fake
    // ISupabaseAuthClient that fails IAccountDeletionService.DeleteUserAsync
    // for exactly one of two guests, succeeding for the other, so both
    // outcomes are asserted from a single request.
    [Test]
    public async Task REQ508_GuestsClear_Post_ReportsFailedOutcome_WhenSupabaseDeleteFailsForOneGuest()
    {
        // The DbContext override's in-memory database name is generated
        // fresh (a new Guid) every time the accumulated ConfigureServices
        // delegate chain actually runs to build a host — so a factory
        // derived via WithWebHostBuilder builds its own, separate host (and
        // separate in-memory database) the first time anything forces it to
        // start, entirely independent of whatever host/database `_factory`
        // itself may already have built. Seeding must therefore go through
        // this same derivedFactory's own Services, never `_factory`'s —
        // otherwise the seeded rows would silently land in a database this
        // request never sees.
        var failingGuestAuthProviderUserId = Guid.NewGuid();

        var derivedFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<ISupabaseAuthClient>(
                    _ => new SelectivelyFailingSupabaseAuthClient(failingGuestAuthProviderUserId)));
            });
        });

        Guid failingGuestId;
        Guid succeedingGuestId;
        using (var seedScope = derivedFactory.Services.CreateScope())
        {
            var seedDbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var failingGuest = new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = failingGuestAuthProviderUserId,
                Email = null,
                DisplayName = $"Guest-{Guid.NewGuid():N}",
                EmailConfirmed = false,
                IsGuest = true,
                CreatedAt = DateTime.UtcNow,
                LastActiveAt = DateTime.UtcNow,
            };
            var succeedingGuest = new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = Guid.NewGuid(),
                Email = null,
                DisplayName = $"Guest-{Guid.NewGuid():N}",
                EmailConfirmed = false,
                IsGuest = true,
                CreatedAt = DateTime.UtcNow,
                LastActiveAt = DateTime.UtcNow,
            };
            seedDbContext.Users.AddRange(failingGuest, succeedingGuest);
            await seedDbContext.SaveChangesAsync();
            failingGuestId = failingGuest.Id;
            succeedingGuestId = succeedingGuest.Id;
        }

        var client = derivedFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(AdminAuthProviderUserId));

        var response = await client.PostAsync("/admin/accounts/guests/clear", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ClearGuestAccountsResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Results, Has.Count.EqualTo(2));

        var failedResult = body.Results.Single(r => r.UserId == failingGuestId);
        Assert.That(failedResult.Outcome, Is.EqualTo("Failed"));
        Assert.That(failedResult.ErrorMessage, Is.Not.Null.And.Not.Empty);
        // Distinguishes this from the "NotFound" branch below — the account
        // was found locally (and its local data removed), only the Supabase
        // credential delete failed.
        Assert.That(failedResult.ErrorMessage, Is.Not.EqualTo(AccountDeletionService.UserNotFoundErrorMessage));

        var succeededResult = body.Results.Single(r => r.UserId == succeedingGuestId);
        Assert.That(succeededResult.Outcome, Is.EqualTo("Succeeded"));
        Assert.That(succeededResult.ErrorMessage, Is.Null);
    }

    // REQ-508: the "NotFound" branch — its own acceptance criteria explains
    // the source of this race ("may differ slightly from the count shown if
    // a guest account was created or claimed in between... not required to
    // re-verify the count is unchanged before executing"). Simulated here by
    // a decorator IUserRepository that removes one just-selected guest (via
    // the same repository's own DeleteAsync, not a raw table write) in the
    // gap between GetAllGuestIdsAsync's selection and the endpoint's
    // per-account IAccountDeletionService.DeleteAccountAsync call for that id.
    [Test]
    public async Task REQ508_GuestsClear_Post_ReportsNotFoundOutcome_WhenAGuestVanishesBetweenSelectionAndProcessing()
    {
        // Same "seed through the factory that will actually serve the
        // request" reasoning as the Failed-outcome test above — a factory
        // derived via WithWebHostBuilder builds its own separate in-memory
        // database the first time it's forced to start, so seeding must go
        // through raceFactory's own Services, never `_factory`'s.
        var vanishingGuestId = Guid.NewGuid();
        var survivingGuestId = Guid.NewGuid();

        var raceFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Scoped<IUserRepository>(sp =>
                    new RaceConditionUserRepository(new UserRepository(sp.GetRequiredService<XGArcadeDbContext>()), vanishingGuestId)));
            });
        });

        using (var seedScope = raceFactory.Services.CreateScope())
        {
            var seedDbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            seedDbContext.Users.AddRange(
                new User
                {
                    Id = vanishingGuestId,
                    AuthProviderUserId = Guid.NewGuid(),
                    Email = null,
                    DisplayName = $"Guest-{Guid.NewGuid():N}",
                    EmailConfirmed = false,
                    IsGuest = true,
                    CreatedAt = DateTime.UtcNow,
                    LastActiveAt = DateTime.UtcNow,
                },
                new User
                {
                    Id = survivingGuestId,
                    AuthProviderUserId = Guid.NewGuid(),
                    Email = null,
                    DisplayName = $"Guest-{Guid.NewGuid():N}",
                    EmailConfirmed = false,
                    IsGuest = true,
                    CreatedAt = DateTime.UtcNow,
                    LastActiveAt = DateTime.UtcNow,
                });
            await seedDbContext.SaveChangesAsync();
        }

        var client = raceFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(AdminAuthProviderUserId));

        var response = await client.PostAsync("/admin/accounts/guests/clear", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ClearGuestAccountsResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Results, Has.Count.EqualTo(2), "both ids selected by GetAllGuestIdsAsync are still reported, even the vanished one");

        var vanishedResult = body.Results.Single(r => r.UserId == vanishingGuestId);
        Assert.That(vanishedResult.Outcome, Is.EqualTo("NotFound"));
        Assert.That(vanishedResult.ErrorMessage, Is.EqualTo(AccountDeletionService.UserNotFoundErrorMessage));

        var survivedResult = body.Results.Single(r => r.UserId == survivingGuestId);
        Assert.That(survivedResult.Outcome, Is.EqualTo("Succeeded"));

        using var assertScope = raceFactory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var remainingIds = (await assertDbContext.Users.AsNoTracking().Select(u => u.Id).ToListAsync()).ToHashSet();
        Assert.That(remainingIds, Does.Not.Contain(vanishingGuestId));
        Assert.That(remainingIds, Does.Not.Contain(survivingGuestId));
    }

    // Test double for ISupabaseAuthClient — fails DeleteUserAsync for exactly
    // one targeted identity, succeeds for everything else, so a single bulk
    // clear request can exercise both the "Succeeded" and "Failed" branches
    // of AdminAccountsEndpoints' outcome mapping. Every other member is a
    // harmless no-op stub: POST /admin/accounts/guests/clear (via
    // IAccountDeletionService.DeleteAccountAsync) never calls them.
    private class SelectivelyFailingSupabaseAuthClient(Guid authProviderUserIdToFail) : ISupabaseAuthClient
    {
        public Task<SupabaseAuthResult> SignUpAsync(string email, string password, string captchaToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() });

        public Task<SupabaseAuthResult> SignInWithPasswordAsync(string email, string password, string captchaToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() });

        public Task<SupabaseAuthResult> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() });

        public Task<bool> DeleteUserAsync(Guid authProviderUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(authProviderUserId != authProviderUserIdToFail);

        public Task<SupabaseAuthResult> SignInAnonymouslyAsync(string captchaToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() });

        public Task<SupabaseAuthResult> LinkEmailPasswordAsync(string accessToken, string email, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() });
    }

    // Decorator over the real UserRepository used only by the "NotFound"
    // race-condition test above — every member delegates unchanged except
    // GetAllGuestIdsAsync, which removes one of its own just-selected ids
    // (via the wrapped repository's DeleteAsync) before returning, simulating
    // a guest deleted by some other process in the gap REQ-508's own
    // acceptance criteria explicitly acknowledges.
    private class RaceConditionUserRepository(IUserRepository inner, Guid userIdToVanishBeforeProcessing) : IUserRepository
    {
        public Task<User?> GetByAuthProviderUserIdAsync(Guid authProviderUserId, CancellationToken cancellationToken = default) =>
            inner.GetByAuthProviderUserIdAsync(authProviderUserId, cancellationToken);

        public Task<bool> DisplayNameExistsAsync(string displayName, Guid? excludeUserId = null, CancellationToken cancellationToken = default) =>
            inner.DisplayNameExistsAsync(displayName, excludeUserId, cancellationToken);

        public Task<User> AddAsync(User user, CancellationToken cancellationToken = default) =>
            inner.AddAsync(user, cancellationToken);

        public Task<User?> UpdateDisplayNameAsync(Guid id, string newDisplayName, CancellationToken cancellationToken = default) =>
            inner.UpdateDisplayNameAsync(id, newDisplayName, cancellationToken);

        public Task<User?> ClaimGuestAsync(Guid id, string email, CancellationToken cancellationToken = default) =>
            inner.ClaimGuestAsync(id, email, cancellationToken);

        public Task<User?> UpdateLastActiveAtAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.UpdateLastActiveAtAsync(id, cancellationToken);

        public Task<IReadOnlyList<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            inner.GetByIdsAsync(ids, cancellationToken);

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            inner.GetByEmailAsync(email, cancellationToken);

        public Task<IReadOnlyList<User>> GetUnclaimedGuestsOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default) =>
            inner.GetUnclaimedGuestsOlderThanAsync(cutoff, cancellationToken);

        public Task<IReadOnlyList<User>> GetInactiveGuestsOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default) =>
            inner.GetInactiveGuestsOlderThanAsync(cutoff, cancellationToken);

        public Task<int> CountUsersAsync(CancellationToken cancellationToken = default) =>
            inner.CountUsersAsync(cancellationToken);

        public Task<int> CountGuestsAsync(CancellationToken cancellationToken = default) =>
            inner.CountGuestsAsync(cancellationToken);

        public Task<int> CountClaimedGuestsAsync(CancellationToken cancellationToken = default) =>
            inner.CountClaimedGuestsAsync(cancellationToken);

        public async Task<IReadOnlyList<Guid>> GetAllGuestIdsAsync(CancellationToken cancellationToken = default)
        {
            var ids = await inner.GetAllGuestIdsAsync(cancellationToken);
            await inner.DeleteAsync(userIdToVanishBeforeProcessing, cancellationToken);
            return ids;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(id, cancellationToken);
    }
}

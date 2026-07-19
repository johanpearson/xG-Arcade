using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Auth;
using XGArcade.Core.Auth;
using XGArcade.Data;
using XGArcade.Data.Entities;

namespace XGArcade.Api.Tests;

// S-004 (docs/backlog.md): API-level tests for auth/signup, auth/login, and
// the auth/me protected endpoint, scoped to REQ-701's checkbox clause only
// (REQ-702 through REQ-705 — confirmation flow — and REQ-606 rate limiting
// are deferred per MVP-SCOPE.md; not covered here). Never hits a real
// Postgres database or a real Supabase project: the DbContext is swapped for
// an in-memory provider and ISupabaseAuthClient is swapped for a fake test
// double, both via WithWebHostBuilder, same pattern as
// XGArcade.Data.Tests/PlayerStoreRepositoryTests.cs uses for the in-memory
// DB half.
public class AuthEndpointTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;
    private FakeSupabaseAuthClient _fakeAuthClient = null!;

    [SetUp]
    public void SetUp()
    {
        _fakeAuthClient = new FakeSupabaseAuthClient();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Program.cs's real-Supabase JWT validation branch now
                // fetches a live JWKS document (ADR-0017) — unit/API tests
                // must never depend on live network (docs/coding-
                // guidelines.md), so this test host uses the same in-process
                // HS256 signer/validator ci.yml's local E2E stack uses
                // instead. The test host's environment is already
                // Development by default, satisfying local-e2e's other
                // gating condition for free.
                builder.UseSetting("Auth:Mode", "local-e2e");

                builder.ConfigureServices(services =>
                {
                    // Swap the real Npgsql-backed XGArcadeDbContext for a
                    // fresh in-memory one, unique per test. Removing only
                    // DbContextOptions<XGArcadeDbContext>/XGArcadeDbContext
                    // isn't enough: AddDbContext also registers the
                    // configuration action itself as an internal enumerable
                    // service closed over XGArcadeDbContext, so that multiple
                    // AddDbContext calls for the same context compose — left
                    // in place, Program.cs's original UseNpgsql(...) action
                    // would still run alongside ours, and EF Core rejects two
                    // providers configured on one DbContextOptions ("Only a
                    // single database provider can be registered"). So sweep
                    // out every service descriptor closed over
                    // XGArcadeDbContext rather than naming that internal type
                    // directly (it isn't a stable public API to reference).
                    var xgArcadeDbContextDescriptors = services
                        .Where(d => d.ServiceType == typeof(XGArcadeDbContext)
                            || (d.ServiceType.IsGenericType && d.ServiceType.GetGenericArguments().Contains(typeof(XGArcadeDbContext))))
                        .ToList();
                    foreach (var descriptor in xgArcadeDbContextDescriptors)
                    {
                        services.Remove(descriptor);
                    }

                    // Captured once, outside the lambda: AddDbContext invokes
                    // this configure action fresh for every scope, so calling
                    // Guid.NewGuid() inside it would give each scope (the
                    // request's own scope vs. a test's follow-up
                    // CreateScope()) a different in-memory database — they'd
                    // never see each other's writes.
                    var inMemoryDatabaseName = Guid.NewGuid().ToString();
                    services.AddDbContext<XGArcadeDbContext>(options =>
                        options.UseInMemoryDatabase(inMemoryDatabaseName));

                    // Swap whichever ISupabaseAuthClient Program.cs registered
                    // (LocalE2EAuthClient, per Auth:Mode=local-e2e set above)
                    // for a controllable fake — this test suite wants to
                    // dictate Supabase's response per-test (e.g. "signup
                    // rejected"), not exercise the real local-e2e stand-in.
                    services.RemoveAll<ISupabaseAuthClient>();
                    services.AddSingleton<ISupabaseAuthClient>(_fakeAuthClient);
                });
            });
    }

    [TearDown]
    public void TearDown()
    {
        _factory.Dispose();
    }

    [Test]
    public async Task REQ701_Signup_BlockedWithoutAgeConfirmedCheckbox()
    {
        var client = _factory.CreateClient();
        var request = new SignupRequest("unconfirmed-age@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: false);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        // The important assertion: the checkbox is enforced before any
        // identity is created anywhere, not just before a User row is saved.
        Assert.That(_fakeAuthClient.SignUpCalled, Is.False);
    }

    [Test]
    public async Task REQ701_Signup_BlockedWithMismatchedConfirmPassword()
    {
        var client = _factory.CreateClient();
        var request = new SignupRequest("mismatched-password@example.com", "a-reasonable-password", "a-different-password", "Test Player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        // The important assertion: the mismatch is enforced before any
        // identity is created anywhere, not just before a User row is saved.
        Assert.That(_fakeAuthClient.SignUpCalled, Is.False);
    }

    [Test]
    public async Task REQ701_Signup_SucceedsWithAgeConfirmedCheckbox()
    {
        var client = _factory.CreateClient();
        var request = new SignupRequest("confirmed-age@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(_fakeAuthClient.SignUpCalled, Is.True);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = await dbContext.Users.SingleOrDefaultAsync(u => u.Email == "confirmed-age@example.com");
        Assert.That(user, Is.Not.Null);
        Assert.That(user!.DisplayName, Is.EqualTo("Test Player"));
    }

    // S-017: case-insensitive DisplayName uniqueness (REQ-701/REQ-401).
    // These exercise AuthController.Signup's IUserRepository
    // .DisplayNameExistsAsync pre-check only — the in-memory EF Core
    // provider this test host uses (see SetUp) does not enforce the real
    // unique index (IX_Users_NormalizedDisplayName), so the DbUpdateException
    // /DisplayNameAlreadyInUseException race-condition catch in
    // UserRepository.AddAsync can't be exercised at this level. That path is
    // covered only by the manual verification against a real local Postgres
    // instance noted in docs/backlog.md's S-017 entry — see also
    // XGArcade.Data.Tests/UserRepositoryTests.cs for why it can't be
    // meaningfully unit-tested with InMemory either.
    [Test]
    public async Task REQ701_Signup_BlockedWhenDisplayNameExactlyMatchesExistingUser()
    {
        using (var seedScope = _factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = Guid.NewGuid(),
                Email = "existing-name-owner@example.com",
                DisplayName = "Test Player",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var request = new SignupRequest("new-signup-same-name@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        // The important assertion: the display name collision is caught
        // before any identity is created anywhere, not just before a User
        // row is saved.
        Assert.That(_fakeAuthClient.SignUpCalled, Is.False);
    }

    [Test]
    public async Task REQ701_Signup_BlockedWhenDisplayNameMatchesExistingUserOnlyInCasing()
    {
        using (var seedScope = _factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = Guid.NewGuid(),
                Email = "existing-name-owner-2@example.com",
                DisplayName = "Test Player",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var request = new SignupRequest("new-signup-different-casing@example.com", "a-reasonable-password", "a-reasonable-password", "TEST PLAYER", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(_fakeAuthClient.SignUpCalled, Is.False);
    }

    // REQ-701's uniqueness check runs against the already-trimmed
    // DisplayName (see Signup's existing `request.DisplayName.Trim()`), so a
    // leading/trailing-whitespace variant of an existing name must still be
    // caught as a collision — not treated as a distinct name.
    [Test]
    public async Task REQ701_Signup_BlockedWhenDisplayNameMatchesExistingUserAfterTrimming()
    {
        using (var seedScope = _factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = Guid.NewGuid(),
                Email = "existing-name-owner-4@example.com",
                DisplayName = "Test Player",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var request = new SignupRequest("new-signup-untrimmed@example.com", "a-reasonable-password", "a-reasonable-password", "  Test Player  ", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(_fakeAuthClient.SignUpCalled, Is.False);
    }

    [Test]
    public async Task REQ701_Signup_LeavesExistingUsersDisplayNameUnchanged_AfterFailedCollidingSignup()
    {
        var existingUserId = Guid.NewGuid();
        using (var seedScope = _factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = existingUserId,
                AuthProviderUserId = Guid.NewGuid(),
                Email = "existing-name-owner-3@example.com",
                DisplayName = "Test Player",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var request = new SignupRequest("new-signup-should-fail@example.com", "a-reasonable-password", "a-reasonable-password", "test player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var existingUser = await assertDbContext.Users.AsNoTracking().SingleAsync(u => u.Id == existingUserId);
        Assert.That(existingUser.DisplayName, Is.EqualTo("Test Player"));
        // No second row was created for the rejected signup's email either.
        var rejectedSignupUser = await assertDbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == "new-signup-should-fail@example.com");
        Assert.That(rejectedSignupUser, Is.Null);
    }

    // Not REQ-701-named: this exercises Supabase's own rejection (e.g.
    // duplicate email) surfacing as a client error, not the checkbox clause
    // REQ-701 is narrowed to for this story (docs/backlog.md S-004).
    [Test]
    public async Task Signup_Post_ReturnsBadRequest_WhenSupabaseAuthRejectsSignup()
    {
        _fakeAuthClient.SignUpResult = (_, _) => new SupabaseAuthResult { Success = false, ErrorMessage = "User already registered" };
        var client = _factory.CreateClient();
        var request = new SignupRequest("already-registered@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Login_Post_ReturnsAccessToken_ForValidCredentials()
    {
        _fakeAuthClient.SignInResult = (_, _) => new SupabaseAuthResult
        {
            Success = true,
            AuthProviderUserId = Guid.NewGuid(),
            AccessToken = "a-fake-access-token",
            RefreshToken = "a-fake-refresh-token",
        };
        var client = _factory.CreateClient();
        var request = new LoginRequest("known-user@example.com", "a-reasonable-password");

        var response = await client.PostAsJsonAsync("/auth/login", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.AccessToken, Is.EqualTo("a-fake-access-token"));
        Assert.That(body.RefreshToken, Is.EqualTo("a-fake-refresh-token"));
    }

    [Test]
    public async Task Login_Post_ReturnsUnauthorized_WhenSupabaseAuthRejectsCredentials()
    {
        _fakeAuthClient.SignInResult = (_, _) => new SupabaseAuthResult { Success = false, ErrorMessage = "Invalid login credentials" };
        var client = _factory.CreateClient();
        var request = new LoginRequest("known-user@example.com", "the-wrong-password");

        var response = await client.PostAsJsonAsync("/auth/login", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ProtectedEndpoint_Get_RejectsAnonymousCalls()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/auth/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ProtectedEndpoint_Get_ReturnsProfile_ForAuthenticatedKnownUser()
    {
        var authProviderUserId = Guid.NewGuid();

        using (var seedScope = _factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = authProviderUserId,
                Email = "known-user@example.com",
                DisplayName = "Known User",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.GetAsync("/auth/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Email, Is.EqualTo("known-user@example.com"));
        Assert.That(body.EmailConfirmed, Is.True);
    }

    // REQ-504: GET /auth/me's IsAdmin field — computed via the same
    // AdminAuthorizationHandler.IsAdminUserId helper the "Admin" policy
    // itself uses (Admin:UserIds config), so the frontend can decide whether
    // to render the admin nav entry point. This SetUp's factory doesn't
    // configure Admin:UserIds at all (no other test in this file needs it),
    // so each of these two tests builds its own variant factory via
    // WithWebHostBuilder rather than disturbing SetUp for every other test —
    // same idiom as RoundEndpointTests' throwingFactory/productionFactory.
    [Test]
    public async Task REQ504_Me_Get_ReturnsIsAdminTrue_ForUserInAdminUserIds()
    {
        var authProviderUserId = Guid.NewGuid();
        var adminFactory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Admin:UserIds", authProviderUserId.ToString()));

        using (var seedScope = adminFactory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = authProviderUserId,
                Email = "admin-user@example.com",
                DisplayName = "Admin User",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = adminFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.GetAsync("/auth/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.IsAdmin, Is.True);
    }

    [Test]
    public async Task REQ504_Me_Get_ReturnsIsAdminFalse_ForUserNotInAdminUserIds()
    {
        var authProviderUserId = Guid.NewGuid();
        // A different GUID in Admin:UserIds — proves the false case isn't
        // just "config is empty", but a genuine non-match against a
        // populated admin list.
        var adminFactory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Admin:UserIds", Guid.NewGuid().ToString()));

        using (var seedScope = adminFactory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = authProviderUserId,
                Email = "non-admin-user@example.com",
                DisplayName = "Non-Admin User",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = adminFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.GetAsync("/auth/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.IsAdmin, Is.False);
    }

    // REQ-710: self-service account deletion (S-025). Seeds a user directly
    // into the in-memory DB (same pattern as ProtectedEndpoint_Get tests
    // above) and mints its own JWT via LocalE2EAuth.MintToken for the
    // [Authorize]-protected DELETE /auth/account endpoint.
    private async Task<User> SeedDeletableUserAsync(Guid authProviderUserId, string email = "deletable-user@example.com", string? displayName = null)
    {
        using var seedScope = _factory.Services.CreateScope();
        var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = authProviderUserId,
            Email = email,
            DisplayName = displayName ?? $"Player-{Guid.NewGuid():N}",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    private async Task SeedGuessAsync(Guid userId)
    {
        using var seedScope = _factory.Services.CreateScope();
        var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        dbContext.Guesses.Add(new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = Guid.NewGuid(),
            UserId = userId,
            CellId = Guid.NewGuid(),
            SubmittedName = "Someone",
            IsCorrect = true,
            AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task REQ710_DeleteAccount_CorrectPassword_ReturnsNoContentAndCallsSupabaseDelete()
    {
        var authProviderUserId = Guid.NewGuid();
        var user = await SeedDeletableUserAsync(authProviderUserId);
        // Default FakeSupabaseAuthClient.SignInResult already succeeds — the
        // confirmation step (AuthController.DeleteAccount's re-verification
        // via SignInWithPasswordAsync) passes without any per-test override.

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.SendAsync(BuildDeleteRequest("the-correct-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(_fakeAuthClient.DeleteUserCalledWith, Is.EqualTo(authProviderUserId));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var remainingUser = await assertDbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == user.Id);
        Assert.That(remainingUser, Is.Null);
    }

    [Test]
    public async Task REQ710_DeleteAccount_WrongPassword_Returns401AndDoesNotDeleteAnything()
    {
        var authProviderUserId = Guid.NewGuid();
        var user = await SeedDeletableUserAsync(authProviderUserId);
        _fakeAuthClient.SignInResult = (_, _) => new SupabaseAuthResult { Success = false, ErrorMessage = "Invalid login credentials" };

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.SendAsync(BuildDeleteRequest("the-wrong-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        // The important assertion: nothing was touched — deletion never even
        // reached IAccountDeletionService, let alone Supabase.
        Assert.That(_fakeAuthClient.DeleteUserCalledWith, Is.Null);

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var remainingUser = await assertDbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == user.Id);
        Assert.That(remainingUser, Is.Not.Null);
    }

    [Test]
    public async Task REQ710_DeleteAccount_SupabaseDeleteFails_Returns500AndLocalDataIsAlreadyGone()
    {
        var authProviderUserId = Guid.NewGuid();
        var user = await SeedDeletableUserAsync(authProviderUserId);
        _fakeAuthClient.DeleteUserResult = _ => false;

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.SendAsync(BuildDeleteRequest("the-correct-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        // Documented, deliberate ordering (AccountDeletionService's own doc
        // comment) — the local writes already committed before the Supabase
        // call ran, so the local User row is gone even though the overall
        // request reports failure.
        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var remainingUser = await assertDbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == user.Id);
        Assert.That(remainingUser, Is.Null);
    }

    [Test]
    public async Task REQ710_DeleteAccount_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(BuildDeleteRequest("irrelevant-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(_fakeAuthClient.DeleteUserCalledWith, Is.Null);
    }

    [Test]
    public async Task REQ710_DeleteAccount_AnonymizesCallersGuessesButNotOtherUsersGuesses()
    {
        var deletedUserAuthProviderId = Guid.NewGuid();
        var deletedUser = await SeedDeletableUserAsync(deletedUserAuthProviderId, email: "deleted-user@example.com");
        var otherUser = await SeedDeletableUserAsync(Guid.NewGuid(), email: "other-user@example.com");
        await SeedGuessAsync(deletedUser.Id);
        await SeedGuessAsync(otherUser.Id);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(deletedUserAuthProviderId));

        var response = await client.SendAsync(BuildDeleteRequest("the-correct-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var guesses = await assertDbContext.Guesses.AsNoTracking().ToListAsync();
        Assert.That(guesses, Has.Count.EqualTo(2));
        // The other user's guess is untouched — still linked to them, not
        // anonymized as a side effect of someone else's deletion.
        Assert.That(guesses.Count(g => g.UserId == otherUser.Id), Is.EqualTo(1));
        Assert.That(guesses.Count(g => g.UserId == null), Is.EqualTo(1));
        Assert.That(guesses.Count(g => g.UserId == deletedUser.Id), Is.EqualTo(0));
    }

    private static HttpRequestMessage BuildDeleteRequest(string password) =>
        new(HttpMethod.Delete, "/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest(password)),
        };

    // Test double for ISupabaseAuthClient (COMP-01's only path to the auth
    // provider — see ADR-0013): never makes a real HTTP call, and exposes
    // SignUpCalled so REQ701's checkbox test can assert Supabase is never
    // contacted when the checkbox is unchecked.
    private class FakeSupabaseAuthClient : ISupabaseAuthClient
    {
        public bool SignUpCalled { get; private set; }

        public Func<string, string, SupabaseAuthResult> SignUpResult { get; set; } =
            (_, _) => new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() };

        public Func<string, string, SupabaseAuthResult> SignInResult { get; set; } =
            (_, _) => new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid(), AccessToken = "fake-access-token" };

        public Task<SupabaseAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            SignUpCalled = true;
            return Task.FromResult(SignUpResult(email, password));
        }

        public Task<SupabaseAuthResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(SignInResult(email, password));

        public Guid? DeleteUserCalledWith { get; private set; }

        public Func<Guid, bool> DeleteUserResult { get; set; } = _ => true;

        public Task<bool> DeleteUserAsync(Guid authProviderUserId, CancellationToken cancellationToken = default)
        {
            DeleteUserCalledWith = authProviderUserId;
            return Task.FromResult(DeleteUserResult(authProviderUserId));
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
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

                    // Swap the real HTTP-calling Supabase client (registered
                    // via AddHttpClient in Program.cs) for a controllable fake.
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
        var request = new SignupRequest("unconfirmed-age@example.com", "a-reasonable-password", AgeConfirmed: false);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        // The important assertion: the checkbox is enforced before any
        // identity is created anywhere, not just before a User row is saved.
        Assert.That(_fakeAuthClient.SignUpCalled, Is.False);
    }

    [Test]
    public async Task REQ701_Signup_SucceedsWithAgeConfirmedCheckbox()
    {
        var client = _factory.CreateClient();
        var request = new SignupRequest("confirmed-age@example.com", "a-reasonable-password", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(_fakeAuthClient.SignUpCalled, Is.True);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = await dbContext.Users.SingleOrDefaultAsync(u => u.Email == "confirmed-age@example.com");
        Assert.That(user, Is.Not.Null);
    }

    // Not REQ-701-named: this exercises Supabase's own rejection (e.g.
    // duplicate email) surfacing as a client error, not the checkbox clause
    // REQ-701 is narrowed to for this story (docs/backlog.md S-004).
    [Test]
    public async Task Signup_Post_ReturnsBadRequest_WhenSupabaseAuthRejectsSignup()
    {
        _fakeAuthClient.SignUpResult = (_, _) => new SupabaseAuthResult { Success = false, ErrorMessage = "User already registered" };
        var client = _factory.CreateClient();
        var request = new SignupRequest("already-registered@example.com", "a-reasonable-password", AgeConfirmed: true);

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
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintValidJwt(authProviderUserId));

        var response = await client.GetAsync("/auth/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Email, Is.EqualTo("known-user@example.com"));
        Assert.That(body.EmailConfirmed, Is.True);
    }

    // Mints a JWT signed with the same secret/issuer/audience the test host's
    // JwtBearer middleware validates against (Program.cs's non-local-e2e
    // branch): issuer "{Supabase:Url}/auth/v1", audience "authenticated",
    // HS256 with Auth:SupabaseJwtSecret. Read from the running host's own
    // configuration rather than hardcoded here, so it can't silently drift
    // from appsettings.Development.json.
    private string MintValidJwt(Guid authProviderUserId)
    {
        var configuration = _factory.Services.GetRequiredService<IConfiguration>();
        var supabaseUrl = configuration["Supabase:Url"]
            ?? throw new InvalidOperationException("Supabase:Url is not configured for the test host.");
        var jwtSecret = configuration["Auth:SupabaseJwtSecret"]
            ?? throw new InvalidOperationException("Auth:SupabaseJwtSecret is not configured for the test host.");

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"{supabaseUrl.TrimEnd('/')}/auth/v1",
            Audience = "authenticated",
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = authProviderUserId.ToString(),
                ["role"] = "authenticated",
            },
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)), SecurityAlgorithms.HmacSha256),
        });
    }

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
    }
}

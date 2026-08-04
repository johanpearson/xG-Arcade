using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Admin;
using XGArcade.Api.Auth;
using XGArcade.Data;
using XGArcade.Data.Entities;

namespace XGArcade.Api.Tests;

// REQ-1209/ADR-0058 (docs/requirements-document.md §4.12): API-level
// coverage for GET /admin/xg-path/cycle. Same "Admin" authorization policy
// (Admin:UserIds) and in-process HS256 local-e2e auth setup as
// AdminAccountsEndpointTests — mirrors that file's own conventions
// (WebApplicationFactory, in-memory DbContext swap, LocalE2EAuth.MintToken)
// rather than inventing a different test-host pattern.
public class AdminXGPathEndpointTests
{
    // Distinct from every other admin test file's own constant purely so a
    // future refactor that merges these constants doesn't hide an accidental
    // collision — same reasoning as AdminAccountsEndpointTests' own comment.
    private static readonly Guid AdminAuthProviderUserId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Same in-process HS256 signer/validator as
                // AdminAccountsEndpointTests' own SetUp comment explains
                // (ADR-0017) — Program.cs's real-Supabase JWT validation
                // branch fetches a live JWKS document, which this test host
                // must avoid.
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

    private HttpClient CreateAdminClient() => CreateAuthenticatedClient(AdminAuthProviderUserId);

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // Sets process environment variables for the duration of one test,
    // restoring each to its original value (including "unset") on dispose —
    // same helper duplicated across this project's admin test files (see
    // AdminAccountsEndpointTests' own copy of this exact helper for why it's
    // duplicated rather than shared).
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

    private IDisposable EnterProductionEnvironment() =>
        TemporaryEnvironmentVariables(
            ("ASPNETCORE_ENVIRONMENT", "Production"),
            ("ConnectionStrings__Database", "Host=localhost;Database=unused-in-tests;Username=postgres;Password=postgres"),
            ("Supabase__Url", "http://localhost:54321"),
            ("Supabase__AnonKey", "test-placeholder-anon-key"),
            ("Supabase__ServiceRoleKey", "test-placeholder-service-role-key"));

    // ---- REQ-1209: GET /admin/xg-path/cycle -------------------------------

    [Test]
    public async Task REQ1209_Cycle_Get_ReturnsPersistedCycleState_ForAnAdmin()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.PathTargetCycles.Add(new PathTargetCycle
            {
                Id = Guid.NewGuid(),
                CycleNumber = 3,
                ObservedPoolSize = 42,
                UsedInCycleCount = 17,
                LastCycleCompletedAt = new DateTime(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc),
            });
            await dbContext.SaveChangesAsync();
        }
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/xg-path/cycle");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AdminXGPathCycleResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.HasData, Is.True);
        Assert.That(body.CycleNumber, Is.EqualTo(3));
        Assert.That(body.ObservedPoolSize, Is.EqualTo(42));
        Assert.That(body.UsedInCycleCount, Is.EqualTo(17));
        Assert.That(body.RemainingInCycleCount, Is.EqualTo(25), "derived as ObservedPoolSize - UsedInCycleCount, not a persisted column");
        Assert.That(body.LastCycleCompletedAt, Is.EqualTo(new DateTime(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task REQ1209_Cycle_Get_ReturnsHasDataFalseShape_WhenNoPathTargetCycleRowExistsYet()
    {
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/xg-path/cycle");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "no xG Path round having ever generated is a normal 200, never a 404/error");
        var body = await response.Content.ReadFromJsonAsync<AdminXGPathCycleResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.HasData, Is.False);
        Assert.That(body.CycleNumber, Is.Null);
        Assert.That(body.ObservedPoolSize, Is.Null);
        Assert.That(body.UsedInCycleCount, Is.Null);
        Assert.That(body.RemainingInCycleCount, Is.Null);
        Assert.That(body.LastCycleCompletedAt, Is.Null);
    }

    [Test]
    public async Task Cycle_Get_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync("/admin/xg-path/cycle");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Cycle_Get_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/admin/xg-path/cycle");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // REQ-1209's own doc comment: "registered unconditionally (including
    // Production — this is real operational state, not seeded/test data)" —
    // same "not absent" proof AdminAccountsEndpointTests' own REQ507/508
    // Production tests use: an unauthenticated request against a route that
    // IS mapped gets the normal 401 auth challenge, not the 404 a genuinely-
    // absent route (like REQ-505/506's Production-gated endpoints) would
    // produce.
    [Test]
    public async Task REQ1209_Cycle_Get_RemainsRegistered_WhenEnvironmentIsProduction()
    {
        using var _ = EnterProductionEnvironment();

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.GetAsync("/admin/xg-path/cycle");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "the route must be mapped (triggering the normal auth challenge), not absent (which would 404)");
    }
}

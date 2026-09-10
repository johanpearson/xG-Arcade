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

// REQ-1507/ADR-0112 (docs/requirements-document.md §4.16): API-level
// coverage for GET /admin/xg-higher-lower/international-stats-coverage.
// Same "Admin" authorization policy and in-process HS256 local-e2e auth
// setup as AdminXGPathEndpointTests — mirrors that file's own conventions
// (WebApplicationFactory, in-memory DbContext swap, LocalE2EAuth.MintToken)
// rather than inventing a different test-host pattern.
public class AdminXGHigherLowerEndpointTests
{
    // Distinct from every other admin test file's own constant purely so a
    // future refactor that merges these constants doesn't hide an accidental
    // collision — same reasoning as AdminAccountsEndpointTests'/
    // AdminXGPathEndpointTests' own comment.
    private static readonly Guid AdminAuthProviderUserId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Auth:Mode", "local-e2e");
                builder.UseSetting("Admin:UserIds", AdminAuthProviderUserId.ToString());

                builder.ConfigureServices(services =>
                {
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

    // Same duplicated-per-file helper as AdminXGPathEndpointTests' own copy
    // — see that file's comment for why it's duplicated rather than shared.
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

    private static Player NewPlayer(string fullName) => new() { Id = Guid.NewGuid(), FullName = fullName };

    // ---- REQ-1507: GET /admin/xg-higher-lower/international-stats-coverage ----

    [Test]
    public async Task REQ1507_Coverage_Get_ReturnsCountsAcrossCapsGoalsTrophyAndTheFloor()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

            // Meets the caps floor (default 10) and has goals + trophy too.
            var wellCovered = NewPlayer("Well Covered");
            // Has a recorded caps value, but below the floor.
            var belowFloor = NewPlayer("Below Floor");
            // Has a trophy row only — no caps/goals at all.
            var trophyOnly = NewPlayer("Trophy Only");
            // No PlayerAttribute/PlayerOverride rows of any kind.
            var noData = NewPlayer("No Data");
            dbContext.Players.AddRange(wellCovered, belowFloor, trophyOnly, noData);

            dbContext.PlayerAttributes.AddRange(
                new PlayerAttribute { PlayerId = wellCovered.Id, AttributeType = "international-caps", AttributeValue = "42" },
                new PlayerAttribute { PlayerId = wellCovered.Id, AttributeType = "international-goals", AttributeValue = "7" },
                new PlayerAttribute { PlayerId = wellCovered.Id, AttributeType = "trophy", AttributeValue = "World Cup" },
                new PlayerAttribute { PlayerId = belowFloor.Id, AttributeType = "international-caps", AttributeValue = "3" },
                new PlayerAttribute { PlayerId = trophyOnly.Id, AttributeType = "trophy", AttributeValue = "League Title" });

            await dbContext.SaveChangesAsync();
        }
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/xg-higher-lower/international-stats-coverage");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AdminXGHigherLowerInternationalStatsCoverageResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.TotalPlayerCount, Is.EqualTo(4));
        Assert.That(body.PlayersWithCapsCount, Is.EqualTo(2), "wellCovered + belowFloor");
        Assert.That(body.PlayersWithGoalsCount, Is.EqualTo(1), "wellCovered only");
        Assert.That(body.PlayersWithTrophyCount, Is.EqualTo(2), "wellCovered + trophyOnly");
        Assert.That(body.PlayersMeetingCapsFloorCount, Is.EqualTo(1), "only wellCovered's 42 clears the default floor of 10");
        Assert.That(body.CapsFloorThreshold, Is.EqualTo(10), "HigherLowerGenerationOptions.MinimumInternationalCaps default");
    }

    [Test]
    public async Task REQ1507_Coverage_Get_PlayerOverrideReplacesRawAttributeValue_ForCapsFloorCount()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

            var overridden = NewPlayer("Overridden Player");
            dbContext.Players.Add(overridden);
            // Raw data says 2 caps (below the floor)...
            dbContext.PlayerAttributes.Add(new PlayerAttribute
            {
                PlayerId = overridden.Id, AttributeType = "international-caps", AttributeValue = "2",
            });
            // ...but an admin override replaces the effective value with 50 (above the floor).
            dbContext.PlayerOverrides.Add(new PlayerOverride
            {
                Id = Guid.NewGuid(),
                PlayerId = overridden.Id,
                Field = "international-caps",
                Value = "50",
                Reason = "corrected from a more complete source",
                LockedByAdminId = Guid.NewGuid(),
                LockedAt = DateTime.UtcNow,
            });

            await dbContext.SaveChangesAsync();
        }
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/xg-higher-lower/international-stats-coverage");

        var body = await response.Content.ReadFromJsonAsync<AdminXGHigherLowerInternationalStatsCoverageResponse>();
        Assert.That(body!.PlayersWithCapsCount, Is.EqualTo(1));
        Assert.That(body.PlayersMeetingCapsFloorCount, Is.EqualTo(1),
            "the override's effective value (50), not the raw PlayerAttribute value (2), must decide the floor count");
    }

    [Test]
    public async Task REQ1507_Coverage_Get_EmptyPool_ReturnsAllZeroCounts()
    {
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/xg-higher-lower/international-stats-coverage");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AdminXGHigherLowerInternationalStatsCoverageResponse>();
        Assert.That(body!.TotalPlayerCount, Is.EqualTo(0));
        Assert.That(body.PlayersWithCapsCount, Is.EqualTo(0));
        Assert.That(body.PlayersWithGoalsCount, Is.EqualTo(0));
        Assert.That(body.PlayersWithTrophyCount, Is.EqualTo(0));
        Assert.That(body.PlayersMeetingCapsFloorCount, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ1507_Coverage_Get_NeverTriggersRoundGeneration_NoHigherLowerInstanceIsCreated()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Players.Add(NewPlayer("Solo Player"));
            await dbContext.SaveChangesAsync();
        }
        var client = CreateAdminClient();

        await client.GetAsync("/admin/xg-higher-lower/international-stats-coverage");

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await verifyDbContext.HigherLowerInstances.AsNoTracking().CountAsync(), Is.EqualTo(0),
            "this endpoint is a pure read of already-persisted state — it must never itself trigger Round generation");
    }

    [Test]
    public async Task Coverage_Get_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync("/admin/xg-higher-lower/international-stats-coverage");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Coverage_Get_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/admin/xg-higher-lower/international-stats-coverage");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // Same "not absent" proof as AdminXGPathEndpointTests'
    // REQ1209_Cycle_Get_RemainsRegistered_WhenEnvironmentIsProduction — this
    // endpoint is registered unconditionally, including Production.
    [Test]
    public async Task REQ1507_Coverage_Get_RemainsRegistered_WhenEnvironmentIsProduction()
    {
        using var _ = EnterProductionEnvironment();

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.GetAsync("/admin/xg-higher-lower/international-stats-coverage");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "the route must be mapped (triggering the normal auth challenge), not absent (which would 404)");
    }
}

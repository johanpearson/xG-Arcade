using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Connect;
using XGArcade.Data;

namespace XGArcade.Api.Tests;

// S-218's own E2E accept criterion (docs/backlog.md): API-level coverage for
// POST /internal/test-data/seed-connect-players
// (XGArcade.Api.Connect.InternalConnectTestDataEndpoints) — same
// non-Production-only test-control pattern RoundEndpointTests' own
// seed-guessable-round/seed-guessable-path-round coverage already
// establishes (REQ-806/ADR-0006). This endpoint has no bearer-token gate
// (mirrors seed-guessable-round's own auth-free posture — its only callers
// are Playwright specs, never a scheduled job), so unlike
// InternalConnectForfeitSweepEndpointTests there is no authorization branch
// to cover here.
public class InternalConnectTestDataEndpointTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Same in-memory-DbContext swap as RoundEndpointTests/
                    // InternalConnectForfeitSweepEndpointTests — see either
                    // file's own SetUp comment for why every
                    // XGArcadeDbContext-closed descriptor must be removed,
                    // not just the two obvious ones.
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

    [Test]
    public async Task REQ1404_SeedConnectPlayers_Post_CreatesTwoNonOverlappingTargetsAndASymmetricallyOverlappingConnector()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/internal/test-data/seed-connect-players", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SeedConnectPlayersResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.TargetPlayerAName, Is.Not.Empty);
        Assert.That(body.TargetPlayerBName, Is.Not.Empty);
        Assert.That(body.ConnectorPlayerName, Is.Not.Empty);
        Assert.That(body.ClubOverlappingWithA, Is.Not.Empty);
        Assert.That(body.ClubOverlappingWithB, Is.Not.Empty);
        // Three distinct players, not accidental reuse of one row.
        Assert.That(
            new[] { body.TargetPlayerAName, body.TargetPlayerBName, body.ConnectorPlayerName }.Distinct().Count(),
            Is.EqualTo(3));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        var targetA = await dbContext.Players.SingleAsync(p => p.FullName == body.TargetPlayerAName);
        var targetB = await dbContext.Players.SingleAsync(p => p.FullName == body.TargetPlayerBName);
        var connector = await dbContext.Players.SingleAsync(p => p.FullName == body.ConnectorPlayerName);

        var targetAStints = await dbContext.PlayerCareerStints.Where(s => s.PlayerId == targetA.Id).ToListAsync();
        var targetBStints = await dbContext.PlayerCareerStints.Where(s => s.PlayerId == targetB.Id).ToListAsync();
        var connectorStints = await dbContext.PlayerCareerStints.Where(s => s.PlayerId == connector.Id).ToListAsync();

        // REQ-1404's own precondition: the two targets share no club at all.
        Assert.That(targetAStints.Select(s => s.ClubName), Has.No.Member(body.ClubOverlappingWithB));
        Assert.That(targetBStints.Select(s => s.ClubName), Has.No.Member(body.ClubOverlappingWithA));

        // REQ-1406's own "one connector closes either target's chain"
        // design: the connector has an overlapping-time stint at EACH
        // target's own club.
        var connectorAtClubA = connectorStints.Single(s => s.ClubName == body.ClubOverlappingWithA);
        var targetAAtClubA = targetAStints.Single(s => s.ClubName == body.ClubOverlappingWithA);
        Assert.That(connectorAtClubA.StartYear, Is.LessThanOrEqualTo(targetAAtClubA.EndYear ?? int.MaxValue));
        Assert.That(targetAAtClubA.StartYear, Is.LessThanOrEqualTo(connectorAtClubA.EndYear ?? int.MaxValue));

        var connectorAtClubB = connectorStints.Single(s => s.ClubName == body.ClubOverlappingWithB);
        var targetBAtClubB = targetBStints.Single(s => s.ClubName == body.ClubOverlappingWithB);
        Assert.That(connectorAtClubB.StartYear, Is.LessThanOrEqualTo(targetBAtClubB.EndYear ?? int.MaxValue));
        Assert.That(targetBAtClubB.StartYear, Is.LessThanOrEqualTo(connectorAtClubB.EndYear ?? int.MaxValue));

        // The test-only PlayerNameIndex id-space alignment (see
        // InternalConnectTestDataEndpoints.cs's own top-of-file comment):
        // all three players get an entry, keyed by the SAME id as their real
        // Player.Id. Bug fix (2026-09-05, ADR-0107): the connector now gets
        // one too (previously it didn't, since its candidate field used to
        // resolve by name alone) — ChainBuilder.tsx's candidate field now
        // also requires a real /players/autocomplete suggestion click, same
        // as TargetPickPanel.tsx's target-pick field already did.
        var targetANameIndexEntry = await dbContext.PlayerNameIndexEntries.SingleAsync(e => e.PlayerId == targetA.Id);
        Assert.That(targetANameIndexEntry.PrimaryName, Is.EqualTo(targetA.FullName));
        var targetBNameIndexEntry = await dbContext.PlayerNameIndexEntries.SingleAsync(e => e.PlayerId == targetB.Id);
        Assert.That(targetBNameIndexEntry.PrimaryName, Is.EqualTo(targetB.FullName));
        var connectorNameIndexEntry = await dbContext.PlayerNameIndexEntries.SingleAsync(e => e.PlayerId == connector.Id);
        Assert.That(connectorNameIndexEntry.PrimaryName, Is.EqualTo(connector.FullName));
        Assert.That(connectorNameIndexEntry.WikidataQid, Is.EqualTo(connector.WikidataQid));
    }

    [Test]
    public async Task SeedConnectPlayers_Post_IsNeverRegistered_WhenEnvironmentIsProduction()
    {
        using var _ = TemporaryEnvironmentVariables(
            ("ASPNETCORE_ENVIRONMENT", "Production"),
            ("ConnectionStrings__Database", "Host=localhost;Database=unused-in-tests;Username=postgres;Password=postgres"),
            ("Supabase__Url", "http://localhost:54321"),
            ("Supabase__AnonKey", "test-placeholder-anon-key"),
            ("Supabase__ServiceRoleKey", "test-placeholder-service-role-key"));

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.PostAsync("/internal/test-data/seed-connect-players", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // Same helper/shape as RoundEndpointTests' own private copy — this
    // codebase duplicates it per test file rather than sharing it (see e.g.
    // AdminManagementEndpointTests/AdminAccountsEndpointTests' own copies).
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
}

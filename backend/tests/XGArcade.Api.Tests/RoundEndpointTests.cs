using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Rounds;
using XGArcade.Core.Rounds;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Tests;

// S-008 (docs/backlog.md): API-level coverage for POST /internal/generate-round
// (REQ-301) and POST /internal/test-data/force-close-round/{id} (REQ-806).
public class RoundEndpointTests
{
    private const string ValidJobToken = "test-internal-job-token";

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Internal:JobToken"] = ValidJobToken,
                    });
                });

                builder.ConfigureServices(services =>
                {
                    // Same in-memory-DbContext swap as GridEndpointTests — see
                    // that file's SetUp comment for why every
                    // XGArcadeDbContext-closed descriptor must be removed, not
                    // just the two obvious ones.
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

                    // MinValidAnswers=1 so a single seeded PlayerAttribute pair
                    // is enough — avoids depending on the real Wikidata HTTP
                    // client (same reasoning as GridEndpointTests.SetUp).
                    services.RemoveAll<GridGenerationOptions>();
                    services.AddSingleton(new GridGenerationOptions { MinValidAnswers = 1, MaxAttempts = 50 });

                    // A tiny round duration keeps REQ-301's "one round ahead"
                    // assertions (start-at-previous-round's-end-time) exact
                    // and fast without a special test-only branch in
                    // RoundGenerationService itself.
                    services.RemoveAll<RoundSchedulingOptions>();
                    services.AddSingleton(new RoundSchedulingOptions
                    {
                        GameKey = GridGameModule.XGGridGameKey,
                        RoundDuration = TimeSpan.FromDays(3),
                        GridSize = 3,
                    });
                });
            });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private async Task SeedFullyMatchedReferenceDataAsync(int size)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        var countries = Enumerable.Range(0, size)
            .Select(i => new CountryDefinition { Id = Guid.NewGuid(), Name = $"Country{i}", WikidataQid = $"Qc{i}" })
            .ToList();
        var clubs = Enumerable.Range(0, size)
            .Select(i => new ClubDefinition { Id = Guid.NewGuid(), Name = $"Club{i}", WikidataQid = $"Qk{i}" })
            .ToList();
        dbContext.CountryDefinitions.AddRange(countries);
        dbContext.ClubDefinitions.AddRange(clubs);

        foreach (var country in countries)
        {
            foreach (var club in clubs)
            {
                var player = new Player { Id = Guid.NewGuid(), FullName = $"{country.Name}-{club.Name}", WikidataQid = $"Qp-{country.Name}-{club.Name}" };
                dbContext.Players.Add(player);
                dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = country.Name });
                dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = club.Name });
            }
        }

        await dbContext.SaveChangesAsync();
    }

    private HttpClient CreateAuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);
        return client;
    }

    // ---- REQ-301: generate-round runs one round ahead ----------------------

    [Test]
    public async Task GenerateRound_Post_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/internal/generate-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task GenerateRound_Post_ReturnsUnauthorized_WithWrongBearerToken()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-right-token");

        var response = await client.PostAsync("/internal/generate-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ301_GenerateRound_Post_CreatesFirstRound_WhenNoneExistYet()
    {
        await SeedFullyMatchedReferenceDataAsync(size: 3);
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/generate-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<GenerateRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.GameKey, Is.EqualTo(GridGameModule.XGGridGameKey));
        Assert.That(body.EndTime - body.StartTime, Is.EqualTo(TimeSpan.FromDays(3)));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Rounds.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ301_GenerateRound_Post_IsIdempotent_WhenAnUpcomingRoundAlreadyExists()
    {
        await SeedFullyMatchedReferenceDataAsync(size: 3);
        var client = CreateAuthorizedClient();

        // Call 1: no round exists yet -> creates round 1, which starts
        // immediately (StartTime ~= now), so it's already active by the time
        // call 2 runs a moment later.
        var first = await client.PostAsync("/internal/generate-round", content: null);
        await first.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        // Call 2: round 1 is active with no round scheduled after it yet ->
        // correctly generates round 2 (the genuine "one round ahead" case),
        // which starts 3 days from now and is therefore still upcoming.
        var second = await client.PostAsync("/internal/generate-round", content: null);
        var secondBody = await second.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        // Call 3: round 2 already exists and hasn't started yet -> already
        // one round ahead, so this must be a no-op, not a round 3.
        var third = await client.PostAsync("/internal/generate-round", content: null);
        var thirdBody = await third.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        Assert.That(third.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(thirdBody!.RoundId, Is.EqualTo(secondBody!.RoundId),
            "already one round ahead — a third call must not create a third round");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Rounds.CountAsync(), Is.EqualTo(2));
    }

    // ---- REQ-806: force-close-round is a non-Production-only test control --

    [Test]
    public async Task REQ806_ForceCloseRound_Post_ClosesRoundImmediately_InNonProductionEnvironment()
    {
        await SeedFullyMatchedReferenceDataAsync(size: 3);
        var client = CreateAuthorizedClient();
        var generateResponse = await client.PostAsync("/internal/generate-round", content: null);
        var round = await generateResponse.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        var response = await _factory.CreateClient().PostAsync($"/internal/test-data/force-close-round/{round!.RoundId}", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ForceCloseRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.EndTime, Is.LessThan(round.EndTime), "closing before the round's real end_time must pull it forward");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var persisted = await dbContext.Rounds.SingleAsync(r => r.Id == round.RoundId);
        Assert.That(persisted.GetStatus(DateTime.UtcNow), Is.EqualTo(RoundStatus.Closed));
    }

    [Test]
    public async Task ForceCloseRound_Post_ReturnsNotFound_ForUnknownRoundId()
    {
        var response = await _factory.CreateClient().PostAsync($"/internal/test-data/force-close-round/{Guid.NewGuid()}", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ForceCloseRound_Post_IsNeverRegistered_WhenEnvironmentIsProduction()
    {
        // A round that genuinely exists — proves the 404 below comes from
        // the route itself being absent (REQ-801's discipline, reused by
        // REQ-806), not merely "round not found" against an existing route.
        await SeedFullyMatchedReferenceDataAsync(size: 3);
        var generateResponse = await CreateAuthorizedClient().PostAsync("/internal/generate-round", content: null);
        var round = await generateResponse.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        // Program.cs reads several required config values (connection
        // string, Supabase settings) eagerly, before WebApplicationFactory's
        // ConfigureAppConfiguration/UseEnvironment hooks can take effect (those
        // only apply once the deferred host-build machinery intercepts
        // Build(), which happens after Program.cs's own top-level code has
        // already run) — real process environment variables are the only
        // override visible early enough to genuinely flip which environment
        // this host starts under, so appsettings.Development.json's values
        // are skipped the same way a real Production deployment would.
        using var _ = TemporaryEnvironmentVariables(
            ("ASPNETCORE_ENVIRONMENT", "Production"),
            ("ConnectionStrings__Database", "Host=localhost;Database=unused-in-tests;Username=postgres;Password=postgres"),
            ("Supabase__Url", "http://localhost:54321"),
            ("Supabase__AnonKey", "test-placeholder-anon-key"));

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.PostAsync($"/internal/test-data/force-close-round/{round!.RoundId}", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- REQ-807: seed-guessable-round is a non-Production-only test control --

    [Test]
    public async Task REQ807_SeedGuessableRound_Post_CreatesAnActiveRoundWithOneGuessableCell()
    {
        var response = await _factory.CreateClient().PostAsync("/internal/test-data/seed-guessable-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SeedGuessableRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.CorrectPlayerName, Is.Not.Empty);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var round = await dbContext.Rounds.SingleAsync(r => r.Id == body.RoundId);
        Assert.That(round.GetStatus(DateTime.UtcNow), Is.EqualTo(RoundStatus.Active));
        var instance = await dbContext.GridInstances.Include(gi => gi.Cells).SingleAsync(gi => gi.Id == round.GameInstanceId);
        Assert.That(instance.Cells.Select(c => c.Id), Does.Contain(body.CellId));
    }

    [Test]
    public async Task SeedGuessableRound_Post_IsNeverRegistered_WhenEnvironmentIsProduction()
    {
        using var _ = TemporaryEnvironmentVariables(
            ("ASPNETCORE_ENVIRONMENT", "Production"),
            ("ConnectionStrings__Database", "Host=localhost;Database=unused-in-tests;Username=postgres;Password=postgres"),
            ("Supabase__Url", "http://localhost:54321"),
            ("Supabase__AnonKey", "test-placeholder-anon-key"));

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.PostAsync("/internal/test-data/seed-guessable-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // Sets process environment variables for the duration of one test,
    // restoring each to its original value (including "unset") on dispose.
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

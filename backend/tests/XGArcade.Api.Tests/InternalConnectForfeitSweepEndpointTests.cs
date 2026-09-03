using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Connect;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGConnect;

namespace XGArcade.Api.Tests;

// REQ-1405/ADR-0103: API-level coverage for POST
// /internal/sweep-connect-forfeits — same bearer-token-gated
// WebApplicationFactory pattern InternalMatchmakingSweepEndpointTests uses
// for /internal/sweep-matchmaking-pairings. The forfeit/resolution logic
// itself is ConnectMatchLifecycleServiceTests' job (unit-level, against the
// service directly); this file only proves the endpoint wires that service
// up correctly, enforces the bearer-token gate, and maps an unhandled
// exception to a 500 with the exception's own message as detail (the
// documented /internal/* carve-out, coding-guidelines.md).
public class InternalConnectForfeitSweepEndpointTests
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

    private HttpClient CreateAuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);
        return client;
    }

    [Test]
    public async Task REQ1405_SweepConnectForfeits_Post_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/internal/sweep-connect-forfeits", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1405_SweepConnectForfeits_Post_ReturnsUnauthorized_WithWrongBearerToken()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-right-token");

        var response = await client.PostAsync("/internal/sweep-connect-forfeits", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1405_SweepConnectForfeits_Post_NoMatchesPastDeadline_ReturnsZeroCounts()
    {
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/sweep-connect-forfeits", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SweepConnectForfeitsResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.PlayersForfeited, Is.EqualTo(0));
        Assert.That(body.MatchesResolved, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ1405_SweepConnectForfeits_Post_MatchPastDeadline_ForfeitsBothAndResolvesToDraw()
    {
        Guid matchId;
        using (var scope = _factory.Services.CreateScope())
        {
            var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
            var now = DateTime.UtcNow;
            var startedAt = now.AddHours(-7);

            var createdMatch = await connectMatchRepository.AddMatchAsync(new ConnectMatch
            {
                Id = Guid.NewGuid(),
                PlayerAUserId = Guid.NewGuid(),
                PlayerBUserId = Guid.NewGuid(),
                CreatedAt = startedAt,
            });
            await connectMatchRepository.StartMatchAsync(createdMatch.Id, startedAt, startedAt.AddHours(6));
            matchId = createdMatch.Id;
        }

        var client = CreateAuthorizedClient();
        var response = await client.PostAsync("/internal/sweep-connect-forfeits", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SweepConnectForfeitsResponse>();
        Assert.That(body!.PlayersForfeited, Is.EqualTo(2));
        Assert.That(body.MatchesResolved, Is.EqualTo(1));

        using var verifyScope = _factory.Services.CreateScope();
        var verifyRepository = verifyScope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        var match = await verifyRepository.GetMatchByIdAsync(matchId);
        Assert.That(match, Is.Not.Null);
        Assert.That(match!.Status, Is.EqualTo(ConnectMatchStatus.Resolved));
        Assert.That(match.Outcome, Is.EqualTo(ConnectMatchOutcome.Draw));
        Assert.That(match.PlayerATimedOutAt, Is.Not.Null);
        Assert.That(match.PlayerBTimedOutAt, Is.Not.Null);
    }

    [Test]
    public async Task REQ1405_SweepConnectForfeits_Post_UnhandledException_Returns500WithExceptionMessageAsDetail()
    {
        // Swaps the real IConnectMatchLifecycleService for a throwing fake —
        // same services.RemoveAll<T>()/AddScoped<T, Throwing...>() approach
        // RoundEndpointTests' own ThrowingRoundGenerationService test uses
        // for its analogous /internal/* catch-all-exception regression
        // coverage, not a mocking framework substitute.
        var throwingFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IConnectMatchLifecycleService>();
                services.AddScoped<IConnectMatchLifecycleService, ThrowingConnectMatchLifecycleService>();
            });
        });
        var client = throwingFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);

        var response = await client.PostAsync("/internal/sweep-connect-forfeits", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Connect forfeit sweep failed unexpectedly"));
        Assert.That(problem.Detail, Is.EqualTo("simulated forfeit sweep failure"));
    }

    private sealed class ThrowingConnectMatchLifecycleService : IConnectMatchLifecycleService
    {
        public Task StartMatchIfBothPicksLockedAsync(Guid matchId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated forfeit sweep failure");

        public Task<ForfeitSweepResult> RunForfeitSweepAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated forfeit sweep failure");

        public Task<bool> TryResolveMatchIfBothTerminalAsync(Guid matchId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated forfeit sweep failure");

        public Task<IReadOnlyList<ConnectMatch>> GetMatchesAwaitingActionAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated forfeit sweep failure");
    }
}

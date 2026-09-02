using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Social;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Tests;

// REQ-1403/ADR-0103: API-level coverage for POST
// /internal/sweep-matchmaking-pairings — same bearer-token-gated
// WebApplicationFactory pattern InternalPredictGradingEndpointTests uses
// for /internal/grade-predict-matches. Unlike that endpoint,
// MatchmakingSweepService has no external API dependency (it's pure
// in-database work), so a real pairing can be exercised end to end here,
// not just the auth gate — the exact pairing/expiry/no-double-booking
// logic itself is MatchmakingSweepServiceTests' job (unit-level, against
// the service directly), this file only proves the endpoint wires that
// service up correctly and returns its counts.
public class InternalMatchmakingSweepEndpointTests
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
    public async Task REQ1403_SweepMatchmakingPairings_Post_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/internal/sweep-matchmaking-pairings", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1403_SweepMatchmakingPairings_Post_ReturnsUnauthorized_WithWrongBearerToken()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-right-token");

        var response = await client.PostAsync("/internal/sweep-matchmaking-pairings", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1403_SweepMatchmakingPairings_Post_NoWaitingOptIns_ReturnsZeroCounts()
    {
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/sweep-matchmaking-pairings", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SweepMatchmakingPairingsResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Paired, Is.EqualTo(0));
        Assert.That(body.Expired, Is.EqualTo(0));
        Assert.That(body.StillWaiting, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ1403_SweepMatchmakingPairings_Post_TwoOverlappingWaitingOptIns_PairsThemIntoARealConnectMatch()
    {
        Guid userAId;
        Guid userBId;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var now = DateTime.UtcNow;

            var userA = new User
            {
                Id = Guid.NewGuid(), AuthProviderUserId = Guid.NewGuid(), Email = "a@example.com",
                DisplayName = "Alex", EmailConfirmed = true, CreatedAt = now,
            };
            var userB = new User
            {
                Id = Guid.NewGuid(), AuthProviderUserId = Guid.NewGuid(), Email = "b@example.com",
                DisplayName = "Blair", EmailConfirmed = true, CreatedAt = now,
            };
            dbContext.Users.AddRange(userA, userB);

            dbContext.MatchmakingOptIns.AddRange(
                new MatchmakingOptIn
                {
                    Id = Guid.NewGuid(), UserId = userA.Id, OptedInAt = now, ExpiresAt = now.AddHours(12),
                    Status = MatchmakingOptInStatus.Waiting,
                },
                new MatchmakingOptIn
                {
                    Id = Guid.NewGuid(), UserId = userB.Id, OptedInAt = now.AddMinutes(1), ExpiresAt = now.AddMinutes(1).AddHours(12),
                    Status = MatchmakingOptInStatus.Waiting,
                });
            await dbContext.SaveChangesAsync();

            userAId = userA.Id;
            userBId = userB.Id;
        }

        var client = CreateAuthorizedClient();
        var response = await client.PostAsync("/internal/sweep-matchmaking-pairings", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SweepMatchmakingPairingsResponse>();
        Assert.That(body!.Paired, Is.EqualTo(2));
        Assert.That(body.Expired, Is.EqualTo(0));
        Assert.That(body.StillWaiting, Is.EqualTo(0));

        using var verifyScope = _factory.Services.CreateScope();
        var connectMatchRepository = verifyScope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        var optInRepository = verifyScope.ServiceProvider.GetRequiredService<IMatchmakingOptInRepository>();
        Assert.That(await optInRepository.GetWaitingOptInsAsync(), Is.Empty);

        var dbContextForRead = verifyScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var pairedOptIns = await dbContextForRead.MatchmakingOptIns
            .Where(o => o.UserId == userAId || o.UserId == userBId)
            .ToListAsync();
        Assert.That(pairedOptIns.All(o => o.Status == MatchmakingOptInStatus.Paired), Is.True);
        var matchId = pairedOptIns[0].ResultingMatchId;
        Assert.That(matchId, Is.Not.Null);
        Assert.That(pairedOptIns.All(o => o.ResultingMatchId == matchId), Is.True);

        var match = await connectMatchRepository.GetMatchByIdAsync(matchId!.Value);
        Assert.That(match, Is.Not.Null);
        Assert.That(new[] { match!.PlayerAUserId, match.PlayerBUserId }, Is.EquivalentTo(new Guid?[] { userAId, userBId }));
    }
}

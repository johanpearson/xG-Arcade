using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Auth;
using XGArcade.Api.Social;
using XGArcade.Data;
using XGArcade.Data.Entities;

namespace XGArcade.Api.Tests;

// REQ-1403: API-level coverage for POST /matchmaking/opt-in. Opting in is
// itself the consent (no accept/decline step) — see
// XGArcade.Core.Social.IMatchmakingService's own doc comment — so this
// file is intentionally small: auth gating plus one happy-path round trip
// proving the created row's shape (Waiting, a 12h-later ExpiresAt) over
// the real HTTP pipeline. The pairing/expiry sweep itself is
// MatchmakingSweepServiceTests' job, not this file's — that logic isn't
// reachable from any player-facing endpoint.
public class MatchmakingEndpointTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Auth:Mode", "local-e2e");

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

    private async Task<Guid> SeedUserAsync(Guid authProviderUserId, string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = authProviderUserId,
            Email = $"{authProviderUserId}@example.com",
            DisplayName = displayName,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user.Id;
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    [Test]
    public async Task REQ1403_PostMatchmakingOptIn_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/matchmaking/opt-in", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1403_PostMatchmakingOptIn_Authenticated_CreatesAWaitingOptInExpiringTwelveHoursLater()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);
        var beforeCall = DateTime.UtcNow;

        var response = await client.PostAsync("/matchmaking/opt-in", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var optIn = await response.Content.ReadFromJsonAsync<MatchmakingOptInResponse>();
        Assert.That(optIn!.Status, Is.EqualTo("Waiting"));
        Assert.That(optIn.ResultingMatchId, Is.Null);
        Assert.That(optIn.ExpiresAt - optIn.OptedInAt, Is.EqualTo(TimeSpan.FromHours(12)));
        Assert.That(optIn.OptedInAt, Is.GreaterThanOrEqualTo(beforeCall));
    }
}

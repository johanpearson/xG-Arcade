using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Auth;
using XGArcade.Api.Connect;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Tests;

// S-218 prep (REQ-1404/1405/1406/1409/1411): API-level coverage for
// GET /matches and GET /matches/{matchId} — the read-only surface
// unblocking S-218's frontend gameplay screen. Same
// WebApplicationFactory<Program> + in-memory-DbContext-swap + LocalE2EAuth
// pattern as ConnectMatchEndpointTests. The full REQ-1404/1405/1406/1409
// perspective-translation/terminal-state/mutual-invisibility branch matrix
// is already covered at the service level by XGArcade.Games.XGConnect.
// Tests/ConnectMatchQueryServiceTests.cs and is deliberately NOT
// re-proven here — this file only covers what only the real HTTP pipeline
// proves: auth gating, the endpoint's own response shaping over real DTOs,
// and the 404/403 outcome mapping.
public class ConnectMatchQueryEndpointTests
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

    private async Task<Guid> AddPlayerAsync(string fullName)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = new Player { Id = Guid.NewGuid(), FullName = fullName };
        dbContext.Players.Add(player);
        await dbContext.SaveChangesAsync();
        return player.Id;
    }

    private async Task<Guid> CreateMatchAsync(Guid playerAUserId, Guid playerBUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        var match = await connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = playerAUserId,
            PlayerBUserId = playerBUserId,
            CreatedAt = DateTime.UtcNow,
        });
        return match.Id;
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- Auth gating --------------------------------------------------------

    [Test]
    public async Task REQ1411_GetMatches_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/matches");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1404_GetMatchDetail_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/matches/{Guid.NewGuid()}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- GET /matches ---------------------------------------------------------

    [Test]
    public async Task REQ1411_GetMatches_ParticipantInOpenAndResolvedMatches_ReturnsBothInCallersPerspective()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var openMatchId = await CreateMatchAsync(userAId, userBId);

        var resolvedMatchId = await CreateMatchAsync(userAId, userBId);
        using (var scope = _factory.Services.CreateScope())
        {
            var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
            var now = DateTime.UtcNow;
            await connectMatchRepository.StartMatchAsync(resolvedMatchId, now, now.AddHours(6));
            await connectMatchRepository.ResolveMatchAsync(resolvedMatchId, ConnectMatchOutcome.PlayerAWin, now, 1, null);
        }

        var client = CreateAuthenticatedClient(aAuthProviderUserId);

        var response = await client.GetAsync("/matches");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<List<ConnectMatchListItemResponse>>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Select(m => m.MatchId), Is.EquivalentTo(new[] { openMatchId, resolvedMatchId }));

        var resolved = body.Single(m => m.MatchId == resolvedMatchId);
        Assert.That(resolved.Status, Is.EqualTo("Resolved"));
        Assert.That(resolved.Outcome, Is.EqualTo("Win"), "caller is PlayerA and Outcome is PlayerAWin");
        Assert.That(resolved.OpponentUserId, Is.EqualTo(userBId));
        Assert.That(resolved.AwaitingMyAction, Is.False);

        var open = body.Single(m => m.MatchId == openMatchId);
        Assert.That(open.Status, Is.EqualTo("AwaitingTargetPicks"));
        Assert.That(open.Outcome, Is.EqualTo("Pending"));
        Assert.That(open.AwaitingMyAction, Is.True);
    }

    // ---- GET /matches/{matchId} ------------------------------------------------

    [Test]
    public async Task REQ1404_GetMatchDetail_MatchDoesNotExist_ReturnsNotFound()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync($"/matches/{Guid.NewGuid()}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task REQ1404_GetMatchDetail_CallerNotAParticipant_ReturnsForbiddenWithProblemDetailsBody()
    {
        var userAId = await SeedUserAsync(Guid.NewGuid(), "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var matchId = await CreateMatchAsync(userAId, userBId);
        var outsiderAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(outsiderAuthProviderUserId, "Casey");
        var client = CreateAuthenticatedClient(outsiderAuthProviderUserId);

        var response = await client.GetAsync($"/matches/{matchId}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Not a participant"));
    }

    [Test]
    public async Task REQ1405_GetMatchDetail_MatchActive_ReturnsBothTargetPicksAndTerminalStates()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var matchId = await CreateMatchAsync(userAId, userBId);
        var aTargetPlayerId = await AddPlayerAsync("A Target");
        var bTargetPlayerId = await AddPlayerAsync("B Target");

        using (var scope = _factory.Services.CreateScope())
        {
            var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
            var now = DateTime.UtcNow;
            await connectMatchRepository.AddOrUpdateTargetPickAsync(matchId, userAId, aTargetPlayerId, now);
            await connectMatchRepository.AddOrUpdateTargetPickAsync(matchId, userBId, bTargetPlayerId, now);
            await connectMatchRepository.LockTargetPicksForMatchAsync(matchId);
            await connectMatchRepository.StartMatchAsync(matchId, now, now.AddHours(6));
        }

        var client = CreateAuthenticatedClient(aAuthProviderUserId);

        var response = await client.GetAsync($"/matches/{matchId}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ConnectMatchDetailResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Status, Is.EqualTo("Active"));
        Assert.That(body.MyTargetPick!.TargetPlayerId, Is.EqualTo(aTargetPlayerId));
        Assert.That(body.MyTargetPick.TargetPlayerName, Is.EqualTo("A Target"));
        Assert.That(body.OpponentTargetPick!.TargetPlayerId, Is.EqualTo(bTargetPlayerId));
        Assert.That(body.OpponentTargetPick.TargetPlayerName, Is.EqualTo("B Target"));
        Assert.That(body.OpponentUserId, Is.EqualTo(userBId));
        Assert.That(body.MyTerminalState, Is.EqualTo(new ConnectTerminalStateResponse(false, false, false)));
        Assert.That(body.OpponentTerminalState, Is.EqualTo(new ConnectTerminalStateResponse(false, false, false)));
    }

    // REQ-1404: over the real HTTP pipeline, an opponent's already-existing
    // (unlocked) pick must not leak through GET /matches/{matchId} while
    // the match is still AwaitingTargetPicks.
    [Test]
    public async Task REQ1404_GetMatchDetail_AwaitingTargetPicks_OpponentTargetPickStaysHidden()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var matchId = await CreateMatchAsync(userAId, userBId);
        var bTargetPlayerId = await AddPlayerAsync("B Target");

        using (var scope = _factory.Services.CreateScope())
        {
            var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
            await connectMatchRepository.AddOrUpdateTargetPickAsync(matchId, userBId, bTargetPlayerId, DateTime.UtcNow);
        }

        var client = CreateAuthenticatedClient(aAuthProviderUserId);

        var response = await client.GetAsync($"/matches/{matchId}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ConnectMatchDetailResponse>();
        Assert.That(body!.MyTargetPick, Is.Null);
        Assert.That(body.OpponentTargetPick, Is.Null);
    }
}

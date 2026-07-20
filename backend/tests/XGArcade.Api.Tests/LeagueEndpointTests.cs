using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Auth;
using XGArcade.Api.Leagues;
using XGArcade.Data;
using XGArcade.Data.Entities;

namespace XGArcade.Api.Tests;

// REQ-402/403: API-level coverage for POST /leagues, POST /leagues/join, and
// GET /leagues/mine — same WebApplicationFactory<Program> + in-memory-
// DbContext-swap pattern as every other *EndpointTests file (see
// AuthEndpointTests' own SetUp comment for why every XGArcadeDbContext-closed
// descriptor must be removed, not just the two obvious ones). Core-level
// scenarios (collision retry, idempotent rejoin) are already covered by
// XGArcade.Core.Tests/Leagues/LeagueServiceTests.cs and are deliberately NOT
// re-proven here — this file only covers what only the real HTTP pipeline
// proves: auth gating, request validation, and JSON response shape
// round-tripping.
public class LeagueEndpointTests
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

    private async Task<bool> IsMemberAsync(Guid leagueId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        return await dbContext.LeagueMemberships.AnyAsync(m => m.LeagueId == leagueId && m.UserId == userId);
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- REQ-402: POST /leagues -------------------------------------------

    [Test]
    public async Task REQ402_PostLeagues_CreatesCustomLeagueAndAutomaticallyAddsCreatorAsMember()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync("/leagues", new CreateLeagueRequest("Friends League"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<LeagueResponse>();
        Assert.That(body!.Name, Is.EqualTo("Friends League"));
        Assert.That(body.InviteCode, Is.Not.Null.And.Not.Empty);

        Assert.That(await IsMemberAsync(body.Id, userId), Is.True);
    }

    [Test]
    public async Task REQ402_PostLeagues_TwoSeparateCreations_ProduceDifferentInviteCodes()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var firstResponse = await client.PostAsJsonAsync("/leagues", new CreateLeagueRequest("League One"));
        var secondResponse = await client.PostAsJsonAsync("/leagues", new CreateLeagueRequest("League Two"));

        var first = await firstResponse.Content.ReadFromJsonAsync<LeagueResponse>();
        var second = await secondResponse.Content.ReadFromJsonAsync<LeagueResponse>();

        Assert.That(first!.InviteCode, Is.Not.EqualTo(second!.InviteCode));
    }

    [Test]
    public async Task REQ402_PostLeagues_EmptyName_ReturnsBadRequest()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync("/leagues", new CreateLeagueRequest("   "));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task REQ402_PostLeagues_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/leagues", new CreateLeagueRequest("Friends League"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- REQ-403: POST /leagues/join --------------------------------------

    [Test]
    public async Task REQ403_PostLeaguesJoin_ValidInviteCode_AddsCallerAsMember()
    {
        var creatorAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(creatorAuthProviderUserId, "Creator");
        var creatorClient = CreateAuthenticatedClient(creatorAuthProviderUserId);
        var createResponse = await creatorClient.PostAsJsonAsync("/leagues", new CreateLeagueRequest("Friends League"));
        var created = await createResponse.Content.ReadFromJsonAsync<LeagueResponse>();

        var joinerAuthProviderUserId = Guid.NewGuid();
        var joinerId = await SeedUserAsync(joinerAuthProviderUserId, "Joiner");
        var joinerClient = CreateAuthenticatedClient(joinerAuthProviderUserId);

        var joinResponse = await joinerClient.PostAsJsonAsync("/leagues/join", new JoinLeagueRequest(created!.InviteCode));

        Assert.That(joinResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var joined = await joinResponse.Content.ReadFromJsonAsync<LeagueResponse>();
        Assert.That(joined!.Id, Is.EqualTo(created.Id));
        Assert.That(await IsMemberAsync(created.Id, joinerId), Is.True);
    }

    // REQ-403: a code typed/pasted in lowercase must still resolve — invite
    // codes are only ever generated in uppercase.
    [Test]
    public async Task REQ403_PostLeaguesJoin_InviteCodeEnteredInLowercase_StillResolves()
    {
        var creatorAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(creatorAuthProviderUserId, "Creator");
        var creatorClient = CreateAuthenticatedClient(creatorAuthProviderUserId);
        var createResponse = await creatorClient.PostAsJsonAsync("/leagues", new CreateLeagueRequest("Friends League"));
        var created = await createResponse.Content.ReadFromJsonAsync<LeagueResponse>();

        var joinerAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(joinerAuthProviderUserId, "Joiner");
        var joinerClient = CreateAuthenticatedClient(joinerAuthProviderUserId);

        var joinResponse = await joinerClient.PostAsJsonAsync(
            "/leagues/join", new JoinLeagueRequest(created!.InviteCode.ToLowerInvariant()));

        Assert.That(joinResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task REQ403_PostLeaguesJoin_InvalidInviteCode_ReturnsNotFoundWithClearErrorAndCreatesNoMembership()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync("/leagues/join", new JoinLeagueRequest("NOSUCH1"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Invalid invite code"));
        Assert.That(problem.Detail, Does.Contain("NOSUCH1"));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.LeagueMemberships.AnyAsync(m => m.UserId == userId), Is.False);
    }

    [Test]
    public async Task REQ403_PostLeaguesJoin_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/leagues/join", new JoinLeagueRequest("ANY0001"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- "my custom leagues" list -----------------------------------------

    [Test]
    public async Task GetLeaguesMine_ReturnsOnlyTheCallersOwnCustomLeagues()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync("/leagues", new CreateLeagueRequest("My League"));

        var otherAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(otherAuthProviderUserId, "Someone Else");
        var otherClient = CreateAuthenticatedClient(otherAuthProviderUserId);
        await otherClient.PostAsJsonAsync("/leagues", new CreateLeagueRequest("Someone Else's League"));

        var response = await client.GetAsync("/leagues/mine");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var leagues = await response.Content.ReadFromJsonAsync<List<LeagueResponse>>();
        Assert.That(leagues!.Select(l => l.Name), Is.EquivalentTo(new[] { "My League" }));
    }

    [Test]
    public async Task GetLeaguesMine_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/leagues/mine");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}

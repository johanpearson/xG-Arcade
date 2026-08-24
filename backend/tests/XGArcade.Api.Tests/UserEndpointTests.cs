using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Auth;
using XGArcade.Api.Users;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Tests;

// REQ-411/S-178 (docs/requirements-document.md §4.4): API-level coverage for
// GET /users/{userId}/stats. Core-level GetUserStatsAsync scenarios (median/
// rank cross-check, min/mean, below-threshold rank omission, GameKey
// scoping) are already covered by
// XGArcade.Core.Tests/Leagues/LeaderboardServiceTests.cs and are
// deliberately NOT re-proven here — per this repo's established split (see
// LeaderboardEndpointTests' own header comment), this file only covers what
// only the real HTTP pipeline can prove: the 401 auth boundary, the 404 for
// an unknown userId, own-id/other-id response-shape symmetry (no privacy
// branching), and the zero-rounds-played shape round-tripping through real
// JSON. Same WebApplicationFactory<Program> + in-memory-DbContext-swap
// pattern as LeaderboardEndpointTests.
public class UserEndpointTests
{
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // See GuessEndpointTests' SetUp comment: local-e2e auth mode
                // avoids any live-network JWKS dependency in this test host.
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

    // ---- Seeding helpers ----------------------------------------------
    // Mirrors LeaderboardEndpointTests' own SeedMemberAsync/SeedLockedGuessAsync
    // shape exactly — same global-league auto-enrollment, same "a real,
    // already-closed Round row backs each qualifying Guess" requirement
    // GetPerRoundFinalPointsByUserIdsAsync (REQ-408/409) relies on.

    private async Task<Guid> SeedMemberAsync(Guid authProviderUserId, string displayName)
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

        var globalLeague = await dbContext.Leagues.SingleOrDefaultAsync(l => l.Type == LeagueTypes.Global);
        if (globalLeague is null)
        {
            globalLeague = new League { Id = Guid.NewGuid(), Name = "Global", Type = LeagueTypes.Global };
            dbContext.Leagues.Add(globalLeague);
            await dbContext.SaveChangesAsync();
        }

        dbContext.LeagueMemberships.Add(new LeagueMembership { LeagueId = globalLeague.Id, UserId = user.Id });
        await dbContext.SaveChangesAsync();

        return user.Id;
    }

    private async Task SeedLockedGuessAsync(Guid userId, int finalPoints)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GridGameModule.XGGridGameKey,
            GameInstanceId = Guid.NewGuid(),
            SequenceNumber = 1,
            StartTime = DateTime.UtcNow.AddDays(-2),
            EndTime = DateTime.UtcNow.AddDays(-1),
            AllowGuessChange = true,
            ClosedAt = DateTime.UtcNow.AddDays(-1),
        };
        dbContext.Rounds.Add(round);
        dbContext.Guesses.Add(new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = round.Id,
            UserId = userId,
            CellId = Guid.NewGuid(),
            SubmittedName = "Someone",
            IsCorrect = true,
            AttemptCount = 1,
            FinalUniquenessScore = finalPoints / 100.0,
            FinalPoints = finalPoints,
            CreatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private async Task SeedQualifyingRoundsAsync(Guid userId, params int[] finalPointsPerRound)
    {
        foreach (var finalPoints in finalPointsPerRound)
            await SeedLockedGuessAsync(userId, finalPoints);
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- REQ-411: authorization boundary -------------------------------

    [Test]
    public async Task REQ411_UserStatsGet_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/users/{Guid.NewGuid()}/stats");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ411_UserStatsGet_ReturnsUnauthorized_ForTokenWithNoMatchingLocalUser()
    {
        // A syntactically valid bearer token that doesn't resolve to a real
        // User row — REQ-411's own text: "this view is not reachable by a
        // fully logged-out visitor", same ResolveRequestingUserAsync 401
        // path LeaderboardEndpoints' routes already exercise.
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync($"/users/{Guid.NewGuid()}/stats");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- REQ-411: nonexistent target user --------------------------------

    [Test]
    public async Task REQ411_UserStatsGet_NonexistentUserId_ReturnsNotFound()
    {
        var requestingAuthProviderUserId = Guid.NewGuid();
        await SeedMemberAsync(requestingAuthProviderUserId, "You");
        var client = CreateAuthenticatedClient(requestingAuthProviderUserId);

        var response = await client.GetAsync($"/users/{Guid.NewGuid()}/stats");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("User not found"));
    }

    // ---- REQ-411: no privacy branching between own id and another's -------

    [Test]
    public async Task REQ411_UserStatsGet_OwnIdAndAnotherPlayersId_BothReturnOkWithIdenticalResponseShape()
    {
        // Proves there is no privacy-based branching (REQ-411's own "Out of
        // scope": no per-player privacy toggle) — same status code and same
        // JSON property set for both calls, even though the underlying
        // figures legitimately differ between the two players.
        var requestingAuthProviderUserId = Guid.NewGuid();
        var requestingUserId = await SeedMemberAsync(requestingAuthProviderUserId, "You");
        await SeedQualifyingRoundsAsync(requestingUserId, 10, 20, 30, 40, 50);
        var otherUserId = await SeedMemberAsync(Guid.NewGuid(), "Alex");
        await SeedQualifyingRoundsAsync(otherUserId, 5, 5, 5);
        var client = CreateAuthenticatedClient(requestingAuthProviderUserId);

        var ownResponse = await client.GetAsync($"/users/{requestingUserId}/stats");
        var otherResponse = await client.GetAsync($"/users/{otherUserId}/stats");

        Assert.That(ownResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(otherResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var ownBody = await ownResponse.Content.ReadFromJsonAsync<UserStatsResponse>();
        var otherBody = await otherResponse.Content.ReadFromJsonAsync<UserStatsResponse>();
        Assert.That(ownBody, Is.Not.Null);
        Assert.That(otherBody, Is.Not.Null);

        // Same shape: own id has enough qualifying rounds to be ranked
        // (Rank present); the other id has only 3 (Rank omitted) — proving
        // both are reachable through the exact same route/shape, not that
        // their values happen to match.
        Assert.That(ownBody!.HasRoundsPlayed, Is.True);
        Assert.That(ownBody.RoundsPlayed, Is.EqualTo(5));
        Assert.That(ownBody.BestFinalPoints, Is.EqualTo(10));
        Assert.That(ownBody.AverageFinalPoints, Is.EqualTo(30.0));
        Assert.That(ownBody.Rank, Is.EqualTo(1));

        Assert.That(otherBody!.HasRoundsPlayed, Is.True);
        Assert.That(otherBody.RoundsPlayed, Is.EqualTo(3));
        Assert.That(otherBody.BestFinalPoints, Is.EqualTo(5));
        Assert.That(otherBody.AverageFinalPoints, Is.EqualTo(5.0));
        Assert.That(otherBody.Rank, Is.Null, "below REQ-409's 5-round minimum");
    }

    // ---- REQ-411: zero-rounds-played shape, end-to-end over real HTTP -----

    [Test]
    public async Task REQ411_UserStatsGet_ZeroQualifyingRounds_ReturnsNoRoundsPlayedShapeOverRealHttpPipeline()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedMemberAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync($"/users/{userId}/stats");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<UserStatsResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.HasRoundsPlayed, Is.False);
        Assert.That(body.RoundsPlayed, Is.EqualTo(0));
        Assert.That(body.BestFinalPoints, Is.Null, "no rounds played -> omitted, never a 0-filled value");
        Assert.That(body.AverageFinalPoints, Is.Null);
        Assert.That(body.Rank, Is.Null);
    }

    // ---- REQ-410 (reused convention)/REQ-411: gameKey query param ----------

    [Test]
    public async Task REQ411_UserStatsGet_UnrecognizedGameKey_ReturnsBadRequestWithInvalidGameKeyTitle()
    {
        // UserEndpoints reuses LeaderboardEndpoints.ValidateGameKey — this
        // proves that shared validation is actually wired up on this route
        // too, not just present on the leaderboard routes.
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedMemberAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync($"/users/{userId}/stats?gameKey=not-a-real-game");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Invalid gameKey"));
    }
}

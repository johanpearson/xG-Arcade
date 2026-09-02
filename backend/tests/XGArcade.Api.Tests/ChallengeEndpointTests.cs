using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Auth;
using XGArcade.Api.Social;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Tests;

// REQ-1402: API-level coverage for POST /challenges, POST
// /challenges/{id}/accept, POST /challenges/{id}/decline, and GET
// /challenges/pending. Same WebApplicationFactory<Program> +
// in-memory-DbContext-swap + LocalE2EAuth pattern as FriendEndpointTests.
// The full REQ-1402 Given/When/Then branch matrix (duplicate-pending both
// directions, non-friend rejection, decline-then-resend) is already
// covered at the Core level by
// XGArcade.Core.Tests/Social/ChallengeServiceTests.cs and is deliberately
// NOT re-proven here — this file only covers what only the real HTTP
// pipeline proves: auth gating on every endpoint, a full send->accept
// round trip through the DTOs that also proves the accept handler's own
// ADR-0103 orchestration (a real ConnectMatch row exists after accept, not
// just a Challenge.ResultingMatchId pointing nowhere), a send->decline
// round trip, and one representative rejection (non-friend) reaching the
// client as a problem-details 403.
public class ChallengeEndpointTests
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

    private async Task MakeFriendsAsync(Guid userAId, Guid userBId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var (userA, userB) = userAId.CompareTo(userBId) <= 0 ? (userAId, userBId) : (userBId, userAId);
        dbContext.Friendships.Add(new Friendship
        {
            Id = Guid.NewGuid(),
            UserAId = userA,
            UserBId = userB,
            CreatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- Auth gating: every endpoint requires authorization ----------------

    [Test]
    public async Task REQ1402_PostChallenges_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/challenges", new SendChallengeRequest(Guid.NewGuid()));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1402_PostChallengesAccept_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync($"/challenges/{Guid.NewGuid()}/accept", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1402_PostChallengesDecline_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync($"/challenges/{Guid.NewGuid()}/decline", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1402_GetChallengesPending_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/challenges/pending");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- Happy-path round trips: DTO shape + ADR-0103 orchestration over ---
    // ---- the real HTTP pipeline ----------------------------------------------

    [Test]
    public async Task REQ1402_SendThenAccept_FullRoundTrip_CreatesChallengeThenARealConnectMatchRow()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var bAuthProviderUserId = Guid.NewGuid();
        var userBId = await SeedUserAsync(bAuthProviderUserId, "Blair");
        await MakeFriendsAsync(userAId, userBId);
        var clientA = CreateAuthenticatedClient(aAuthProviderUserId);
        var clientB = CreateAuthenticatedClient(bAuthProviderUserId);

        var sendResponse = await clientA.PostAsJsonAsync("/challenges", new SendChallengeRequest(userBId));
        Assert.That(sendResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var sent = await sendResponse.Content.ReadFromJsonAsync<ChallengeResponse>();
        Assert.That(sent!.ChallengerUserId, Is.EqualTo(userAId));
        Assert.That(sent.ChallengedUserId, Is.EqualTo(userBId));
        Assert.That(sent.Status, Is.EqualTo("Pending"));

        var pendingResponse = await clientB.GetAsync("/challenges/pending");
        Assert.That(pendingResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var pending = await pendingResponse.Content.ReadFromJsonAsync<List<ChallengeResponse>>();
        Assert.That(pending!.Select(c => c.Id), Is.EquivalentTo(new[] { sent.Id }));

        var acceptResponse = await clientB.PostAsync($"/challenges/{sent.Id}/accept", content: null);
        Assert.That(acceptResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<ChallengeResponse>();
        Assert.That(accepted!.Status, Is.EqualTo("Accepted"));
        Assert.That(accepted.ResolvedAt, Is.Not.Null);
        Assert.That(accepted.ResultingMatchId, Is.Not.Null);

        // Proves the endpoint's own ADR-0103 orchestration actually wrote a
        // real ConnectMatch row via IConnectMatchRepository, not just a
        // Challenge.ResultingMatchId pointing at an id nobody created.
        using var scope = _factory.Services.CreateScope();
        var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        var match = await connectMatchRepository.GetMatchByIdAsync(accepted.ResultingMatchId!.Value);
        Assert.That(match, Is.Not.Null);
        Assert.That(new[] { match!.PlayerAUserId, match.PlayerBUserId }, Is.EquivalentTo(new Guid?[] { userAId, userBId }));

        var pendingForBAfterAccept = await clientB.GetAsync("/challenges/pending");
        var pendingAfterAccept = await pendingForBAfterAccept.Content.ReadFromJsonAsync<List<ChallengeResponse>>();
        Assert.That(pendingAfterAccept, Is.Empty, "resolved challenge must no longer appear in the challenged user's pending list");
    }

    [Test]
    public async Task REQ1402_SendThenDecline_FullRoundTrip_ResolvesAsDeclinedWithoutCreatingAConnectMatch()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var bAuthProviderUserId = Guid.NewGuid();
        var userBId = await SeedUserAsync(bAuthProviderUserId, "Blair");
        await MakeFriendsAsync(userAId, userBId);
        var clientA = CreateAuthenticatedClient(aAuthProviderUserId);
        var clientB = CreateAuthenticatedClient(bAuthProviderUserId);

        var sendResponse = await clientA.PostAsJsonAsync("/challenges", new SendChallengeRequest(userBId));
        var sent = await sendResponse.Content.ReadFromJsonAsync<ChallengeResponse>();

        var declineResponse = await clientB.PostAsync($"/challenges/{sent!.Id}/decline", content: null);

        Assert.That(declineResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var declined = await declineResponse.Content.ReadFromJsonAsync<ChallengeResponse>();
        Assert.That(declined!.Status, Is.EqualTo("Declined"));
        Assert.That(declined.ResultingMatchId, Is.Null);
    }

    // ---- Representative rejection: proves the problem-details shape --------

    [Test]
    public async Task REQ1402_PostChallenges_NotFriends_ReturnsForbiddenWithProblemDetailsBody()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(aAuthProviderUserId, "Alex");
        var bAuthProviderUserId = Guid.NewGuid();
        var userBId = await SeedUserAsync(bAuthProviderUserId, "Blair");
        var clientA = CreateAuthenticatedClient(aAuthProviderUserId);

        var response = await clientA.PostAsJsonAsync("/challenges", new SendChallengeRequest(userBId));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Not friends"));
    }
}

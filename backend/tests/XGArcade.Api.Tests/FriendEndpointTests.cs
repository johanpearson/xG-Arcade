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

namespace XGArcade.Api.Tests;

// REQ-1401: API-level coverage for POST /friends/requests,
// POST /friends/requests/{id}/accept, POST /friends/requests/{id}/decline,
// GET /friends/requests/pending, and GET /friends. Same
// WebApplicationFactory<Program> + in-memory-DbContext-swap + LocalE2EAuth
// pattern as LeagueEndpointTests. The full REQ-1401 Given/When/Then
// branch matrix (duplicate-pending both directions, already-friends,
// self-request, decline-then-resend) is already covered at the Core level
// by XGArcade.Core.Tests/Social/FriendServiceTests.cs and is deliberately
// NOT re-proven here — this file only covers what only the real HTTP
// pipeline proves: auth gating on every endpoint, a full send->accept and
// send->decline round trip through the DTOs, and that one representative
// rejection (self-request) reaches the client as a problem-details 400.
public class FriendEndpointTests
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

    // ---- Auth gating: every endpoint requires authorization ----------------

    [Test]
    public async Task REQ1401_PostFriendsRequests_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/friends/requests", new SendFriendRequestRequest(Guid.NewGuid()));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1401_PostFriendsRequestsAccept_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync($"/friends/requests/{Guid.NewGuid()}/accept", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1401_PostFriendsRequestsDecline_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync($"/friends/requests/{Guid.NewGuid()}/decline", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1401_GetFriendsRequestsPending_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/friends/requests/pending");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1401_GetFriends_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/friends");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- Happy-path round trips: DTO shape over the real HTTP pipeline -----

    [Test]
    public async Task REQ1401_SendThenAccept_FullRoundTrip_CreatesRequestThenSymmetricFriendshipVisibleToBothViaGetFriends()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var bAuthProviderUserId = Guid.NewGuid();
        var userBId = await SeedUserAsync(bAuthProviderUserId, "Blair");
        var clientA = CreateAuthenticatedClient(aAuthProviderUserId);
        var clientB = CreateAuthenticatedClient(bAuthProviderUserId);

        var sendResponse = await clientA.PostAsJsonAsync("/friends/requests", new SendFriendRequestRequest(userBId));
        Assert.That(sendResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var sent = await sendResponse.Content.ReadFromJsonAsync<FriendRequestResponse>();
        Assert.That(sent!.RequesterUserId, Is.EqualTo(userAId));
        Assert.That(sent.RecipientUserId, Is.EqualTo(userBId));
        Assert.That(sent.Status, Is.EqualTo("Pending"));
        // REQ-1401 display-name fix: both parties' names always populated.
        Assert.That(sent.RequesterDisplayName, Is.EqualTo("Alex"));
        Assert.That(sent.RecipientDisplayName, Is.EqualTo("Blair"));

        var pendingResponse = await clientB.GetAsync("/friends/requests/pending");
        Assert.That(pendingResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var pending = await pendingResponse.Content.ReadFromJsonAsync<List<FriendRequestResponse>>();
        Assert.That(pending!.Select(r => r.Id), Is.EquivalentTo(new[] { sent.Id }));
        Assert.That(pending.Single().RequesterDisplayName, Is.EqualTo("Alex"));
        Assert.That(pending.Single().RecipientDisplayName, Is.EqualTo("Blair"));

        var acceptResponse = await clientB.PostAsync($"/friends/requests/{sent.Id}/accept", content: null);
        Assert.That(acceptResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<FriendRequestResponse>();
        Assert.That(accepted!.Status, Is.EqualTo("Accepted"));
        Assert.That(accepted.ResolvedAt, Is.Not.Null);
        Assert.That(accepted.RequesterDisplayName, Is.EqualTo("Alex"));
        Assert.That(accepted.RecipientDisplayName, Is.EqualTo("Blair"));

        var friendsOfAResponse = await clientA.GetAsync("/friends");
        var friendsOfA = await friendsOfAResponse.Content.ReadFromJsonAsync<List<FriendshipResponse>>();
        Assert.That(friendsOfA!.Select(f => f.FriendUserId), Is.EquivalentTo(new[] { userBId }));
        Assert.That(friendsOfA.Single().FriendDisplayName, Is.EqualTo("Blair"));

        var friendsOfBResponse = await clientB.GetAsync("/friends");
        var friendsOfB = await friendsOfBResponse.Content.ReadFromJsonAsync<List<FriendshipResponse>>();
        Assert.That(friendsOfB!.Select(f => f.FriendUserId), Is.EquivalentTo(new[] { userAId }));
        Assert.That(friendsOfB.Single().FriendDisplayName, Is.EqualTo("Alex"));

        var pendingForBAfterAccept = await clientB.GetAsync("/friends/requests/pending");
        var pendingAfterAccept = await pendingForBAfterAccept.Content.ReadFromJsonAsync<List<FriendRequestResponse>>();
        Assert.That(pendingAfterAccept, Is.Empty, "resolved request must no longer appear in the recipient's pending list");
    }

    [Test]
    public async Task REQ1401_SendThenDecline_FullRoundTrip_ResolvesAsDeclinedWithoutCreatingAFriendship()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(aAuthProviderUserId, "Alex");
        var bAuthProviderUserId = Guid.NewGuid();
        var userBId = await SeedUserAsync(bAuthProviderUserId, "Blair");
        var clientA = CreateAuthenticatedClient(aAuthProviderUserId);
        var clientB = CreateAuthenticatedClient(bAuthProviderUserId);

        var sendResponse = await clientA.PostAsJsonAsync("/friends/requests", new SendFriendRequestRequest(userBId));
        var sent = await sendResponse.Content.ReadFromJsonAsync<FriendRequestResponse>();

        var declineResponse = await clientB.PostAsync($"/friends/requests/{sent!.Id}/decline", content: null);

        Assert.That(declineResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var declined = await declineResponse.Content.ReadFromJsonAsync<FriendRequestResponse>();
        Assert.That(declined!.Status, Is.EqualTo("Declined"));
        Assert.That(declined.RequesterDisplayName, Is.EqualTo("Alex"));
        Assert.That(declined.RecipientDisplayName, Is.EqualTo("Blair"));

        var friendsOfAResponse = await clientA.GetAsync("/friends");
        var friendsOfA = await friendsOfAResponse.Content.ReadFromJsonAsync<List<FriendshipResponse>>();
        Assert.That(friendsOfA, Is.Empty);
    }

    // ---- REQ-1401 display-name batch-resolve: the multi-row case that an ---
    // ---- N+1-avoiding dictionary keying can subtly get wrong ---------------

    [Test]
    public async Task REQ1401_GetFriendsRequestsPending_TwoRequestsFromDifferentRequesters_EachRowHasCorrectlyMatchedDisplayName()
    {
        var recipientAuthProviderUserId = Guid.NewGuid();
        var recipientId = await SeedUserAsync(recipientAuthProviderUserId, "Recipient");
        var requester1AuthProviderUserId = Guid.NewGuid();
        var requester1Id = await SeedUserAsync(requester1AuthProviderUserId, "Casey");
        var requester2AuthProviderUserId = Guid.NewGuid();
        var requester2Id = await SeedUserAsync(requester2AuthProviderUserId, "Devon");
        var requester1Client = CreateAuthenticatedClient(requester1AuthProviderUserId);
        var requester2Client = CreateAuthenticatedClient(requester2AuthProviderUserId);
        var recipientClient = CreateAuthenticatedClient(recipientAuthProviderUserId);

        await requester1Client.PostAsJsonAsync("/friends/requests", new SendFriendRequestRequest(recipientId));
        await requester2Client.PostAsJsonAsync("/friends/requests", new SendFriendRequestRequest(recipientId));

        var pendingResponse = await recipientClient.GetAsync("/friends/requests/pending");
        Assert.That(pendingResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var pending = await pendingResponse.Content.ReadFromJsonAsync<List<FriendRequestResponse>>();

        Assert.That(pending, Has.Count.EqualTo(2));
        var byRequesterId = pending!.ToDictionary(r => r.RequesterUserId, r => r.RequesterDisplayName);
        Assert.That(byRequesterId[requester1Id], Is.EqualTo("Casey"));
        Assert.That(byRequesterId[requester2Id], Is.EqualTo("Devon"));
        Assert.That(pending.Select(r => r.RecipientDisplayName), Has.All.EqualTo("Recipient"));
    }

    // ---- Representative rejection: proves the problem-details shape --------

    [Test]
    public async Task REQ1401_PostFriendsRequests_SelfRequest_ReturnsBadRequestWithProblemDetailsBody()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync("/friends/requests", new SendFriendRequestRequest(userId));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Cannot friend yourself"));
    }
}

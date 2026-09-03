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

// REQ-1410 (docs/requirements-document.md §4.15): API-level coverage for
// POST/GET /matches/{matchId}/chat-messages. Same WebApplicationFactory<Program>
// + in-memory-DbContext-swap + LocalE2EAuth pattern as
// ConnectChainStepEndpointTests/ConnectMatchEndpointTests. The full REQ-1410
// Given/When/Then branch matrix (message visibility scoped to one match,
// readability after resolution, non-participant rejection) is already
// covered at the service level by
// XGArcade.Games.XGConnect.Tests/ConnectChatServiceTests.cs and is
// deliberately NOT re-proven exhaustively here — this file covers auth
// gating, status-code mapping for each outcome over the real HTTP pipeline,
// and one full send-then-read round trip proving the endpoints' own response
// shaping (ChatMessageResponse) really persists to and reads from the
// database, not just the handler's own return value.
public class ConnectChatEndpointTests
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

    private async Task<Guid> CreateMatchAsync(
        Guid playerAUserId, Guid playerBUserId,
        ConnectMatchStatus status = ConnectMatchStatus.Active, ConnectMatchOutcome outcome = ConnectMatchOutcome.Pending)
    {
        using var scope = _factory.Services.CreateScope();
        var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        var match = await connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = playerAUserId,
            PlayerBUserId = playerBUserId,
            CreatedAt = DateTime.UtcNow,
            Status = status,
            Outcome = outcome,
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
    public async Task REQ1410_PostChatMessage_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/matches/{Guid.NewGuid()}/chat-messages", new SendChatMessageRequest("hello"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1410_GetChatMessages_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/matches/{Guid.NewGuid()}/chat-messages");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- Happy-path round trip: one player sends, the other reads, over
    // ---- the real HTTP pipeline ------------------------------------------------

    [Test]
    public async Task REQ1410_PostThenGetChatMessage_Participant_MessageIsVisibleToTheOtherParticipant()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var bAuthProviderUserId = Guid.NewGuid();
        var userBId = await SeedUserAsync(bAuthProviderUserId, "Blair");
        var matchId = await CreateMatchAsync(userAId, userBId);
        var clientA = CreateAuthenticatedClient(aAuthProviderUserId);
        var clientB = CreateAuthenticatedClient(bAuthProviderUserId);

        var postResponse = await clientA.PostAsJsonAsync(
            $"/matches/{matchId}/chat-messages", new SendChatMessageRequest("gl hf"));
        Assert.That(postResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var posted = await postResponse.Content.ReadFromJsonAsync<ChatMessageResponse>();
        Assert.That(posted!.MessageText, Is.EqualTo("gl hf"));
        Assert.That(posted.SenderUserId, Is.EqualTo(userAId));

        var getResponse = await clientB.GetAsync($"/matches/{matchId}/chat-messages");
        Assert.That(getResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var messages = await getResponse.Content.ReadFromJsonAsync<List<ChatMessageResponse>>();
        Assert.That(messages, Has.Count.EqualTo(1));
        Assert.That(messages![0].Id, Is.EqualTo(posted.Id));
        Assert.That(messages[0].MessageText, Is.EqualTo("gl hf"));
    }

    // ---- Chat remains readable once the match has reached a terminal state --

    [Test]
    public async Task REQ1410_GetChatMessages_MatchResolved_StillReturnsOkWithMessages()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var bAuthProviderUserId = Guid.NewGuid();
        var userBId = await SeedUserAsync(bAuthProviderUserId, "Blair");
        var matchId = await CreateMatchAsync(userAId, userBId);
        var clientA = CreateAuthenticatedClient(aAuthProviderUserId);
        await clientA.PostAsJsonAsync($"/matches/{matchId}/chat-messages", new SendChatMessageRequest("before resolution"));

        using (var scope = _factory.Services.CreateScope())
        {
            var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
            await connectMatchRepository.ResolveMatchAsync(matchId, ConnectMatchOutcome.PlayerAWin, DateTime.UtcNow, 3, null);
        }

        var response = await clientA.GetAsync($"/matches/{matchId}/chat-messages");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var messages = await response.Content.ReadFromJsonAsync<List<ChatMessageResponse>>();
        Assert.That(messages, Has.Count.EqualTo(1));
        Assert.That(messages![0].MessageText, Is.EqualTo("before resolution"));
    }

    // ---- Precondition failures --------------------------------------------

    [Test]
    public async Task REQ1410_PostChatMessage_MatchNotFound_ReturnsNotFound()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{Guid.NewGuid()}/chat-messages", new SendChatMessageRequest("anyone home?"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task REQ1410_GetChatMessages_MatchNotFound_ReturnsNotFound()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync($"/matches/{Guid.NewGuid()}/chat-messages");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task REQ1410_PostChatMessage_CallerNotAParticipant_ReturnsForbidden()
    {
        var userAId = await SeedUserAsync(Guid.NewGuid(), "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var matchId = await CreateMatchAsync(userAId, userBId);
        var outsiderAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(outsiderAuthProviderUserId, "Outsider");
        var client = CreateAuthenticatedClient(outsiderAuthProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chat-messages", new SendChatMessageRequest("let me in"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Not a participant"));
    }

    [Test]
    public async Task REQ1410_GetChatMessages_CallerNotAParticipant_ReturnsForbidden()
    {
        var userAId = await SeedUserAsync(Guid.NewGuid(), "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var matchId = await CreateMatchAsync(userAId, userBId);
        var outsiderAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(outsiderAuthProviderUserId, "Outsider");
        var client = CreateAuthenticatedClient(outsiderAuthProviderUserId);

        var response = await client.GetAsync($"/matches/{matchId}/chat-messages");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Not a participant"));
    }
}

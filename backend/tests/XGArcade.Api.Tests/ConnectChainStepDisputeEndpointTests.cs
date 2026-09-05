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

// REQ-1412/1413 (docs/requirements-document.md §4.15), ADR-0109: API-level
// coverage for the dispute-a-failed-chain-step raise/approve/deny/list
// surface. Same WebApplicationFactory<Program> + in-memory-DbContext-swap +
// LocalE2EAuth pattern as ConnectChainStepEndpointTests. The full service-
// level branch matrix (retry-consumption, cascading denial, reopen-a-
// resolved-match, scoring parity) is already covered at the service level by
// XGArcade.Games.XGConnect.Tests/ConnectChainStepDisputeServiceTests.cs and
// is deliberately NOT re-proven here — this file only covers status-code
// mapping for each outcome, over the real HTTP pipeline.
public class ConnectChainStepDisputeEndpointTests
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

    private async Task<Guid> SeedPlayerAsync(string fullName)
    {
        using var scope = _factory.Services.CreateScope();
        var playerRepository = scope.ServiceProvider.GetRequiredService<IPlayerRepository>();
        var player = await playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = fullName });
        return player.Id;
    }

    private async Task<Guid> CreateActiveMatchAsync(Guid aUserId, Guid bUserId, Guid aTargetPlayerId, Guid bTargetPlayerId)
    {
        using var scope = _factory.Services.CreateScope();
        var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        var now = DateTime.UtcNow;
        var match = await connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = aUserId,
            PlayerBUserId = bUserId,
            CreatedAt = now,
            Status = ConnectMatchStatus.Active,
            StartedAt = now,
            DeadlineUtc = now.AddHours(6),
        });
        await connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, aUserId, aTargetPlayerId, now);
        await connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, bUserId, bTargetPlayerId, now);
        await connectMatchRepository.LockTargetPicksForMatchAsync(match.Id);
        return match.Id;
    }

    private async Task<Guid> AddInvalidChainStepAsync(Guid matchId, Guid userId, Guid candidatePlayerId, int position, int attemptNumber)
    {
        using var scope = _factory.Services.CreateScope();
        var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        var step = await connectMatchRepository.AddChainStepAsync(new ConnectChainStep
        {
            Id = Guid.NewGuid(),
            ConnectMatchId = matchId,
            UserId = userId,
            Position = position,
            AttemptNumber = attemptNumber,
            CandidatePlayerId = candidatePlayerId,
            IsValid = false,
            ClosesChain = false,
            SubmittedAt = DateTime.UtcNow,
        });
        return step.Id;
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- Auth gating --------------------------------------------------------

    [Test]
    public async Task REQ1412_PostDispute_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/matches/{Guid.NewGuid()}/chain-steps/{Guid.NewGuid()}/dispute", new RaiseChainStepDisputeRequest("Arsenal"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- Raise ----------------------------------------------------------------

    [Test]
    public async Task REQ1412_PostDispute_OwnFailedStep_ReturnsOkWithPendingDispute()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var candidateId = await SeedPlayerAsync("Middle Link Player");
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var stepId = await AddInvalidChainStepAsync(matchId, userAId, candidateId, position: 1, attemptNumber: 1);
        var client = CreateAuthenticatedClient(aAuthProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps/{stepId}/dispute", new RaiseChainStepDisputeRequest("Arsenal"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ChainStepDisputeResponse>();
        Assert.That(body!.Status, Is.EqualTo("Pending"));
        Assert.That(body.ClaimedClubName, Is.EqualTo("Arsenal"));
        Assert.That(body.ChainStepId, Is.EqualTo(stepId));
    }

    [Test]
    public async Task REQ1412_PostDispute_NotStepOwner_ReturnsForbidden()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var bAuthProviderUserId = Guid.NewGuid();
        var userBId = await SeedUserAsync(bAuthProviderUserId, "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var candidateId = await SeedPlayerAsync("Middle Link Player");
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var stepId = await AddInvalidChainStepAsync(matchId, userAId, candidateId, position: 1, attemptNumber: 1);
        var client = CreateAuthenticatedClient(bAuthProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps/{stepId}/dispute", new RaiseChainStepDisputeRequest("Arsenal"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Not your step"));
    }

    [Test]
    public async Task REQ1412_PostDispute_MatchNotFound_ReturnsNotFound()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{Guid.NewGuid()}/chain-steps/{Guid.NewGuid()}/dispute", new RaiseChainStepDisputeRequest("Arsenal"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- Approve/Deny -----------------------------------------------------------

    [Test]
    public async Task REQ1413_PostApprove_ByOpponent_ReturnsOkWithApprovedDispute()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var bAuthProviderUserId = Guid.NewGuid();
        var userBId = await SeedUserAsync(bAuthProviderUserId, "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var candidateId = await SeedPlayerAsync("Middle Link Player");
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var stepId = await AddInvalidChainStepAsync(matchId, userAId, candidateId, position: 1, attemptNumber: 1);
        var disputingClient = CreateAuthenticatedClient(aAuthProviderUserId);
        var raiseResponse = await disputingClient.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps/{stepId}/dispute", new RaiseChainStepDisputeRequest("Arsenal"));
        var raised = await raiseResponse.Content.ReadFromJsonAsync<ChainStepDisputeResponse>();

        var opponentClient = CreateAuthenticatedClient(bAuthProviderUserId);
        var response = await opponentClient.PostAsync($"/matches/{matchId}/disputes/{raised!.DisputeId}/approve", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ChainStepDisputeResponse>();
        Assert.That(body!.Status, Is.EqualTo("Approved"));
    }

    [Test]
    public async Task REQ1413_PostApprove_ByDisputingPlayerThemselves_ReturnsForbidden()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var candidateId = await SeedPlayerAsync("Middle Link Player");
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var stepId = await AddInvalidChainStepAsync(matchId, userAId, candidateId, position: 1, attemptNumber: 1);
        var client = CreateAuthenticatedClient(aAuthProviderUserId);
        var raiseResponse = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps/{stepId}/dispute", new RaiseChainStepDisputeRequest("Arsenal"));
        var raised = await raiseResponse.Content.ReadFromJsonAsync<ChainStepDisputeResponse>();

        var response = await client.PostAsync($"/matches/{matchId}/disputes/{raised!.DisputeId}/approve", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Cannot review your own dispute"));
    }

    [Test]
    public async Task REQ1413_PostApprove_ByThirdParty_ReturnsForbidden()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var candidateId = await SeedPlayerAsync("Middle Link Player");
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var stepId = await AddInvalidChainStepAsync(matchId, userAId, candidateId, position: 1, attemptNumber: 1);
        var disputingClient = CreateAuthenticatedClient(aAuthProviderUserId);
        var raiseResponse = await disputingClient.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps/{stepId}/dispute", new RaiseChainStepDisputeRequest("Arsenal"));
        var raised = await raiseResponse.Content.ReadFromJsonAsync<ChainStepDisputeResponse>();

        var outsiderAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(outsiderAuthProviderUserId, "Outsider");
        var outsiderClient = CreateAuthenticatedClient(outsiderAuthProviderUserId);

        var response = await outsiderClient.PostAsync($"/matches/{matchId}/disputes/{raised!.DisputeId}/approve", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Not a participant"));
    }

    [Test]
    public async Task REQ1413_PostDeny_ByOpponent_ReturnsOkWithDeniedDispute()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var bAuthProviderUserId = Guid.NewGuid();
        var userBId = await SeedUserAsync(bAuthProviderUserId, "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var candidateId = await SeedPlayerAsync("Middle Link Player");
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var stepId = await AddInvalidChainStepAsync(matchId, userAId, candidateId, position: 1, attemptNumber: 1);
        var disputingClient = CreateAuthenticatedClient(aAuthProviderUserId);
        var raiseResponse = await disputingClient.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps/{stepId}/dispute", new RaiseChainStepDisputeRequest("Arsenal"));
        var raised = await raiseResponse.Content.ReadFromJsonAsync<ChainStepDisputeResponse>();

        var opponentClient = CreateAuthenticatedClient(bAuthProviderUserId);
        var response = await opponentClient.PostAsync($"/matches/{matchId}/disputes/{raised!.DisputeId}/deny", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ChainStepDisputeResponse>();
        Assert.That(body!.Status, Is.EqualTo("Denied"));
    }

    // ---- List --------------------------------------------------------------

    [Test]
    public async Task REQ1412_GetDisputes_ReturnsEveryDisputeInMatch()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var candidateId = await SeedPlayerAsync("Middle Link Player");
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var stepId = await AddInvalidChainStepAsync(matchId, userAId, candidateId, position: 1, attemptNumber: 1);
        var client = CreateAuthenticatedClient(aAuthProviderUserId);
        await client.PostAsJsonAsync($"/matches/{matchId}/chain-steps/{stepId}/dispute", new RaiseChainStepDisputeRequest("Arsenal"));

        var response = await client.GetAsync($"/matches/{matchId}/disputes");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<List<ChainStepDisputeListItemResponse>>();
        Assert.That(body, Has.Count.EqualTo(1));
        Assert.That(body![0].RaisedByMe, Is.True);
        Assert.That(body[0].ClaimedClubName, Is.EqualTo("Arsenal"));
    }

    [Test]
    public async Task REQ1412_GetDisputes_NotAParticipant_ReturnsForbidden()
    {
        var userAId = await SeedUserAsync(Guid.NewGuid(), "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var outsiderAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(outsiderAuthProviderUserId, "Outsider");
        var client = CreateAuthenticatedClient(outsiderAuthProviderUserId);

        var response = await client.GetAsync($"/matches/{matchId}/disputes");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}

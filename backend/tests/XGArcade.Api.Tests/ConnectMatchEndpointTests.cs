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

// REQ-1404: API-level coverage for POST /matches/{matchId}/target-pick. Same
// WebApplicationFactory<Program> + in-memory-DbContext-swap + LocalE2EAuth
// pattern as ChallengeEndpointTests. The full REQ-1404 Given/When/Then
// branch matrix (free pre-lock resubmission, trivial-pair rejection leaving
// the first player's pick unaffected, non-trivial locking, live-lookup-
// unavailable, every mechanical outcome) is already covered at the service
// level by XGArcade.Games.XGConnect.Tests/ConnectTargetPickServiceTests.cs
// and is deliberately NOT re-proven here — this file only covers what only
// the real HTTP pipeline proves: auth gating, a full first-pick-then-
// completing-pick round trip through the real DTOs (proving the endpoint's
// own response shaping and that both picks really lock in the database, not
// just in the handler's return value), one representative rejection
// (TriviallyConnected) reaching the client as a problem-details 409, and
// (bug fix, S-218 prep, ADR-0007) the new TargetPlayerNotFound outcome
// reaching the client as a problem-details 404.
//
// Target players are real seeded Player rows (SeedPlayerAsync) — the
// request body now carries a NAME, resolved server-side via IPlayerRepository
// (never a client-supplied Guid; see IConnectTargetPickService's own doc
// comment for why). Each target player's PlayerCareerStint rows are seeded
// directly (never via a real Wikidata call) — the "fetch once, cache
// forever" behavior means a player with at least one cached stint row never
// triggers PlayerCareerOverlapService's live IWikidataClient lookup, so this
// avoids needing to swap out the real HTTP-backed IWikidataClient
// registration the way the DbContext is swapped below.
public class ConnectMatchEndpointTests
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

    private async Task<Player> SeedPlayerAsync(string fullName)
    {
        using var scope = _factory.Services.CreateScope();
        var playerRepository = scope.ServiceProvider.GetRequiredService<IPlayerRepository>();
        return await playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = fullName });
    }

    // Seeds a single cached PlayerCareerStint row for targetPlayerId so
    // PlayerCareerOverlapService's "already cached, never call Wikidata live"
    // path applies — see this file's own doc comment.
    private async Task SeedCareerStintAsync(Guid targetPlayerId, string clubName, int startYear, int? endYear)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        dbContext.PlayerCareerStints.Add(new PlayerCareerStint
        {
            Id = Guid.NewGuid(),
            PlayerId = targetPlayerId,
            ClubName = clubName,
            StartYear = startYear,
            EndYear = endYear,
        });
        await dbContext.SaveChangesAsync();
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- Auth gating --------------------------------------------------------

    [Test]
    public async Task REQ1404_PostTargetPick_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/matches/{Guid.NewGuid()}/target-pick", new SubmitTargetPickRequest("Anyone"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- Happy-path round trip: first pick, then the completing,
    // ---- non-trivial pick, over the real HTTP pipeline ----------------------

    [Test]
    public async Task REQ1404_PostTargetPick_FirstThenNonTrivialCompletingPick_LocksBothRowsInTheDatabase()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var bAuthProviderUserId = Guid.NewGuid();
        var userBId = await SeedUserAsync(bAuthProviderUserId, "Blair");
        var matchId = await CreateMatchAsync(userAId, userBId);
        var aTargetPlayer = await SeedPlayerAsync("Alpha Target");
        var bTargetPlayer = await SeedPlayerAsync("Bravo Target");
        // Different, non-overlapping clubs — never trivially connected.
        await SeedCareerStintAsync(aTargetPlayer.Id, "Arsenal", 1999, 2007);
        await SeedCareerStintAsync(bTargetPlayer.Id, "Chelsea", 1999, 2007);
        var clientA = CreateAuthenticatedClient(aAuthProviderUserId);
        var clientB = CreateAuthenticatedClient(bAuthProviderUserId);

        var firstResponse = await clientA.PostAsJsonAsync(
            $"/matches/{matchId}/target-pick", new SubmitTargetPickRequest(aTargetPlayer.FullName));
        Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var first = await firstResponse.Content.ReadFromJsonAsync<SubmitTargetPickResponse>();
        Assert.That(first!.TargetPlayerId, Is.EqualTo(aTargetPlayer.Id));
        Assert.That(first.Locked, Is.False);

        var completingResponse = await clientB.PostAsJsonAsync(
            $"/matches/{matchId}/target-pick", new SubmitTargetPickRequest(bTargetPlayer.FullName));
        Assert.That(completingResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var completing = await completingResponse.Content.ReadFromJsonAsync<SubmitTargetPickResponse>();
        Assert.That(completing!.TargetPlayerId, Is.EqualTo(bTargetPlayer.Id));
        Assert.That(completing.Locked, Is.True);

        // Proves both rows are really locked in the database, not just in
        // the handler's own return value.
        using var scope = _factory.Services.CreateScope();
        var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        var picks = await connectMatchRepository.GetTargetPicksForMatchAsync(matchId);
        Assert.That(picks, Has.Count.EqualTo(2));
        Assert.That(picks.All(p => p.IsLocked), Is.True);
    }

    // ---- Representative rejection: proves the problem-details shape --------

    [Test]
    public async Task REQ1404_PostTargetPick_TriviallyConnectedCompletingPick_ReturnsConflictWithProblemDetailsBody()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var bAuthProviderUserId = Guid.NewGuid();
        var userBId = await SeedUserAsync(bAuthProviderUserId, "Blair");
        var matchId = await CreateMatchAsync(userAId, userBId);
        var aTargetPlayer = await SeedPlayerAsync("Alpha Target");
        var bTargetPlayer = await SeedPlayerAsync("Bravo Target");
        // Same club, overlapping years — a direct, trivial connection.
        await SeedCareerStintAsync(aTargetPlayer.Id, "Arsenal", 1999, 2007);
        await SeedCareerStintAsync(bTargetPlayer.Id, "Arsenal", 2003, 2010);
        var clientA = CreateAuthenticatedClient(aAuthProviderUserId);
        var clientB = CreateAuthenticatedClient(bAuthProviderUserId);
        await clientA.PostAsJsonAsync($"/matches/{matchId}/target-pick", new SubmitTargetPickRequest(aTargetPlayer.FullName));

        var response = await clientB.PostAsJsonAsync(
            $"/matches/{matchId}/target-pick", new SubmitTargetPickRequest(bTargetPlayer.FullName));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Target picks are already connected"));

        // The first player's own pick survives the rejected second pick.
        using var scope = _factory.Services.CreateScope();
        var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        var playerAPick = await connectMatchRepository.GetTargetPickAsync(matchId, userAId);
        Assert.That(playerAPick, Is.Not.Null);
        Assert.That(playerAPick!.TargetPlayerId, Is.EqualTo(aTargetPlayer.Id));
        Assert.That(playerAPick.IsLocked, Is.False);
    }

    // ---- Bug fix (S-218 prep, ADR-0007): an unresolvable name reaches the
    // ---- client as a problem-details 404, never a 5xx --------------------

    [Test]
    public async Task REQ1404_PostTargetPick_TargetPlayerNameDoesNotResolve_ReturnsNotFoundWithProblemDetailsBody()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var bAuthProviderUserId = Guid.NewGuid();
        var userBId = await SeedUserAsync(bAuthProviderUserId, "Blair");
        var matchId = await CreateMatchAsync(userAId, userBId);
        var clientA = CreateAuthenticatedClient(aAuthProviderUserId);

        var response = await clientA.PostAsJsonAsync(
            $"/matches/{matchId}/target-pick", new SubmitTargetPickRequest("Nobody Real"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Target player not found"));

        using var scope = _factory.Services.CreateScope();
        var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        Assert.That(await connectMatchRepository.GetTargetPicksForMatchAsync(matchId), Is.Empty);
    }
}

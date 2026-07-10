using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Admin;
using XGArcade.Api.Auth;
using XGArcade.Api.Guesses;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Tests;

// S-012 (docs/backlog.md): API-level coverage for /admin/player-data/*
// and /admin/player-overrides (REQ-501/502/503) — the "Admin" authorization
// policy (Admin__UserIds, AdminAuthorizationHandler) and PlayerOverride CRUD.
public class AdminEndpointTests
{
    // Fixed so every test can configure the same "this is an admin" identity
    // via Admin:UserIds without re-creating the factory per test.
    private static readonly Guid AdminAuthProviderUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Same in-process HS256 signer/validator as GuessEndpointTests —
                // see that file's SetUp comment for why (ADR-0017).
                builder.UseSetting("Auth:Mode", "local-e2e");
                builder.UseSetting("Admin:UserIds", AdminAuthProviderUserId.ToString());

                builder.ConfigureServices(services =>
                {
                    // Same in-memory-DbContext swap as every other
                    // XGArcade.Api.Tests file — see AuthEndpointTests' SetUp
                    // comment for why every XGArcadeDbContext-closed
                    // descriptor must be removed, not just the two obvious ones.
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

    private async Task<Guid> SeedPlayerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = $"Q-{Guid.NewGuid()}" };
        dbContext.Players.Add(player);
        await dbContext.SaveChangesAsync();
        return player.Id;
    }

    private async Task SeedUnverifiedPlayerDataAsync(Guid playerId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        dbContext.PlayerData.Add(new PlayerData
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Field = "club",
            Value = "Arsenal",
            Source = "wikidata",
            Confidence = "unverified",
            SyncedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private async Task<Guid> SeedOverrideAsync(Guid playerId, string field = "club", string value = "Arsenal")
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var playerOverride = new PlayerOverride
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Field = field,
            Value = value,
            Reason = "Manual correction",
            LockedByAdminId = AdminAuthProviderUserId,
            LockedAt = DateTime.UtcNow,
        };
        dbContext.PlayerOverrides.Add(playerOverride);
        await dbContext.SaveChangesAsync();
        return playerOverride.Id;
    }

    // Same shape as GuessEndpointTests.SeedUserAsync — needed here too since
    // REQ501_CreatePlayerOverride_FlipsCellCorrectness_ForSubsequentGuess
    // submits a real guess through the player-facing endpoint, which
    // requires a matching local User row for the bearer token's "sub".
    private async Task SeedGuessingUserAsync(Guid authProviderUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = authProviderUserId,
            Email = $"{authProviderUserId}@example.com",
            DisplayName = "Test Player",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    // Seeds a Round/GridCell requiring nationality=France + club=Arsenal
    // (same shape as GuessEndpointTests.SeedRoundWithCellAsync), plus a
    // player who satisfies the row category (nationality=France) but NOT
    // the column category (cached club=Barcelona, not Arsenal) — so a guess
    // of that player's name is incorrect until an admin override for
    // "club" flips it, per ADR-0015. AllowGuessChange=true so the same
    // cell/round can be guessed a second time after the override exists.
    private async Task<(Guid RoundId, Guid CellId, Guid PlayerId, string PlayerFullName)> SeedRoundWithCellAndMisfitPlayerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        var instanceId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        dbContext.GridInstances.Add(new GridInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            Cells =
            [
                new GridCell
                {
                    Id = cellId,
                    GridInstanceId = instanceId,
                    Row = 0,
                    Col = 0,
                    RowCategoryType = CategoryPairingRules.Country,
                    RowCategoryValue = "France",
                    ColCategoryType = CategoryPairingRules.Club,
                    ColCategoryValue = "Arsenal",
                },
            ],
        });

        var player = new Player { Id = Guid.NewGuid(), FullName = "Misfit Player", WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        dbContext.Players.Add(player);
        dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" });
        dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Barcelona" });

        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GridGameModule.XGGridGameKey,
            GameInstanceId = instanceId,
            StartTime = DateTime.UtcNow.AddDays(-1),
            EndTime = DateTime.UtcNow.AddDays(1),
            AllowGuessChange = true,
        };
        dbContext.Rounds.Add(round);

        await dbContext.SaveChangesAsync();
        return (round.Id, cellId, player.Id, player.FullName);
    }

    private HttpClient CreateAdminClient() => CreateAuthenticatedClient(AdminAuthProviderUserId);

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- Admin policy guardrails (Admin__UserIds) --------------------------

    [Test]
    public async Task AdminEndpoint_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/admin/player-data/unverified");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task AdminEndpoint_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync("/admin/player-data/unverified");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // ---- REQ-503: review of unverified data --------------------------------

    [Test]
    public async Task REQ503_GetUnverifiedPlayerData_ReturnsSourceAndConfidence()
    {
        var playerId = await SeedPlayerAsync();
        await SeedUnverifiedPlayerDataAsync(playerId);
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/player-data/unverified");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<List<UnverifiedPlayerDataResponse>>();
        Assert.That(body, Is.Not.Null);
        var row = body!.Single(r => r.PlayerId == playerId);
        Assert.That(row.Source, Is.EqualTo("wikidata"));
        Assert.That(row.Confidence, Is.EqualTo("unverified"));
    }

    // ---- REQ-501: PlayerOverride CRUD --------------------------------------

    [Test]
    public async Task CreatePlayerOverride_ReturnsBadRequest_ForMissingField()
    {
        var playerId = await SeedPlayerAsync();
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/admin/player-overrides", new CreatePlayerOverrideRequest(playerId, "", "Arsenal", "Manual correction"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task CreatePlayerOverride_ReturnsNotFound_ForUnknownPlayerId()
    {
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/admin/player-overrides", new CreatePlayerOverrideRequest(Guid.NewGuid(), "club", "Arsenal", "Manual correction"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task CreatePlayerOverride_ReturnsConflict_WhenOverrideAlreadyExistsForField()
    {
        var playerId = await SeedPlayerAsync();
        await SeedOverrideAsync(playerId);
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/admin/player-overrides", new CreatePlayerOverrideRequest(playerId, "club", "Barcelona", "Different correction"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task CreatePlayerOverride_PersistsRow_AndReturnsCreated()
    {
        var playerId = await SeedPlayerAsync();
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/admin/player-overrides", new CreatePlayerOverrideRequest(playerId, "club", "Arsenal", "Manual correction"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var body = await response.Content.ReadFromJsonAsync<PlayerOverrideResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.PlayerId, Is.EqualTo(playerId));
        Assert.That(body.Value, Is.EqualTo("Arsenal"));
        Assert.That(body.LockedByAdminId, Is.EqualTo(AdminAuthProviderUserId));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.PlayerOverrides.CountAsync(o => o.PlayerId == playerId), Is.EqualTo(1));
    }

    [Test]
    public async Task GetPlayerOverride_ReturnsNotFound_ForUnknownId()
    {
        var client = CreateAdminClient();

        var response = await client.GetAsync($"/admin/player-overrides/{Guid.NewGuid()}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task GetPlayerOverride_ReturnsIt_WhenFound()
    {
        var playerId = await SeedPlayerAsync();
        var overrideId = await SeedOverrideAsync(playerId);
        var client = CreateAdminClient();

        var response = await client.GetAsync($"/admin/player-overrides/{overrideId}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<PlayerOverrideResponse>();
        Assert.That(body!.Id, Is.EqualTo(overrideId));
    }

    [Test]
    public async Task UpdatePlayerOverride_ReturnsBadRequest_ForMissingValue()
    {
        var playerId = await SeedPlayerAsync();
        var overrideId = await SeedOverrideAsync(playerId);
        var client = CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"/admin/player-overrides/{overrideId}", new UpdatePlayerOverrideRequest("", "Reason"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task UpdatePlayerOverride_ReturnsNotFound_ForUnknownId()
    {
        var client = CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"/admin/player-overrides/{Guid.NewGuid()}", new UpdatePlayerOverrideRequest("Barcelona", "Corrected again"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UpdatePlayerOverride_PersistsChange()
    {
        var playerId = await SeedPlayerAsync();
        var overrideId = await SeedOverrideAsync(playerId);
        var client = CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"/admin/player-overrides/{overrideId}", new UpdatePlayerOverrideRequest("Barcelona", "Corrected again"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<PlayerOverrideResponse>();
        Assert.That(body!.Value, Is.EqualTo("Barcelona"));
        Assert.That(body.Reason, Is.EqualTo("Corrected again"));
    }

    [Test]
    public async Task DeletePlayerOverride_ReturnsNotFound_ForUnknownId()
    {
        var client = CreateAdminClient();

        var response = await client.DeleteAsync($"/admin/player-overrides/{Guid.NewGuid()}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DeletePlayerOverride_RemovesRow_AndReturnsNoContent()
    {
        var playerId = await SeedPlayerAsync();
        var overrideId = await SeedOverrideAsync(playerId);
        var client = CreateAdminClient();

        var response = await client.DeleteAsync($"/admin/player-overrides/{overrideId}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.PlayerOverrides.AnyAsync(o => o.Id == overrideId), Is.False);
    }

    // ---- REQ-501: manual override always wins ------------------------------

    [Test]
    public async Task REQ501_CreatePlayerOverride_FlipsCellCorrectness_ForSubsequentGuess()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedGuessingUserAsync(authProviderUserId);
        var (roundId, cellId, playerId, playerFullName) = await SeedRoundWithCellAndMisfitPlayerAsync();
        var guessingClient = CreateAuthenticatedClient(authProviderUserId);
        var adminClient = CreateAdminClient();

        var before = await guessingClient.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(playerFullName));

        Assert.That(before.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var beforeBody = await before.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(beforeBody, Is.Not.Null);
        Assert.That(beforeBody!.IsCorrect, Is.False, "player's cached club (Barcelona) does not satisfy the cell's club=Arsenal requirement before any override exists");

        var overrideResponse = await adminClient.PostAsJsonAsync(
            "/admin/player-overrides", new CreatePlayerOverrideRequest(playerId, "club", "Arsenal", "Corrected: player actually plays for Arsenal"));

        Assert.That(overrideResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        var after = await guessingClient.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(playerFullName));

        Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var afterBody = await after.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(afterBody, Is.Not.Null);
        Assert.That(afterBody!.IsCorrect, Is.True, "REQ-501/ADR-0015: an admin override must flip the same cell/guess from incorrect to correct, replacing the entire 'club' attribute type for this player");
    }

    [Test]
    public async Task REQ501_CreatePlayerOverride_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var playerId = await SeedPlayerAsync();
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.PostAsJsonAsync(
            "/admin/player-overrides", new CreatePlayerOverrideRequest(playerId, "club", "Arsenal", "Manual correction"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XGArcade.Api.Admin;
using XGArcade.Api.Auth;
using XGArcade.Api.Guesses;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;
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
    // REQ-513 (GitHub issue #239): swapped in for the real WikidataClient so
    // no test in this file ever makes a real network call — same
    // "local fake, swapped via RemoveAll+AddSingleton" precedent as
    // AdminSuggestionEndpointTests.FakeWikidataClient, but with
    // QueryPlayerRefreshDataByQidAsync (rather than
    // QueryPlayerCareerAndNationalityByNameAsync) as the one meaningfully
    // implemented member.
    private FakeWikidataClient _fakeWikidataClient = null!;

    [SetUp]
    public void SetUp()
    {
        _fakeWikidataClient = new FakeWikidataClient();

        // Generated once, OUTSIDE the WithWebHostBuilder lambda below, and
        // captured by closure — bug fix (issue #239 CI failure): a derived
        // factory built later off `_factory` via a second `.WithWebHostBuilder(...)`
        // call (`CreateAdminClientWithLogging`/`CreateAdminClientWithUpdatePlayerCallCounter`
        // below) replays this whole customization delegate again to build its
        // own host. Generating the name INSIDE the lambda (as this used to)
        // meant each such derived factory got its own fresh, empty in-memory
        // database, disconnected from whatever `_factory`'s own DbContext had
        // already seeded — every REQ-513 test using either of those two
        // helpers got a spurious 404 for a player that was, in fact, seeded.
        // Same "generate once outside the lambda" shape as
        // AdminSuggestionEndpointTests.cs's own SetUp, which is exactly why
        // its sibling `CreateAdminClientWithLogging` usage never hit this.
        var inMemoryDatabaseName = Guid.NewGuid().ToString();

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

                    services.AddDbContext<XGArcadeDbContext>(options =>
                        options.UseInMemoryDatabase(inMemoryDatabaseName));

                    services.RemoveAll<IWikidataClient>();
                    services.AddSingleton<IWikidataClient>(_fakeWikidataClient);
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

    // Returns the seeded row's own Id — REQ-503's approve tests (2026-07-20
    // extension) target a specific PlayerData row, not just a player.
    private async Task<Guid> SeedUnverifiedPlayerDataAsync(Guid playerId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var data = new PlayerData
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Field = "club",
            Value = "Arsenal",
            Source = "wikidata",
            Confidence = "unverified",
            SyncedAt = DateTime.UtcNow,
        };
        dbContext.PlayerData.Add(data);
        await dbContext.SaveChangesAsync();
        return data.Id;
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
            SequenceNumber = 1,
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

    // REQ-513: same "swap in a capturing provider, create a client off that
    // factory" shape as AdminSuggestionEndpointTests.CreateAdminClientWithLogging
    // — used by the audit-trail/failure-logging tests below.
    private HttpClient CreateAdminClientWithLogging(out CapturingLoggerProvider loggerProvider)
    {
        var provider = new CapturingLoggerProvider();
        loggerProvider = provider;
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(provider)));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(AdminAuthProviderUserId));
        return client;
    }

    // quality-architect follow-up (issue #239 review): same "swap in a
    // capturing/counting collaborator, create a client off that factory"
    // shape as CreateAdminClientWithLogging above, but wrapping the REAL
    // PlayerRepository (still backed by this test's own in-memory
    // XGArcadeDbContext — never a hand-rolled fake repository, so the actual
    // EF read/write path under test is unchanged) in a thin call-counting
    // decorator, so a test can observe whether UpdatePlayerAsync was
    // actually invoked rather than only re-reading the final stored value
    // (which a same-value rewrite would also produce unchanged).
    private HttpClient CreateAdminClientWithUpdatePlayerCallCounter(out UpdatePlayerCallCounter counter)
    {
        var counterInstance = new UpdatePlayerCallCounter();
        counter = counterInstance;
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPlayerRepository>();
                services.AddScoped<IPlayerRepository>(sp =>
                    new CallCountingPlayerRepository(
                        new PlayerRepository(sp.GetRequiredService<XGArcadeDbContext>()), counterInstance));
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(AdminAuthProviderUserId));
        return client;
    }

    // REQ-513: a player with all four refreshable scalar fields populated,
    // plus a real WikidataQid to refresh from — the common starting point
    // for the refresh-from-wikidata tests below. Field values are
    // deliberately distinct from any value SetRefreshData below configures,
    // so a test can choose per-field whether Wikidata's fresh value should
    // differ, match, or be absent.
    private async Task<Guid> SeedPlayerForRefreshAsync(
        string wikidataQid = "Q188207",
        string fullName = "Clarnce Seedorf",
        string? position = "midfielder",
        int? birthYear = 1976,
        string? photoUrl = "https://example.com/old-photo.jpg")
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = new Player
        {
            Id = Guid.NewGuid(),
            FullName = fullName,
            WikidataQid = wikidataQid,
            Position = position,
            BirthYear = birthYear,
            PhotoUrl = photoUrl,
        };
        dbContext.Players.Add(player);
        await dbContext.SaveChangesAsync();
        return player.Id;
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

    // Regression: the endpoint used to resolve PlayerFullName with one
    // GetPlayerByIdAsync call per row inside a loop — correct for a single
    // row, but an N+1 query storm against real Wikidata-sync volume
    // (thousands of unverified rows) that made this endpoint hang once
    // S-026 gave it a real UI caller. Now resolves every row's player in
    // one batched GetPlayersByIdsAsync call; this asserts multiple distinct
    // players' names still resolve correctly under the batched path, not
    // just a single-row case that a broken batch lookup could still pass.
    [Test]
    public async Task REQ503_GetUnverifiedPlayerData_ResolvesEachRowsPlayerFullName_ForMultipleDistinctPlayers()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var henry = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = $"Q-{Guid.NewGuid()}" };
            var pires = new Player { Id = Guid.NewGuid(), FullName = "Robert Pires", WikidataQid = $"Q-{Guid.NewGuid()}" };
            dbContext.Players.AddRange(henry, pires);
            dbContext.PlayerData.AddRange(
                new PlayerData { Id = Guid.NewGuid(), PlayerId = henry.Id, Field = "club", Value = "Arsenal", Source = "wikidata", Confidence = "unverified", SyncedAt = DateTime.UtcNow },
                new PlayerData { Id = Guid.NewGuid(), PlayerId = pires.Id, Field = "club", Value = "Arsenal", Source = "wikidata", Confidence = "unverified", SyncedAt = DateTime.UtcNow });
            await dbContext.SaveChangesAsync();
        }
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/player-data/unverified");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<List<UnverifiedPlayerDataResponse>>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Select(r => r.PlayerFullName), Is.EquivalentTo(new[] { "Thierry Henry", "Robert Pires" }));
    }

    // ---- REQ-503 (2026-07-20 extension): approve action -------------------

    [Test]
    public async Task REQ503_ApprovePlayerData_SingleRow_FlipsConfidenceToVerified_NoReasonRequired()
    {
        var playerId = await SeedPlayerAsync();
        var dataId = await SeedUnverifiedPlayerDataAsync(playerId);
        var client = CreateAdminClient();

        // No `reason` field in the request body at all — unlike
        // CreatePlayerOverrideRequest, which requires one.
        var response = await client.PostAsJsonAsync("/admin/player-data/approve", new ApprovePlayerDataRequest([dataId]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ApprovePlayerDataResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Results, Has.Count.EqualTo(1));
        Assert.That(body.Results[0].PlayerDataId, Is.EqualTo(dataId));
        Assert.That(body.Results[0].Approved, Is.True);
        Assert.That(body.Results[0].FailureReason, Is.Null);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var stored = await dbContext.PlayerData.SingleAsync(pd => pd.Id == dataId);
        Assert.That(stored.Confidence, Is.EqualTo("verified"));
        Assert.That(stored.ApprovedByAdminId, Is.EqualTo(AdminAuthProviderUserId));
        Assert.That(stored.ApprovedAt, Is.Not.Null);
    }

    [Test]
    public async Task REQ503_ApprovePlayerData_Bulk_SelectAll_ApprovesEveryRow_EachLoggedIndividually()
    {
        var playerId = await SeedPlayerAsync();
        var firstId = await SeedUnverifiedPlayerDataAsync(playerId);
        Guid secondId;
        using (var seedScope = _factory.Services.CreateScope())
        {
            var seedDbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var second = new PlayerData
            {
                Id = Guid.NewGuid(), PlayerId = playerId, Field = "nationality", Value = "France",
                Source = "wikidata", Confidence = "unverified", SyncedAt = DateTime.UtcNow,
            };
            seedDbContext.PlayerData.Add(second);
            await seedDbContext.SaveChangesAsync();
            secondId = second.Id;
        }
        var client = CreateAdminClient();

        // Simulates a "select all" bulk submission over every row currently
        // loaded in the review view.
        var response = await client.PostAsJsonAsync("/admin/player-data/approve", new ApprovePlayerDataRequest([firstId, secondId]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ApprovePlayerDataResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Results, Has.Count.EqualTo(2));
        Assert.That(body.Results, Has.All.Matches<PlayerDataApprovalResult>(r => r.Approved));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var rows = await dbContext.PlayerData.Where(pd => pd.Id == firstId || pd.Id == secondId).ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows, Has.All.Matches<PlayerData>(pd => pd.Confidence == "verified" && pd.ApprovedByAdminId == AdminAuthProviderUserId));
    }

    [Test]
    public async Task REQ503_ApprovePlayerData_Bulk_PartialFailure_ReportsWhichRowsSucceededAndWhichFailed()
    {
        var playerId = await SeedPlayerAsync();
        var validId = await SeedUnverifiedPlayerDataAsync(playerId);
        // Simulates a row deleted (or already changed) by another admin
        // between selection and submission.
        var missingId = Guid.NewGuid();
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync("/admin/player-data/approve", new ApprovePlayerDataRequest([validId, missingId]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "a partial failure must not fail the whole batch as one all-or-nothing unit");
        var body = await response.Content.ReadFromJsonAsync<ApprovePlayerDataResponse>();
        Assert.That(body, Is.Not.Null);
        var validResult = body!.Results.Single(r => r.PlayerDataId == validId);
        var missingResult = body.Results.Single(r => r.PlayerDataId == missingId);
        Assert.That(validResult.Approved, Is.True);
        Assert.That(missingResult.Approved, Is.False);
        Assert.That(missingResult.FailureReason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task REQ503_ApprovePlayerData_ReturnsBadRequest_ForEmptyIdList()
    {
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync("/admin/player-data/approve", new ApprovePlayerDataRequest([]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task REQ503_ApprovePlayerData_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var playerId = await SeedPlayerAsync();
        var dataId = await SeedUnverifiedPlayerDataAsync(playerId);
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.PostAsJsonAsync("/admin/player-data/approve", new ApprovePlayerDataRequest([dataId]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // ---- REQ-503 (2026-07-20 extension): remove action ---------------------

    [Test]
    public async Task REQ503_RemovePlayerData_SingleRow_DeletesRow()
    {
        var playerId = await SeedPlayerAsync();
        var dataId = await SeedUnverifiedPlayerDataAsync(playerId);
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync("/admin/player-data/remove", new RemovePlayerDataRequest([dataId]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<RemovePlayerDataResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Results, Has.Count.EqualTo(1));
        Assert.That(body.Results[0].PlayerDataId, Is.EqualTo(dataId));
        Assert.That(body.Results[0].Removed, Is.True);
        Assert.That(body.Results[0].FailureReason, Is.Null);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.PlayerData.AnyAsync(pd => pd.Id == dataId), Is.False);
    }

    [Test]
    public async Task REQ503_RemovePlayerData_Bulk_SelectAll_RemovesEveryRow()
    {
        var playerId = await SeedPlayerAsync();
        var firstId = await SeedUnverifiedPlayerDataAsync(playerId);
        Guid secondId;
        using (var seedScope = _factory.Services.CreateScope())
        {
            var seedDbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var second = new PlayerData
            {
                Id = Guid.NewGuid(), PlayerId = playerId, Field = "nationality", Value = "France",
                Source = "wikidata", Confidence = "unverified", SyncedAt = DateTime.UtcNow,
            };
            seedDbContext.PlayerData.Add(second);
            await seedDbContext.SaveChangesAsync();
            secondId = second.Id;
        }
        var client = CreateAdminClient();

        // Simulates a "select all" bulk submission over every row currently
        // loaded in the review view.
        var response = await client.PostAsJsonAsync("/admin/player-data/remove", new RemovePlayerDataRequest([firstId, secondId]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<RemovePlayerDataResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Results, Has.Count.EqualTo(2));
        Assert.That(body.Results, Has.All.Matches<PlayerDataRemovalResult>(r => r.Removed));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.PlayerData.AnyAsync(pd => pd.Id == firstId || pd.Id == secondId), Is.False);
    }

    [Test]
    public async Task REQ503_RemovePlayerData_Bulk_PartialFailure_ReportsWhichRowsSucceededAndWhichFailed()
    {
        var playerId = await SeedPlayerAsync();
        var validId = await SeedUnverifiedPlayerDataAsync(playerId);
        // Simulates a row already deleted (or never existed) between
        // selection and submission.
        var missingId = Guid.NewGuid();
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync("/admin/player-data/remove", new RemovePlayerDataRequest([validId, missingId]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "a partial failure must not fail the whole batch as one all-or-nothing unit");
        var body = await response.Content.ReadFromJsonAsync<RemovePlayerDataResponse>();
        Assert.That(body, Is.Not.Null);
        var validResult = body!.Results.Single(r => r.PlayerDataId == validId);
        var missingResult = body.Results.Single(r => r.PlayerDataId == missingId);
        Assert.That(validResult.Removed, Is.True);
        Assert.That(missingResult.Removed, Is.False);
        Assert.That(missingResult.FailureReason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task REQ503_RemovePlayerData_ReturnsBadRequest_ForEmptyIdList()
    {
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync("/admin/player-data/remove", new RemovePlayerDataRequest([]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task REQ503_RemovePlayerData_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var playerId = await SeedPlayerAsync();
        var dataId = await SeedUnverifiedPlayerDataAsync(playerId);
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.PostAsJsonAsync("/admin/player-data/remove", new RemovePlayerDataRequest([dataId]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.PlayerData.AnyAsync(pd => pd.Id == dataId), Is.True, "a rejected request must not remove the row");
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

    // ---- REQ-513 (GitHub issue #239): admin refresh of a Player from -------
    // Wikidata (POST /admin/players/{id}/refresh-from-wikidata) -------------

    [Test]
    public async Task REQ513_RefreshFromWikidata_FieldDiffersFromStored_OverwritesIt_AndReportsChangedWithOldAndNewValue()
    {
        var playerId = await SeedPlayerForRefreshAsync(
            wikidataQid: "Q188207", fullName: "Clarnce Seedorf", position: "midfielder", birthYear: 1976, photoUrl: "https://example.com/old-photo.jpg");
        // Only FullName differs from what's stored — every other field comes
        // back identical, isolating this test to the single-field-changed
        // case (issue #239's own garbled-name scenario).
        _fakeWikidataClient.SetRefreshData("Q188207", new WikidataPlayerRefreshData("Clarence Seedorf", "midfielder", 1976, "https://example.com/old-photo.jpg"));
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/players/{playerId}/refresh-from-wikidata", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<RefreshPlayerFromWikidataResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.PlayerId, Is.EqualTo(playerId));
        Assert.That(body.WikidataQid, Is.EqualTo("Q188207"));
        var fullNameResult = body.Fields.Single(f => f.Field == "fullName");
        Assert.That(fullNameResult.Changed, Is.True);
        Assert.That(fullNameResult.OldValue, Is.EqualTo("Clarnce Seedorf"));
        Assert.That(fullNameResult.NewValue, Is.EqualTo("Clarence Seedorf"));
        Assert.That(body.Fields.Where(f => f.Field != "fullName"), Has.All.Matches<PlayerRefreshFieldResult>(f => !f.Changed && f.NewValue == null),
            "every other field returned an identical Wikidata value and must be reported unchanged");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = await dbContext.Players.AsNoTracking().SingleAsync(p => p.Id == playerId);
        Assert.That(player.FullName, Is.EqualTo("Clarence Seedorf"), "the corrected name must actually be persisted, not just reported");
        // quality-architect follow-up (issue #239): the whole point of this
        // refresh action is fixing a garbled name that makes guess-matching
        // fail (REQ-208 queries NormalizedFullName, never FullName directly —
        // GridNameMatcher/XGPathGameModule). Player.FullName's setter
        // re-derives NormalizedFullName (Player.cs), so persisting through
        // that setter (rather than a raw column write) must leave it in sync
        // with the corrected name, not stale from the garbled one.
        Assert.That(player.NormalizedFullName, Is.EqualTo("clarence seedorf"),
            "NormalizedFullName must be re-derived from the corrected FullName — this is the column guess-matching actually queries (REQ-208), so a stale value here would mean the refresh fixed the display name but not the underlying bug issue #239 was filed for");
    }

    [Test]
    public async Task REQ513_RefreshFromWikidata_NullOrEmptyFetchedValue_LeavesFieldUnchanged_AndReportsUnchanged()
    {
        var playerId = await SeedPlayerForRefreshAsync(
            wikidataQid: "Q188207", fullName: "Clarence Seedorf", position: "midfielder", birthYear: 1976, photoUrl: "https://example.com/old-photo.jpg");
        // Wikidata currently has no P413 (position) binding at all — must
        // never be treated as "wipe the existing stored value" (REQ-513's
        // own "absence is not evidence of wrongness" clause, ADR-0046).
        _fakeWikidataClient.SetRefreshData("Q188207", new WikidataPlayerRefreshData("Clarence Seedorf", null, 1976, "https://example.com/old-photo.jpg"));
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/players/{playerId}/refresh-from-wikidata", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<RefreshPlayerFromWikidataResponse>();
        var positionResult = body!.Fields.Single(f => f.Field == "position");
        Assert.That(positionResult.Changed, Is.False);
        Assert.That(positionResult.OldValue, Is.EqualTo("midfielder"), "OldValue is always reported regardless of Changed");
        Assert.That(positionResult.NewValue, Is.Null);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = await dbContext.Players.AsNoTracking().SingleAsync(p => p.Id == playerId);
        Assert.That(player.Position, Is.EqualTo("midfielder"), "a missing Wikidata binding must never null out the existing stored value");
    }

    [Test]
    public async Task REQ513_RefreshFromWikidata_FetchedValueIdenticalToStored_ReportsUnchanged_AndValueRemainsUnchangedInStorage()
    {
        var playerId = await SeedPlayerForRefreshAsync(
            wikidataQid: "Q188207", fullName: "Clarence Seedorf", position: "midfielder", birthYear: 1976, photoUrl: "https://example.com/old-photo.jpg");
        // Every field comes back byte-for-byte identical to what's already
        // stored — nothing here should ever be (re)written.
        _fakeWikidataClient.SetRefreshData("Q188207", new WikidataPlayerRefreshData("Clarence Seedorf", "midfielder", 1976, "https://example.com/old-photo.jpg"));
        using (var seedScope = _factory.Services.CreateScope())
        {
            var seedDbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            Assert.That((await seedDbContext.Players.AsNoTracking().SingleAsync(p => p.Id == playerId)).FullName, Is.EqualTo("Clarence Seedorf"));
        }
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/players/{playerId}/refresh-from-wikidata", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<RefreshPlayerFromWikidataResponse>();
        Assert.That(body!.Fields, Has.All.Matches<PlayerRefreshFieldResult>(f => !f.Changed),
            "an identical fetched value must be reported unchanged, not silently rewritten and called a change");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = await dbContext.Players.AsNoTracking().SingleAsync(p => p.Id == playerId);
        Assert.That(player.FullName, Is.EqualTo("Clarence Seedorf"));
        Assert.That(player.Position, Is.EqualTo("midfielder"));
        Assert.That(player.BirthYear, Is.EqualTo(1976));
        // quality-architect follow-up (issue #239 review): this only proves
        // the stored VALUE is unchanged after the fact — a same-value
        // rewrite (read, then write back the identical value) would produce
        // an identical read-back too, so this assertion alone can't claim
        // "no write occurred". See the dedicated
        // REQ513_RefreshFromWikidata_FetchedValueIdenticalToStored_NeverInvokesUpdatePlayerAsync
        // test below for the actual write-behavior claim, verified via a
        // call-counting IPlayerRepository decorator.
        Assert.That(player.PhotoUrl, Is.EqualTo("https://example.com/old-photo.jpg"),
            "a full no-op refresh must leave every stored value exactly as it was");
    }

    // quality-architect follow-up (issue #239 review): REQ-513's response
    // contract explicitly promises "only changed fields are written" — a
    // claim about WRITE BEHAVIOR, not just final stored value (which a
    // same-value rewrite would also leave looking unchanged, as the test
    // above's own softened assertion message now says explicitly). This
    // test observes UpdatePlayerAsync invocations directly via a thin
    // call-counting decorator wrapped around the real PlayerRepository
    // (CreateAdminClientWithUpdatePlayerCallCounter) — same "swap a
    // purpose-built collaborator into DI for one test" shape as
    // CreateAdminClientWithLogging's CapturingLoggerProvider above, applied
    // to IPlayerRepository instead of ILoggerProvider.
    [Test]
    public async Task REQ513_RefreshFromWikidata_FetchedValueIdenticalToStored_NeverInvokesUpdatePlayerAsync()
    {
        var playerId = await SeedPlayerForRefreshAsync(
            wikidataQid: "Q188207", fullName: "Clarnce Seedorf", position: "midfielder", birthYear: 1976, photoUrl: "https://example.com/old-photo.jpg");
        var client = CreateAdminClientWithUpdatePlayerCallCounter(out var counter);

        // Phase 1: a genuinely DIFFERENT fetched value. This proves the
        // counter itself actually observes a real UpdatePlayerAsync call
        // (i.e. it isn't just stuck reporting 0 regardless of what happens
        // underneath) before phase 2 below relies on it staying at 0 to mean
        // something.
        _fakeWikidataClient.SetRefreshData("Q188207", new WikidataPlayerRefreshData("Clarence Seedorf", "midfielder", 1976, "https://example.com/old-photo.jpg"));
        var changedResponse = await client.PostAsync($"/admin/players/{playerId}/refresh-from-wikidata", null);
        Assert.That(changedResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(counter.Count, Is.EqualTo(1),
            "sanity check that the call-counting decorator is wired correctly: a genuinely different fetched value must invoke UpdatePlayerAsync exactly once");

        // Phase 2: refresh again, with Wikidata now reporting the SAME value
        // phase 1 just persisted — this is the actual no-op case.
        var noOpResponse = await client.PostAsync($"/admin/players/{playerId}/refresh-from-wikidata", null);

        Assert.That(noOpResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await noOpResponse.Content.ReadFromJsonAsync<RefreshPlayerFromWikidataResponse>();
        Assert.That(body!.Fields, Has.All.Matches<PlayerRefreshFieldResult>(f => !f.Changed));
        Assert.That(counter.Count, Is.EqualTo(1),
            "REQ-513's 'only changed fields are written' promise is about write behavior: an identical-value refresh must not invoke UpdatePlayerAsync a second time at all — not merely leave the final stored value looking unchanged, which a same-value rewrite would also do");
    }

    [Test]
    public async Task REQ513_RefreshFromWikidata_AllFourFieldsDiffer_EachIsIndependentlyDiffedAndPersisted_NotAllOrNothing()
    {
        var playerId = await SeedPlayerForRefreshAsync(
            wikidataQid: "Q188207", fullName: "Clarnce Seedorf", position: "midfielder", birthYear: 1976, photoUrl: "https://example.com/old-photo.jpg");
        _fakeWikidataClient.SetRefreshData("Q188207", new WikidataPlayerRefreshData(
            "Clarence Seedorf", "defender", 1977, "https://example.com/new-photo.jpg"));
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/players/{playerId}/refresh-from-wikidata", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<RefreshPlayerFromWikidataResponse>();
        Assert.That(body!.Fields, Has.Count.EqualTo(4));
        Assert.That(body.Fields, Has.All.Matches<PlayerRefreshFieldResult>(f => f.Changed), "all four fields differ and must each be independently reported as changed");
        Assert.That(body.Fields.Single(f => f.Field == "fullName").NewValue, Is.EqualTo("Clarence Seedorf"));
        Assert.That(body.Fields.Single(f => f.Field == "position").NewValue, Is.EqualTo("defender"));
        Assert.That(body.Fields.Single(f => f.Field == "birthYear").NewValue, Is.EqualTo("1977"));
        Assert.That(body.Fields.Single(f => f.Field == "photoUrl").NewValue, Is.EqualTo("https://example.com/new-photo.jpg"));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = await dbContext.Players.AsNoTracking().SingleAsync(p => p.Id == playerId);
        Assert.That(player.FullName, Is.EqualTo("Clarence Seedorf"));
        Assert.That(player.Position, Is.EqualTo("defender"));
        Assert.That(player.BirthYear, Is.EqualTo(1977));
        Assert.That(player.PhotoUrl, Is.EqualTo("https://example.com/new-photo.jpg"));
    }

    [Test]
    public async Task REQ513_RefreshFromWikidata_ReturnsNotFound_ForUnknownPlayerId()
    {
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/players/{Guid.NewGuid()}/refresh-from-wikidata", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task REQ513_RefreshFromWikidata_ReturnsConflict_WhenPlayerHasNoWikidataQid()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = new Player { Id = Guid.NewGuid(), FullName = "No Qid Player", WikidataQid = null };
        dbContext.Players.Add(player);
        await dbContext.SaveChangesAsync();
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/players/{player.Id}/refresh-from-wikidata", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "there is no WikidataQid to refresh from — this action must never fall back to a name-based search");
    }

    [Test]
    public async Task REQ513_RefreshFromWikidata_ReturnsServiceUnavailable_NeverSilentNoChange_WhenWikidataQueryFails()
    {
        var playerId = await SeedPlayerForRefreshAsync(wikidataQid: "Q188207");
        _fakeWikidataClient.FailNextRefreshLookups(1);
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/players/{playerId}/refresh-from-wikidata", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), "ADR-0046: a failed/timed-out lookup must never be silently treated as 'no fields changed'");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = await dbContext.Players.AsNoTracking().SingleAsync(p => p.Id == playerId);
        Assert.That(player.FullName, Is.EqualTo("Clarnce Seedorf"), "a failed lookup must never touch the stored row");
    }

    [Test]
    public async Task REQ513_RefreshFromWikidata_LogsWarning_WhenWikidataQueryFails()
    {
        var playerId = await SeedPlayerForRefreshAsync(wikidataQid: "Q188207");
        _fakeWikidataClient.FailNextRefreshLookups(1);
        var client = CreateAdminClientWithLogging(out var loggerProvider);

        var response = await client.PostAsync($"/admin/players/{playerId}/refresh-from-wikidata", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(loggerProvider.Entries, Has.Some.Matches<(LogLevel Level, string Message)>(
            e => e.Level == LogLevel.Warning && e.Message.Contains(playerId.ToString()) && e.Message.Contains("Q188207")),
            "the exception must be logged at Warning server-side, with the player id and WikidataQid for later diagnosis — same contract as AdminSuggestionEndpoints' own lookup failures");
    }

    // REQ-513's own "Audit trail" acceptance criterion: logged
    // unconditionally at refresh time, whether or not anything actually
    // changed — Player has no admin-audit columns of its own to record this
    // on instead (unlike PlayerOverride's LockedByAdminId/LockedAt).
    [Test]
    public async Task REQ513_RefreshFromWikidata_LogsAdminIdPlayerIdAndQid_EvenWhenNoFieldChanged()
    {
        var playerId = await SeedPlayerForRefreshAsync(
            wikidataQid: "Q188207", fullName: "Clarence Seedorf", position: "midfielder", birthYear: 1976, photoUrl: "https://example.com/old-photo.jpg");
        _fakeWikidataClient.SetRefreshData("Q188207", new WikidataPlayerRefreshData("Clarence Seedorf", "midfielder", 1976, "https://example.com/old-photo.jpg"));
        var client = CreateAdminClientWithLogging(out var loggerProvider);

        var response = await client.PostAsync($"/admin/players/{playerId}/refresh-from-wikidata", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(loggerProvider.Entries, Has.Some.Matches<(LogLevel Level, string Message)>(
            e => e.Level == LogLevel.Information
                && e.Message.Contains(AdminAuthProviderUserId.ToString())
                && e.Message.Contains(playerId.ToString())
                && e.Message.Contains("Q188207")),
            "REQ-513: the refresh action must be recorded via a structured log line (admin id, player id, WikidataQid) even when no field actually changed — there is no PlayerOverride-style audit row for Player to fall back on");
    }

    [Test]
    public async Task REQ513_RefreshFromWikidata_ReturnsUnauthorized_WithoutBearerToken()
    {
        var playerId = await SeedPlayerForRefreshAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsync($"/admin/players/{playerId}/refresh-from-wikidata", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ513_RefreshFromWikidata_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var playerId = await SeedPlayerForRefreshAsync();
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.PostAsync($"/admin/players/{playerId}/refresh-from-wikidata", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = await dbContext.Players.AsNoTracking().SingleAsync(p => p.Id == playerId);
        Assert.That(player.FullName, Is.EqualTo("Clarnce Seedorf"), "a forbidden request must never reach the write path");
    }

    // ---- Test double for IWikidataClient -----------------------------------
    // Deliberately NOT the DataSync.Tests project's own internal
    // FakeWikidataClient (a different assembly, no InternalsVisibleTo wired
    // between it and this project), and deliberately NOT
    // AdminSuggestionEndpointTests' own private FakeWikidataClient either (a
    // different, unrelated test class in this same project) — a minimal
    // local fake instead, same "local fake, swapped via
    // RemoveAll+AddSingleton" precedent both of those already follow. Only
    // QueryPlayerRefreshDataByQidAsync is meaningfully implemented; every
    // other IWikidataClient member is never called by AdminEndpoints'
    // refresh-from-wikidata action and stays a trivial stub purely to
    // satisfy the interface.
    private sealed class FakeWikidataClient : IWikidataClient
    {
        private readonly Dictionary<string, WikidataPlayerRefreshData> _refreshDataByQid = new();
        private int _remainingRefreshFailures;

        public void SetRefreshData(string wikidataQid, WikidataPlayerRefreshData data) => _refreshDataByQid[wikidataQid] = data;

        public void FailNextRefreshLookups(int calls) => _remainingRefreshFailures = calls;

        public Task<WikidataPlayerRefreshData> QueryPlayerRefreshDataByQidAsync(
            string wikidataQid, CancellationToken cancellationToken = default)
        {
            if (_remainingRefreshFailures > 0)
            {
                _remainingRefreshFailures--;
                throw new WikidataQueryException("simulated WDQS failure for admin player refresh");
            }

            var result = _refreshDataByQid.TryGetValue(wikidataQid, out var configured)
                ? configured
                : new WikidataPlayerRefreshData(null, null, null, null);
            return Task.FromResult(result);
        }

        public Task<WikidataPlayerCareerLookupResult?> QueryPlayerCareerAndNationalityByNameAsync(
            string playerName, CancellationToken cancellationToken = default) =>
            Task.FromResult<WikidataPlayerCareerLookupResult?>(null);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
            string countryWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryNationalTeamClubIntersectionAsync(
            string nationalTeamWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryClubClubIntersectionAsync(
            string clubAWikidataQid, string clubBWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyCountryIntersectionAsync(
            string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyClubIntersectionAsync(
            string trophyWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyCountryIntersectionAsync(
            string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyNationalTeamIntersectionAsync(
            string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyClubIntersectionAsync(
            string trophyWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyNationalTeamIntersectionAsync(
            string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolBirthYearAsync(
            int birthYear, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

        public Task<IReadOnlyDictionary<string, string>> QueryPlayerPhotosByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<IReadOnlyDictionary<string, PlayerPositionBirthYearEntry>> QueryPlayerPositionsAndBirthYearsByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, PlayerPositionBirthYearEntry>>(new Dictionary<string, PlayerPositionBirthYearEntry>());

        public Task<IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>>> QueryPlayerCareerStintsByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>>>(new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>());

        public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByNationalityAsync(
            string nationalityWikidataQid, bool useCountryForSportProperty, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

        public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByClubAsync(
            string clubWikidataQid, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

        public Task<IReadOnlyDictionary<string, int>> QuerySitelinkCountsByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

        public Task<WikidataPlayerPhotoLookupResult?> QueryPlayerPhotoByNameAsync(
            string playerName, CancellationToken cancellationToken = default) =>
            Task.FromResult<WikidataPlayerPhotoLookupResult?>(null);
    }

    // ---- Test double for IPlayerRepository (call-counting decorator) ------
    // quality-architect follow-up (issue #239 review): a spy over the REAL
    // PlayerRepository, not a hand-rolled fake repository — every read/write
    // still goes through the actual EF Core path against this test's own
    // in-memory XGArcadeDbContext, this decorator only observes whether
    // UpdatePlayerAsync was invoked. Thread-safety (Interlocked.Increment)
    // is not load-bearing for these single-request tests but costs nothing
    // and avoids being a footgun if a future test parallelizes calls through
    // the same counter.
    private sealed class UpdatePlayerCallCounter
    {
        private int _count;
        public int Count => _count;
        public void Increment() => Interlocked.Increment(ref _count);
    }

    // Delegates every member to `inner` unchanged except UpdatePlayerAsync,
    // which increments `counter` before delegating — deliberately NOT a
    // reimplementation of PlayerRepository's own logic (that would test the
    // decorator instead of the real repository).
    private sealed class CallCountingPlayerRepository(IPlayerRepository inner, UpdatePlayerCallCounter counter) : IPlayerRepository
    {
        public Task<Player?> GetPlayerByWikidataQidAsync(string wikidataQid, CancellationToken cancellationToken = default) =>
            inner.GetPlayerByWikidataQidAsync(wikidataQid, cancellationToken);

        public Task<Player?> GetPlayerByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetPlayerByIdAsync(id, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, Player>> GetPlayersByIdsAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            inner.GetPlayersByIdsAsync(ids, cancellationToken);

        public Task<Player> AddPlayerAsync(Player player, CancellationToken cancellationToken = default) =>
            inner.AddPlayerAsync(player, cancellationToken);

        public Task<IReadOnlyDictionary<string, PlayerCreationResult>> GetOrCreatePlayersByWikidataQidAsync(
            IReadOnlyList<PlayerCreationRequest> requests, CancellationToken cancellationToken = default) =>
            inner.GetOrCreatePlayersByWikidataQidAsync(requests, cancellationToken);

        public Task<IReadOnlyList<Player>> GetPlayersByNormalizedFullNameAsync(
            string normalizedFullName, CancellationToken cancellationToken = default) =>
            inner.GetPlayersByNormalizedFullNameAsync(normalizedFullName, cancellationToken);

        public Task<Player?> GetPlayerForRefreshAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetPlayerForRefreshAsync(id, cancellationToken);

        public Task UpdatePlayerAsync(Player player, CancellationToken cancellationToken = default)
        {
            counter.Increment();
            return inner.UpdatePlayerAsync(player, cancellationToken);
        }
    }
}

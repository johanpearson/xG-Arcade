using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Admin;
using XGArcade.Api.Auth;
using XGArcade.Core.Rounds;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Tests;

// S-026 (docs/backlog.md): API-level coverage for the non-Production-only
// admin round control (REQ-505: GET/POST/PUT under /admin/rounds/{gameKey})
// and admin user deletion (REQ-506: DELETE /admin/users) endpoints in
// AdminManagementEndpoints.cs. Same "Admin" authorization policy
// (Admin__UserIds) as AdminEndpointTests.cs, plus the fail-closed
// "route absent entirely in Production" discipline RoundEndpointTests.cs
// established for REQ-806/807.
public class AdminManagementEndpointTests
{
    // Fixed so every test can configure the same "this is an admin" identity
    // via Admin:UserIds without re-creating the factory per test.
    private static readonly Guid AdminAuthProviderUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Same in-process HS256 signer/validator as AdminEndpointTests
                // — see that file's SetUp comment for why (ADR-0017).
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

    // Round has no foreign key to GridInstance (ADR-0003) — GameInstanceId
    // is an opaque id here, same as RoundEndpointTests' generated rounds,
    // so a plain Guid.NewGuid() is enough; no GridInstance/GridCell needs
    // to exist for these admin round-control endpoints to operate.
    private async Task<Round> SeedActiveRoundAsync(DateTime? startTime = null, DateTime? endTime = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GridGameModule.XGGridGameKey,
            GameInstanceId = Guid.NewGuid(),
            StartTime = startTime ?? DateTime.UtcNow.AddDays(-1),
            EndTime = endTime ?? DateTime.UtcNow.AddDays(1),
            AllowGuessChange = false,
        };
        dbContext.Rounds.Add(round);
        await dbContext.SaveChangesAsync();
        return round;
    }

    private async Task<User> SeedDeletableUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = email,
            DisplayName = $"Player-{Guid.NewGuid():N}",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    // Same shape as AuthEndpointTests/AccountDeletionServiceTests' own
    // SeedGuessAsync — needed here to assert REQ-506 reuses REQ-710's exact
    // anonymize-don't-hard-delete behavior, not a second, independently
    // written deletion path.
    private async Task SeedGuessAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        dbContext.Guesses.Add(new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = Guid.NewGuid(),
            UserId = userId,
            CellId = Guid.NewGuid(),
            SubmittedName = "Someone",
            IsCorrect = true,
            AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private HttpClient CreateAdminClient() => CreateAuthenticatedClient(AdminAuthProviderUserId);

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // Sets process environment variables for the duration of one test,
    // restoring each to its original value (including "unset") on dispose —
    // same helper as RoundEndpointTests, duplicated here rather than shared
    // since that file must not be modified for this story.
    private static IDisposable TemporaryEnvironmentVariables(params (string Name, string Value)[] variables)
    {
        var originalValues = variables.Select(v => (v.Name, Original: Environment.GetEnvironmentVariable(v.Name))).ToList();
        foreach (var (name, value) in variables)
            Environment.SetEnvironmentVariable(name, value);

        return new RestoreEnvironmentVariables(originalValues);
    }

    private sealed class RestoreEnvironmentVariables(List<(string Name, string? Original)> originalValues) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (name, original) in originalValues)
                Environment.SetEnvironmentVariable(name, original);
        }
    }

    // Program.cs reads several required config values (connection string,
    // Supabase settings) eagerly, before WebApplicationFactory's
    // ConfigureAppConfiguration/UseEnvironment hooks can take effect — see
    // RoundEndpointTests' ForceCloseRound_Post_IsNeverRegistered_WhenEnvironmentIsProduction
    // for the full explanation. Real process environment variables are the
    // only override visible early enough to genuinely flip which environment
    // this host starts under.
    private IDisposable EnterProductionEnvironment() =>
        TemporaryEnvironmentVariables(
            ("ASPNETCORE_ENVIRONMENT", "Production"),
            ("ConnectionStrings__Database", "Host=localhost;Database=unused-in-tests;Username=postgres;Password=postgres"),
            ("Supabase__Url", "http://localhost:54321"),
            ("Supabase__AnonKey", "test-placeholder-anon-key"),
            ("Supabase__ServiceRoleKey", "test-placeholder-service-role-key"));

    // ---- REQ-505: GET /admin/rounds/{gameKey}/active -----------------------

    [Test]
    public async Task REQ505_ActiveRound_Get_ReturnsHasActiveRoundTrue_WhenARoundIsCurrentlyActive()
    {
        var round = await SeedActiveRoundAsync();
        var client = CreateAdminClient();

        var response = await client.GetAsync($"/admin/rounds/{GridGameModule.XGGridGameKey}/active");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AdminActiveRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.HasActiveRound, Is.True);
        Assert.That(body.Round, Is.Not.Null);
        Assert.That(body.Round!.RoundId, Is.EqualTo(round.Id));
        Assert.That(body.Round.GameKey, Is.EqualTo(GridGameModule.XGGridGameKey));
    }

    [Test]
    public async Task REQ505_ActiveRound_Get_ReturnsHasActiveRoundFalse_WhenNoRoundIsActive()
    {
        var client = CreateAdminClient();

        var response = await client.GetAsync($"/admin/rounds/{GridGameModule.XGGridGameKey}/active");

        // Deliberately still 200, not 404 — the endpoint's own doc comment
        // explains this is the frontend's only reliable way to tell "no
        // active round right now" apart from "this route doesn't exist here
        // at all" (a real 404 from routing itself, only possible in
        // Production). Asserting the body's shape here, not just the status
        // code, catches a regression that silently reintroduces a bare 404.
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AdminActiveRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.HasActiveRound, Is.False);
        Assert.That(body.Round, Is.Null);
    }

    [Test]
    public async Task ActiveRound_Get_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync($"/admin/rounds/{GridGameModule.XGGridGameKey}/active");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task ActiveRound_Get_IsNeverRegistered_WhenEnvironmentIsProduction()
    {
        using var _ = EnterProductionEnvironment();

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.GetAsync($"/admin/rounds/{GridGameModule.XGGridGameKey}/active");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- REQ-505: POST /admin/rounds/{gameKey}/close -----------------------

    [Test]
    public async Task REQ505_CloseRound_Post_ClosesTheActiveRoundImmediately_ForAnAdmin()
    {
        var round = await SeedActiveRoundAsync();
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/rounds/{GridGameModule.XGGridGameKey}/close", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AdminRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.EndTime, Is.LessThan(round.EndTime), "closing before the round's real end_time must pull it forward");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var persisted = await dbContext.Rounds.SingleAsync(r => r.Id == round.Id);
        Assert.That(persisted.GetStatus(DateTime.UtcNow), Is.EqualTo(RoundStatus.Closed));
    }

    [Test]
    public async Task CloseRound_Post_ReturnsNotFound_WhenNoActiveRoundExists()
    {
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/rounds/{GridGameModule.XGGridGameKey}/close", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task CloseRound_Post_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        await SeedActiveRoundAsync();
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.PostAsync($"/admin/rounds/{GridGameModule.XGGridGameKey}/close", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task CloseRound_Post_IsNeverRegistered_WhenEnvironmentIsProduction()
    {
        using var _ = EnterProductionEnvironment();

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.PostAsync($"/admin/rounds/{GridGameModule.XGGridGameKey}/close", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- REQ-505: PUT /admin/rounds/{gameKey}/end-time ---------------------

    [Test]
    public async Task REQ505_UpdateEndTime_Put_UpdatesTheActiveRoundsEndTime_ForAnAdmin()
    {
        var round = await SeedActiveRoundAsync();
        var newEndTime = DateTime.UtcNow.AddDays(5);
        var client = CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"/admin/rounds/{GridGameModule.XGGridGameKey}/end-time", new UpdateRoundEndTimeRequest(newEndTime));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AdminRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.RoundId, Is.EqualTo(round.Id));
        Assert.That(body.EndTime, Is.EqualTo(newEndTime).Within(TimeSpan.FromSeconds(1)));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var persisted = await dbContext.Rounds.AsNoTracking().SingleAsync(r => r.Id == round.Id);
        Assert.That(persisted.EndTime, Is.EqualTo(newEndTime).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task REQ505_UpdateEndTime_Put_ReturnsBadRequest_WhenNewEndTimeIsBeforeStartTime()
    {
        var round = await SeedActiveRoundAsync(startTime: DateTime.UtcNow.AddDays(-1), endTime: DateTime.UtcNow.AddDays(1));
        var client = CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"/admin/rounds/{GridGameModule.XGGridGameKey}/end-time", new UpdateRoundEndTimeRequest(round.StartTime.AddHours(-1)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Invalid end time"));
    }

    [Test]
    public async Task REQ505_UpdateEndTime_Put_ReturnsBadRequest_WhenNewEndTimeIsNotInTheFuture()
    {
        await SeedActiveRoundAsync(startTime: DateTime.UtcNow.AddDays(-1), endTime: DateTime.UtcNow.AddDays(1));
        var client = CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"/admin/rounds/{GridGameModule.XGGridGameKey}/end-time", new UpdateRoundEndTimeRequest(DateTime.UtcNow.AddMinutes(-1)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Invalid end time"));
    }

    [Test]
    public async Task UpdateEndTime_Put_ReturnsNotFound_WhenNoActiveRoundExists()
    {
        var client = CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"/admin/rounds/{GridGameModule.XGGridGameKey}/end-time", new UpdateRoundEndTimeRequest(DateTime.UtcNow.AddDays(5)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UpdateEndTime_Put_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        await SeedActiveRoundAsync();
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.PutAsJsonAsync(
            $"/admin/rounds/{GridGameModule.XGGridGameKey}/end-time", new UpdateRoundEndTimeRequest(DateTime.UtcNow.AddDays(5)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task UpdateEndTime_Put_IsNeverRegistered_WhenEnvironmentIsProduction()
    {
        using var _ = EnterProductionEnvironment();

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.PutAsJsonAsync(
            $"/admin/rounds/{GridGameModule.XGGridGameKey}/end-time", new UpdateRoundEndTimeRequest(DateTime.UtcNow.AddDays(5)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- REQ-506: DELETE /admin/users ---------------------------------------

    [Test]
    public async Task REQ506_DeleteUser_ReturnsNoContent_AndAnonymizesGuessesAndRemovesTheUser_ForAnAdmin()
    {
        var user = await SeedDeletableUserAsync("delete-me@example.com");
        await SeedGuessAsync(user.Id);
        var client = CreateAdminClient();

        // Safe: SeedDeletableUserAsync always sets a real email string — the
        // null-forgiving operator here is only about User.Email's REQ-717
        // nullability (a guest has none), never true for this seeded row.
        var response = await client.DeleteAsync($"/admin/users?email={Uri.EscapeDataString(user.Email!)}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var remainingUser = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == user.Id);
        Assert.That(remainingUser, Is.Null);

        // REQ-710's anonymize-don't-hard-delete contract, reused as-is: the
        // Guess row survives (other players' uniqueness scores depend on the
        // total guess count staying intact) with its UserId severed.
        var guess = await dbContext.Guesses.AsNoTracking().SingleAsync(g => g.SubmittedName == "Someone");
        Assert.That(guess.UserId, Is.Null);
    }

    [Test]
    public async Task DeleteUser_ReturnsNotFound_ForAnUnknownEmail()
    {
        var client = CreateAdminClient();

        var response = await client.DeleteAsync($"/admin/users?email={Uri.EscapeDataString("no-such-user@example.com")}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DeleteUser_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var user = await SeedDeletableUserAsync("forbidden-target@example.com");
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        // Safe: SeedDeletableUserAsync always sets a real email string — the
        // null-forgiving operator here is only about User.Email's REQ-717
        // nullability (a guest has none), never true for this seeded row.
        var response = await client.DeleteAsync($"/admin/users?email={Uri.EscapeDataString(user.Email!)}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task DeleteUser_IsNeverRegistered_WhenEnvironmentIsProduction()
    {
        using var _ = EnterProductionEnvironment();

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.DeleteAsync($"/admin/users?email={Uri.EscapeDataString("whoever@example.com")}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}

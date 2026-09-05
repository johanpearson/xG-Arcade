using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Admin;
using XGArcade.Api.Auth;
using XGArcade.Api.Connect;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Tests;

// REQ-1414 (docs/requirements-document.md §4.15), ADR-0053: API-level
// coverage for GET /admin/connect-dispute-suggestions. Same in-memory-
// DbContext-swap/local-e2e-auth/Admin:UserIds pattern as
// AdminSuggestionEndpointTests — a minimal, wiring-level suite (an approved
// dispute's suggestion is visible to an admin; a non-admin is rejected).
// The full dispute raise/review flow that PRODUCES a suggestion is already
// covered end to end at the service level
// (ConnectChainStepDisputeServiceTests.cs's own REQ-1414 tests) — this file
// seeds the suggestion directly via the repository rather than replaying
// that whole flow again.
public class AdminConnectDisputeSuggestionEndpointTests
{
    private static readonly Guid AdminAuthProviderUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Auth:Mode", "local-e2e");
                builder.UseSetting("Admin:UserIds", AdminAuthProviderUserId.ToString());

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

                    services.AddDbContext<XGArcadeDbContext>(options =>
                        options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                });
            });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(AdminAuthProviderUserId));
        return client;
    }

    private async Task SeedSuggestionAsync(string claimedClubName)
    {
        using var scope = _factory.Services.CreateScope();
        var playerRepository = scope.ServiceProvider.GetRequiredService<IPlayerRepository>();
        var candidate = await playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = "Candidate Player" });
        var preceding = await playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = "Preceding Player" });

        var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        await connectMatchRepository.AddDataCorrectionSuggestionAsync(new ConnectDisputeDataCorrectionSuggestion
        {
            Id = Guid.NewGuid(),
            ConnectMatchId = Guid.NewGuid(),
            ConnectChainStepId = Guid.NewGuid(),
            ConnectChainStepDisputeId = Guid.NewGuid(),
            CandidatePlayerId = candidate.Id,
            PrecedingPlayerId = preceding.Id,
            ClaimedClubName = claimedClubName,
            CreatedAt = DateTime.UtcNow,
        });
    }

    [Test]
    public async Task REQ1414_GetSuggestions_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/admin/connect-dispute-suggestions");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1414_GetSuggestions_NonAdmin_ReturnsForbidden()
    {
        var nonAdminAuthProviderUserId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(), AuthProviderUserId = nonAdminAuthProviderUserId, Email = "player@example.com",
                DisplayName = "Player", EmailConfirmed = true, CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(nonAdminAuthProviderUserId));

        var response = await client.GetAsync("/admin/connect-dispute-suggestions");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task REQ1414_GetSuggestions_Admin_ReturnsEverySuggestion()
    {
        await SeedSuggestionAsync("Arsenal");
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/connect-dispute-suggestions");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<List<ConnectDisputeDataCorrectionSuggestionResponse>>();
        Assert.That(body, Has.Count.EqualTo(1));
        Assert.That(body![0].ClaimedClubName, Is.EqualTo("Arsenal"));
        Assert.That(body[0].CandidatePlayerName, Is.EqualTo("Candidate Player"));
        Assert.That(body[0].PrecedingPlayerName, Is.EqualTo("Preceding Player"));
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Auth;
using XGArcade.Api.Incidents;
using XGArcade.Core.IncidentReporting;
using XGArcade.Data;
using XGArcade.Data.Entities;

namespace XGArcade.Api.Tests;

// REQ-903/ADR-0064/COMP-12: API-level coverage for POST /incidents. Same
// in-memory-DbContext-swap/local-e2e-auth pattern as SuggestionEndpointTests,
// with IGitHubIssueClient swapped for FakeGitHubIssueClient — this suite
// must never call the real GitHub API (REQ-903's own "Test level" note).
public class IncidentEndpointTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private readonly FakeGitHubIssueClient _fakeGitHubIssueClient = new();

    [SetUp]
    public void SetUp()
    {
        _fakeGitHubIssueClient.Calls.Clear();
        _fakeGitHubIssueClient.NextResult = GitHubIssueCreationResult.Ok("https://github.com/johanpearson/xg-arcade/issues/1");

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

                    services.RemoveAll<IGitHubIssueClient>();
                    services.AddSingleton<IGitHubIssueClient>(_fakeGitHubIssueClient);
                });
            });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private async Task<Guid> SeedUserAsync(Guid authProviderUserId, bool isGuest = false)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = authProviderUserId,
            Email = isGuest ? null : $"{authProviderUserId}@example.com",
            DisplayName = isGuest ? $"Guest{Guid.NewGuid():N}"[..12] : "Test Player",
            EmailConfirmed = !isGuest,
            IsGuest = isGuest,
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

    private static SubmitIncidentReportRequest ValidRequest() =>
        new("Grid freezes on submit", "The grid froze after I submitted a guess.", "grid", "https://xg-arcade-dev.example.com");

    // ---- REQ-903: unauthenticated ------------------------------------

    [Test]
    public async Task REQ903_Incidents_Post_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/incidents", ValidRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(_fakeGitHubIssueClient.Calls, Is.Empty);
    }

    [Test]
    public async Task REQ903_Incidents_Post_ReturnsUnauthorized_ForTokenWithNoMatchingLocalUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.PostAsJsonAsync("/incidents", ValidRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(_fakeGitHubIssueClient.Calls, Is.Empty);
    }

    // ---- REQ-903: guest rejection is server-side, not just a disabled UI --

    [Test]
    public async Task REQ903_Incidents_Post_GuestAccount_ReturnsForbidden_EvenWithAWellFormedRequest()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, isGuest: true);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync("/incidents", ValidRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Guest accounts cannot file incident reports"));
        Assert.That(_fakeGitHubIssueClient.Calls, Is.Empty, "a rejected guest submission must never reach GitHub");
    }

    // ---- REQ-903: validation ------------------------------------------

    [TestCase("")]
    [TestCase("   ")]
    public async Task REQ903_Incidents_Post_ReturnsBadRequest_ForEmptyOrWhitespaceTitle(string title)
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync("/incidents", new SubmitIncidentReportRequest(title, "A real description.", "grid", null));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("A title is required"));
    }

    [Test]
    public async Task REQ903_Incidents_Post_ReturnsBadRequest_ForTitleOverTheMaxLength()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);
        var tooLong = new string('a', IncidentEndpoints.TitleMaxLength + 1);

        var response = await client.PostAsJsonAsync("/incidents", new SubmitIncidentReportRequest(tooLong, "A real description.", "grid", null));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Title is too long"));
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task REQ903_Incidents_Post_ReturnsBadRequest_ForEmptyOrWhitespaceDescription(string description)
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync("/incidents", new SubmitIncidentReportRequest("A title", description, "grid", null));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("A description is required"));
    }

    [Test]
    public async Task REQ903_Incidents_Post_ReturnsBadRequest_ForDescriptionOverTheMaxLength()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);
        var tooLong = new string('a', IncidentEndpoints.DescriptionMaxLength + 1);

        var response = await client.PostAsJsonAsync("/incidents", new SubmitIncidentReportRequest("A title", tooLong, "grid", null));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Description is too long"));
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task REQ903_Incidents_Post_ReturnsBadRequest_ForEmptyOrWhitespaceScreen(string screen)
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync("/incidents", new SubmitIncidentReportRequest("A title", "A real description.", screen, null));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("A screen is required"));
    }

    [Test]
    public async Task REQ903_Incidents_Post_ReturnsBadRequest_ForScreenOverTheMaxLength()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);
        var tooLong = new string('a', IncidentEndpoints.ScreenMaxLength + 1);

        var response = await client.PostAsJsonAsync("/incidents", new SubmitIncidentReportRequest("A title", "A real description.", tooLong, null));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Screen is too long"));
    }

    [Test]
    public async Task REQ903_Incidents_Post_ReturnsBadRequest_ForEnvironmentOverTheMaxLength()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);
        var tooLong = new string('a', IncidentEndpoints.EnvironmentMaxLength + 1);

        var response = await client.PostAsJsonAsync("/incidents", new SubmitIncidentReportRequest("A title", "A real description.", "grid", tooLong));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Environment is too long"));
    }

    [Test]
    public async Task REQ903_Incidents_Post_AcceptsAMissingEnvironment_SinceItIsOptional()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync("/incidents", new SubmitIncidentReportRequest("A title", "A real description.", "grid", null));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ---- REQ-903: happy path -------------------------------------------

    [Test]
    public async Task REQ903_Incidents_Post_ValidRequestFromNonGuest_ReturnsOk_WithTheIssueUrl()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);
        _fakeGitHubIssueClient.NextResult = GitHubIssueCreationResult.Ok("https://github.com/johanpearson/xg-arcade/issues/7");

        var response = await client.PostAsJsonAsync("/incidents", ValidRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitIncidentReportResponse>();
        Assert.That(body!.IssueUrl, Is.EqualTo("https://github.com/johanpearson/xg-arcade/issues/7"));
        Assert.That(_fakeGitHubIssueClient.Calls, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task REQ903_Incidents_Post_IncludesTheResolvedUserId_InTheIssueBody_ButNeverAnEmailAddress()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        await client.PostAsJsonAsync("/incidents", ValidRequest());

        var (_, body) = _fakeGitHubIssueClient.Calls.Single();
        Assert.That(body, Does.Contain(userId.ToString()));
        Assert.That(body, Does.Not.Contain("@example.com"),
            "REQ-903: the issue body must never include the reporting player's email address");
    }

    [Test]
    public async Task REQ903_Incidents_Post_SendsTheGivenTitle_AndFormatsTheScreenAndEnvironment_IntoTheIssueBody()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        await client.PostAsJsonAsync(
            "/incidents",
            new SubmitIncidentReportRequest("Grid freezes on submit", "The grid froze after I submitted a guess.", "grid", "https://xg-arcade-dev.example.com"));

        var (title, body) = _fakeGitHubIssueClient.Calls.Single();
        Assert.That(title, Is.EqualTo("Grid freezes on submit"));
        Assert.That(body, Does.Contain("**Screen:** grid"));
        Assert.That(body, Does.Contain("**Environment:** https://xg-arcade-dev.example.com"));
    }

    // ---- REQ-903: GitHub failures surface as a clear, generic failure -----

    [Test]
    public async Task REQ903_Incidents_Post_ReturnsServiceUnavailable_WithoutLeakingGitHubDetail_WhenGitHubCallFails()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);
        _fakeGitHubIssueClient.NextResult = GitHubIssueCreationResult.Failed("Could not create the issue on GitHub. Please try again later.");

        var response = await client.PostAsJsonAsync("/incidents", ValidRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Could not submit your report"));
    }

    // ---- REQ-903: per-user rate limit -----------------------------------

    [Test]
    public async Task REQ903_Incidents_Post_ReturnsTooManyRequests_AfterExceedingThePerUserLimit()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        // Default permit limit is 3/window (Program.cs's RateLimiting:
        // IncidentReportPermitLimit default) — the fourth request in the
        // same window must be rejected.
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/incidents", ValidRequest());
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"request {i + 1} should still be within the limit");
        }

        var limited = await client.PostAsJsonAsync("/incidents", ValidRequest());

        Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
    }

    [Test]
    public async Task REQ903_Incidents_Post_RateLimitsIndependently_PerUser()
    {
        var firstAuthProviderUserId = Guid.NewGuid();
        var secondAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(firstAuthProviderUserId);
        await SeedUserAsync(secondAuthProviderUserId);
        var firstClient = CreateAuthenticatedClient(firstAuthProviderUserId);
        var secondClient = CreateAuthenticatedClient(secondAuthProviderUserId);

        for (var i = 0; i < 3; i++)
        {
            await firstClient.PostAsJsonAsync("/incidents", ValidRequest());
        }
        var firstUserLimited = await firstClient.PostAsJsonAsync("/incidents", ValidRequest());
        var secondUserResponse = await secondClient.PostAsJsonAsync("/incidents", ValidRequest());

        Assert.That(firstUserLimited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(secondUserResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "a different user's own requests must not be affected by another user's exhausted limit");
    }
}

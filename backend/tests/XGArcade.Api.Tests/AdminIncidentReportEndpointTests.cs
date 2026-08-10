using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Admin;
using XGArcade.Api.Auth;
using XGArcade.Core.IncidentReporting;
using XGArcade.Data;

namespace XGArcade.Api.Tests;

// REQ-904/ADR-0066: API-level coverage for GET /admin/incident-reports.
// Same in-memory-DbContext-swap/local-e2e-auth/Admin__UserIds pattern as
// AdminSuggestionEndpointTests, with IGitHubIssueClient swapped for
// FakeGitHubIssueClient (never the real GitHub API, per REQ-904's own
// "Test level" note). The endpoint reads through the REAL
// CachedIncidentIssueSummaryProvider (registered by Program.cs, wrapping
// the swapped fake client) rather than a fake provider — that's the point
// of this suite: it proves the endpoint + cache + client wiring together,
// which GitHubIssueClientTests/CachedIncidentIssueSummaryProviderTests
// (XGArcade.Core.Tests) don't exercise end-to-end.
public class AdminIncidentReportEndpointTests
{
    private static readonly Guid AdminAuthProviderUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // NUnit's default fixture lifecycle is SingleInstance (one
    // AdminIncidentReportEndpointTests instance shared across every [Test]
    // in this class) — _fakeGitHubIssueClient must be rebuilt in [SetUp],
    // same as IncidentEndpointTests' own SetUp resetting its shared fake's
    // state, or ListCallCount/NextListResult would leak between tests.
    private WebApplicationFactory<Program> _factory = null!;
    private FakeGitHubIssueClient _fakeGitHubIssueClient = null!;

    [SetUp]
    public void SetUp()
    {
        _fakeGitHubIssueClient = new FakeGitHubIssueClient();
    }

    private WebApplicationFactory<Program> BuildFactory(string? cacheTtlSeconds = null) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Auth:Mode", "local-e2e");
                builder.UseSetting("Admin:UserIds", AdminAuthProviderUserId.ToString());
                if (cacheTtlSeconds is not null)
                {
                    builder.UseSetting("GitHub:IncidentReportCacheTtlSeconds", cacheTtlSeconds);
                }

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

    [TearDown]
    public void TearDown() => _factory?.Dispose();

    private HttpClient CreateAdminClient()
    {
        _factory = BuildFactory();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(AdminAuthProviderUserId));
        return client;
    }

    // ---- Authorization boundary -----------------------------------------

    [Test]
    public async Task REQ904_IncidentReports_Get_ReturnsUnauthorized_WithoutBearerToken()
    {
        _factory = BuildFactory();
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/admin/incident-reports");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ904_IncidentReports_Get_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        _factory = BuildFactory();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(Guid.NewGuid()));

        var response = await client.GetAsync("/admin/incident-reports");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(_fakeGitHubIssueClient.ListCallCount, Is.EqualTo(0), "a forbidden request must never reach GitHub");
    }

    // ---- Success shape -----------------------------------------------------

    [Test]
    public async Task REQ904_IncidentReports_Get_ReturnsAvailableTrue_WithOpenCountAndIssues_ForAdmin()
    {
        _fakeGitHubIssueClient.NextListResult = GitHubIssueListResult.Ok([
            new GitHubIssueSummary(42, "Grid freezes on submit", "https://github.com/johanpearson/xg-arcade/issues/42"),
        ]);
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/incident-reports");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<IncidentReportsResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Available, Is.True);
        Assert.That(body.OpenCount, Is.EqualTo(1));
        var issue = body.Issues.Single();
        Assert.That(issue.Number, Is.EqualTo(42));
        Assert.That(issue.Title, Is.EqualTo("Grid freezes on submit"));
        Assert.That(issue.Url, Is.EqualTo("https://github.com/johanpearson/xg-arcade/issues/42"));
    }

    [Test]
    public async Task REQ904_IncidentReports_Get_ReturnsAvailableTrue_WithZeroOpenCount_WhenNoOpenIssuesExist()
    {
        _fakeGitHubIssueClient.NextListResult = GitHubIssueListResult.Ok([]);
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/incident-reports");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<IncidentReportsResponse>();
        Assert.That(body!.Available, Is.True);
        Assert.That(body.OpenCount, Is.EqualTo(0));
        Assert.That(body.Issues, Is.Empty);
    }

    // ---- Failure shape: never a false zero-count, and never a non-200 -----

    [Test]
    public async Task REQ904_IncidentReports_Get_Returns200_WithAvailableFalse_ZeroOpenCount_AndNoIssues_WhenGitHubCallFails()
    {
        _fakeGitHubIssueClient.NextListResult = GitHubIssueListResult.Failed("Could not list open issues on GitHub. Please try again later.");
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/incident-reports");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "REQ-904: a GitHub-side failure is a distinct in-body state, never an HTTP error status");
        var body = await response.Content.ReadFromJsonAsync<IncidentReportsResponse>();
        Assert.That(body!.Available, Is.False);
        Assert.That(body.OpenCount, Is.EqualTo(0));
        Assert.That(body.Issues, Is.Empty);
    }

    // ---- Server-side caching (ADR-0066) -------------------------------------

    [Test]
    public async Task REQ904_IncidentReports_Get_RepeatedRequestsWithinTtl_DoNotEachTriggerANewGitHubCall()
    {
        _fakeGitHubIssueClient.NextListResult = GitHubIssueListResult.Ok([
            new GitHubIssueSummary(1, "First issue", "https://github.com/johanpearson/xg-arcade/issues/1"),
        ]);
        var client = CreateAdminClient();

        var first = await client.GetAsync("/admin/incident-reports");
        var second = await client.GetAsync("/admin/incident-reports");
        var third = await client.GetAsync("/admin/incident-reports");

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(third.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(_fakeGitHubIssueClient.ListCallCount, Is.EqualTo(1),
            "repeated admin page loads within the cache TTL must not multiply outbound GitHub API calls 1:1 with requests");
    }

    [Test]
    public async Task REQ904_IncidentReports_Get_RequestAfterTtlExpires_TriggersAFreshGitHubCall()
    {
        _factory = BuildFactory(cacheTtlSeconds: "0.05");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(AdminAuthProviderUserId));
        _fakeGitHubIssueClient.NextListResult = GitHubIssueListResult.Ok([
            new GitHubIssueSummary(1, "First issue", "https://github.com/johanpearson/xg-arcade/issues/1"),
        ]);

        var first = await client.GetAsync("/admin/incident-reports");
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        _fakeGitHubIssueClient.NextListResult = GitHubIssueListResult.Ok([
            new GitHubIssueSummary(1, "First issue", "https://github.com/johanpearson/xg-arcade/issues/1"),
            new GitHubIssueSummary(2, "Second issue", "https://github.com/johanpearson/xg-arcade/issues/2"),
        ]);
        var second = await client.GetAsync("/admin/incident-reports");

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(_fakeGitHubIssueClient.ListCallCount, Is.EqualTo(2), "a request after the TTL expires must trigger a fresh GitHub call");
        var secondBody = await second.Content.ReadFromJsonAsync<IncidentReportsResponse>();
        Assert.That(secondBody!.OpenCount, Is.EqualTo(2), "the cache must be repopulated from the fresh result");
    }
}

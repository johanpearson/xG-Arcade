using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Predict;
using XGArcade.Data;

namespace XGArcade.Api.Tests;

// REQ-1305/ADR-0097: API-level coverage for POST /internal/grade-predict-matches
// — same bearer-token-gated WebApplicationFactory pattern
// InternalGuestCleanupEndpointTests uses for /internal/purge-guest-accounts.
//
// Deliberately does NOT seed a match that's actually ready for grading:
// IFootballDataClient is registered against the real football-data.org
// HttpClient in this composition root (ServiceRegistration.
// AddFootballDataServices) — this sandbox/CI has no live football-data.org
// account or network access to it (ADR-0099's own standing caveat), so an
// authorized call here only ever exercises the "nothing ready to grade"
// path (PredictGradingService's own loop never reaches
// GetFixtureResultAsync when GetMatchesReadyForGradingAsync returns
// empty), plus the endpoint's own auth gate. Full grading-logic coverage
// against a real Finished/NotYetConfirmed/PostponedOrAbandoned outcome is
// PredictGradingServiceTests' job (XGArcade.Games.XGPredict.Tests), via
// FakeFootballDataClient.
public class InternalPredictGradingEndpointTests
{
    private const string ValidJobToken = "test-internal-job-token";

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Internal:JobToken"] = ValidJobToken,
                    });
                });

                builder.ConfigureServices(services =>
                {
                    // Same in-memory-DbContext swap as every other
                    // XGArcade.Api.Tests file — see AuthEndpointTests' SetUp
                    // comment for why every XGArcadeDbContext-closed
                    // descriptor must be removed, not just the two obvious
                    // ones.
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

    private HttpClient CreateAuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);
        return client;
    }

    [Test]
    public async Task REQ1305_GradePredictMatches_Post_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/internal/grade-predict-matches", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1305_GradePredictMatches_Post_ReturnsUnauthorized_WithWrongBearerToken()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-right-token");

        var response = await client.PostAsync("/internal/grade-predict-matches", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ1305_GradePredictMatches_Post_NoMatchesReadyForGrading_ReturnsZeroCounts()
    {
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/grade-predict-matches", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<GradePredictMatchesResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Graded, Is.EqualTo(0));
        Assert.That(body.Voided, Is.EqualTo(0));
        Assert.That(body.StillPending, Is.EqualTo(0));
        Assert.That(body.Failed, Is.EqualTo(0));
    }
}

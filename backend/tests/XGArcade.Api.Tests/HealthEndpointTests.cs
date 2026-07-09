using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace XGArcade.Api.Tests;

// S-002 (trivial end-to-end slice, docs/backlog.md): no REQ-xxx exists for
// the health check itself (pure infra, not user-facing behavior), so this
// is named descriptively rather than REQ-prefixed.
public class HealthEndpointTests
{
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>();
    }

    [TearDown]
    public void TearDown()
    {
        _factory.Dispose();
    }

    [Test]
    public async Task Health_Get_ReturnsOkWithHealthyStatus()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("healthy"));
    }
}

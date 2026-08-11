using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using XGArcade.Api.CompositionRoot;

namespace XGArcade.Api.Tests;

// S-113 (docs/backlog.md Epic 8): AuthSetup.cs is otherwise a pure
// composition-root wiring file (DI registrations, middleware config),
// covered indirectly by every other file in this project via
// WebApplicationFactory — see docs/coding-guidelines.md's "Composition-root
// testing" convention for why that's the deliberate default. IsLocalE2EAuth
// and GetClientIpPartitionKey are the exception: real, security/correctness-
// relevant branching logic that's already a pure function of its inputs, so
// it's tested directly here instead, no HTTP host required.
public class AuthSetupTests
{
    private static IConfiguration BuildConfiguration(string? authMode)
    {
        var data = new Dictionary<string, string?>();
        if (authMode is not null)
            data["Auth:Mode"] = authMode;

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Test]
    public void REQ606_IsLocalE2EAuth_ModeLocalE2EAndDevelopment_ReturnsTrue()
    {
        var configuration = BuildConfiguration("local-e2e");
        var environment = new FakeHostEnvironment(Environments.Development);

        var result = AuthSetup.IsLocalE2EAuth(configuration, environment);

        Assert.That(result, Is.True);
    }

    // ADR-0006's "never guarded only by config/an attribute" principle,
    // applied to auth: a stray Auth:Mode=local-e2e config value must never
    // by itself enable the fake auth client outside Development, even
    // though nothing else in this method's signature stops it.
    [Test]
    public void REQ606_IsLocalE2EAuth_ModeLocalE2EButProduction_ReturnsFalse()
    {
        var configuration = BuildConfiguration("local-e2e");
        var environment = new FakeHostEnvironment(Environments.Production);

        var result = AuthSetup.IsLocalE2EAuth(configuration, environment);

        Assert.That(result, Is.False);
    }

    [Test]
    public void REQ606_IsLocalE2EAuth_ModeNotSetInDevelopment_ReturnsFalse()
    {
        var configuration = BuildConfiguration(authMode: null);
        var environment = new FakeHostEnvironment(Environments.Development);

        var result = AuthSetup.IsLocalE2EAuth(configuration, environment);

        Assert.That(result, Is.False);
    }

    [Test]
    public void REQ606_IsLocalE2EAuth_ModeNotSetInProduction_ReturnsFalse()
    {
        var configuration = BuildConfiguration(authMode: null);
        var environment = new FakeHostEnvironment(Environments.Production);

        var result = AuthSetup.IsLocalE2EAuth(configuration, environment);

        Assert.That(result, Is.False);
    }

    [Test]
    public void REQ606_GetClientIpPartitionKey_RemoteIpAddressSet_ReturnsItsStringForm()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.5");

        var partitionKey = AuthSetup.GetClientIpPartitionKey(httpContext);

        Assert.That(partitionKey, Is.EqualTo("203.0.113.5"));
    }

    // TestServer (WebApplicationFactory) leaves RemoteIpAddress null for
    // every request — this is what makes AuthEndpointTests.cs's REQ606 rate
    // limit tests able to trip the limit deterministically with a
    // same-process burst (see AuthSetup.cs's own doc comment on this
    // method). Asserted directly here so that behavior has one source of
    // truth instead of being an implicit assumption baked into every test
    // that depends on it.
    [Test]
    public void REQ606_GetClientIpPartitionKey_RemoteIpAddressNull_ReturnsUnknown()
    {
        var httpContext = new DefaultHttpContext();

        var partitionKey = AuthSetup.GetClientIpPartitionKey(httpContext);

        Assert.That(partitionKey, Is.EqualTo("unknown"));
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "XGArcade.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

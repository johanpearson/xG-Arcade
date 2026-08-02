using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using XGArcade.Api.Cli;

namespace XGArcade.Api.Tests;

// ADR-0054 (2026-08-02, ADR-0052 follow-up): regression coverage for the
// bug this class fixes — every CLI verb's ILoggerFactory (warm-player-cache,
// import-player-name-index, backfill-player-photos in Program.cs) used to be
// built with a hardcoded `SetMinimumLevel(LogLevel.Information)` that never
// consulted IConfiguration's "Logging" section at all, silently breaking
// ADR-0052's own documented "set Logging:LogLevel:Default, or a scoped
// category override, to Debug" troubleshooting instruction for every one of
// these verbs. Builds IConfiguration the same way Program.cs's CLI verbs do
// (ConfigurationBuilder + in-memory collection, standing in for
// AddEnvironmentVariables' env-var-sourced key/value pairs — same
// `Section:SubSection` colon-separated key shape .NET's configuration
// binder normalizes a `Section__SubSection` environment variable to) to
// prove the fix actually reaches a scoped category override, not just the
// blanket default.
public class CliLoggerFactoryTests
{
    private const string WikidataClientCategory = "XGArcade.DataSync.Wikidata.WikidataClient";

    [Test]
    public void REQ110_Build_NoConfiguredOverride_DefaultsToInformation()
    {
        var configuration = new ConfigurationBuilder().Build();

        using var loggerFactory = CliLoggerFactory.Build(configuration);
        var logger = loggerFactory.CreateLogger(WikidataClientCategory);

        Assert.That(logger.IsEnabled(LogLevel.Information), Is.True);
        Assert.That(logger.IsEnabled(LogLevel.Debug), Is.False,
            "with no Logging:LogLevel override configured, the default minimum level should stay Information " +
            "(unchanged behavior from before this fix) — Debug-level detail should stay filtered out of a normal run.");
    }

    // The actual bug: setting a scoped category override (exactly the shape
    // Logging__LogLevel__XGArcade.DataSync.Wikidata.WikidataClient=Debug
    // normalizes to once .NET's configuration binder replaces `__` with `:`)
    // must actually raise that category's effective minimum level — this is
    // what silently did nothing before this fix.
    [Test]
    public void REQ110_Build_ScopedCategoryOverrideConfigured_RaisesThatCategoryToDebug()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Logging:LogLevel:{WikidataClientCategory}"] = "Debug",
            })
            .Build();

        using var loggerFactory = CliLoggerFactory.Build(configuration);
        var wikidataClientLogger = loggerFactory.CreateLogger(WikidataClientCategory);
        var unrelatedLogger = loggerFactory.CreateLogger("XGArcade.Games.XGGrid.PlayerCacheWarmingService");

        Assert.That(wikidataClientLogger.IsEnabled(LogLevel.Debug), Is.True,
            "a scoped Logging:LogLevel override for WikidataClient's own category must reach its logger");
        Assert.That(unrelatedLogger.IsEnabled(LogLevel.Debug), Is.False,
            "the override is scoped to WikidataClient's category only — an unrelated category must stay at the default minimum level");
    }

    [Test]
    public void REQ110_Build_DefaultLogLevelOverrideConfigured_RaisesEveryCategory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Debug",
            })
            .Build();

        using var loggerFactory = CliLoggerFactory.Build(configuration);
        var logger = loggerFactory.CreateLogger(WikidataClientCategory);

        Assert.That(logger.IsEnabled(LogLevel.Debug), Is.True,
            "Logging:LogLevel:Default (ADR-0052's own troubleshooting instruction) must reach every CLI verb's logger");
    }
}

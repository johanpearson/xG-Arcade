using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace XGArcade.Api.Cli;

// ADR-0054 (2026-08-02, ADR-0052 follow-up): single source of truth for
// every CLI verb's (`dotnet run -- warm-player-cache`/`import-player-name-index`/
// `backfill-player-photos` in Program.cs) ILoggerFactory. Extracted into its
// own testable static method — rather than staying a Program.cs local
// function the way ConfigureWikidataHttpClient does — specifically so the
// bug this fixes has a regression test: Program.cs's top-level statements
// have no test harness of their own (WebApplicationFactory<Program> only
// ever exercises the WebApplication/HTTP path, which every CLI verb returns
// before ever reaching). Same "extract to its own file for independent unit
// testing" precedent Auth/SupabaseJwksConfigurationRetriever.cs already
// established — see SupabaseJwksConfigurationRetrieverTests.cs.
//
// See ADR-0054 for the full "why this didn't work before" story. Every CLI
// verb above used to build its ILoggerFactory with
// `LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information))`
// — a hardcoded minimum level that never consulted IConfiguration's
// "Logging" section at all (environment-variable overrides included). That
// silently broke ADR-0052's own documented troubleshooting instruction ("set
// Logging:LogLevel:Default, or a scoped category override, to Debug to see
// WikidataClient's per-pair timeout/HTTP/parse-error detail again") for
// every one of these CLI verbs — confirmed live: setting
// Logging__LogLevel__XGArcade.DataSync.Wikidata.WikidataClient=Debug as an
// env var on a warm-player-cache run produced zero Debug-level lines
// anywhere in a complete job log.
//
// AddConfiguration(configuration.GetSection("Logging")) below is exactly
// what WebApplication.CreateBuilder's own default logging setup does with
// that section for the normal HTTP server path — wiring the same
// Logging:LogLevel:Default / per-category override rules into the console
// provider these CLI verbs already used. Do not go back to a hardcoded
// SetMinimumLevel; it silently reintroduces this exact gap.
public static class CliLoggerFactory
{
    public static ILoggerFactory Build(IConfiguration configuration) =>
        LoggerFactory.Create(builder => builder
            .AddConfiguration(configuration.GetSection("Logging"))
            .AddConsole());
}

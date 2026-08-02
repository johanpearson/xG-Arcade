# ADR-0054: CLI verbs build their `ILoggerFactory` from configuration, not a hardcoded minimum level

- **Status:** Accepted
- **Date:** 2026-08-02
- **Related requirements:** REQ-110
- **Related components:** COMP-07 (DataSync.Clients)

## Context

ADR-0052 downgraded `WikidataClient.RunIntersectionQueryAsync`'s two
per-pair failure logs (timeout; HTTP/JSON parse error) from `Warning` to
`Debug`, specifically so a normal `warm-player-cache` run's console stays
readable, with an explicit documented escape hatch: "set
`Logging:LogLevel:Default` (or a scoped override) to `Debug` when actually
troubleshooting a specific pair."

That escape hatch turned out not to work for any CLI verb. `Program.cs`'s
`warm-player-cache`, `import-player-name-index`, and
`backfill-player-photos` verbs (the three that log at all) each return
before `WebApplication.CreateBuilder(args)` ever runs — the thing that
normally wires `IConfiguration`'s `Logging` section (appsettings.json +
environment variables + command-line args, in the usual precedence order)
into the app's logging providers automatically. Each of these verbs instead
built its own `ILoggerFactory` directly:

```csharp
using var warmingLoggerFactory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(LogLevel.Information));
```

`SetMinimumLevel(LogLevel.Information)` is a hardcoded floor with no
connection to `IConfiguration` at all — no `Logging:LogLevel:Default`
override, no scoped per-category override, nothing. This was confirmed
live: setting
`Logging__LogLevel__XGArcade.DataSync.Wikidata.WikidataClient=Debug` as an
environment variable on a `warm-player-cache` CI run (see
`diagnostic/club-club-cache-warming-failures`, a throwaway diagnostic
branch) produced zero `Debug`-level lines anywhere in the complete job log,
even though the same override reaches `WikidataClient`'s logger correctly
for the normal `WebApplication` path (the one `AddHttpClient<IWikidataClient,
WikidataClient>` registration serves). This silently defeated ADR-0052's
own documented troubleshooting instructions for the exact CLI verb that
instruction exists for — cache warming's own investigation into a separate,
still-open problem (~118-125 persistently-failing Club×Club pairs) was
blocked by this bug before it could even gather evidence.

## Decision

Extract a single, testable helper, `CliLoggerFactory.Build(IConfiguration)`
(`XGArcade.Api.Cli`), used by all three logging CLI verbs in place of their
own hand-rolled `LoggerFactory.Create(...)` call:

```csharp
public static ILoggerFactory Build(IConfiguration configuration) =>
    LoggerFactory.Create(builder => builder
        .AddConfiguration(configuration.GetSection("Logging"))
        .AddConsole());
```

`AddConfiguration(configuration.GetSection("Logging"))` is exactly what
`WebApplication.CreateBuilder`'s own default logging setup does with that
section for the normal HTTP server path — wiring
`Logging:LogLevel:Default`/per-category override rules (config file or
environment variable, since each CLI verb's own `ConfigurationBuilder`
already calls `.AddEnvironmentVariables()`) into the console provider. Each
CLI verb passes its own already-built `IConfiguration` instance (the one it
already uses for `ConnectionStrings:Database`), so no new configuration
source is introduced. With no override configured, the effective behavior
is unchanged from before this fix — .NET's own default minimum level with
no filter rules present is `Information`, the same value the old
`SetMinimumLevel(LogLevel.Information)` hardcoded.

`CliLoggerFactory.Build` is a small `public static` method in its own file
rather than staying a `Program.cs` local function (the pattern
`ConfigureWikidataHttpClient` already uses for the same "shared between the
DI registration and the CLI verbs" reason) specifically so this fix has a
regression test: `Program.cs`'s top-level statements have no test harness
of their own — `WebApplicationFactory<Program>` in `XGArcade.Api.Tests`
only ever exercises the `WebApplication`/HTTP path, which every CLI verb
returns before ever reaching, so a local function here would have stayed
just as untestable as the bug it replaces.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Keep the hardcoded `SetMinimumLevel(LogLevel.Information)`, work around it per-investigation (e.g. temporarily hardcode `Debug` in source for one CI run) | No production code change | Exactly what the throwaway `diagnostic/club-club-cache-warming-failures` branch tried and found didn't work anyway (still hardcoded, just to a different literal) — worse, it re-breaks ADR-0052's own documented "set the env var" instructions for every future investigation, not just this one | ADR-0052 already documented the env-var approach as the supported way to troubleshoot this exact class of problem; silently leaving it broken means the next person hits the identical dead end |
| Route every CLI verb through `Host.CreateApplicationBuilder(args)` instead of hand-building `ConfigurationBuilder`/`DbContextOptionsBuilder`/`ILoggerFactory` directly | Would fix this bug and give CLI verbs the same full configuration/DI machinery as the real host, in one general change | Much larger blast radius for an observability-only bug: every one of the eight CLI verbs in `Program.cs` (`migrate-and-seed`, `warm-player-cache`, `import-player-name-index`, `backfill-player-photos`, `verify-wikidata-player-data`, `clean-stale-club-attributes`, `clear-pair-lookup-failures`, `purge-player-pool`) deliberately builds its dependencies directly "rather than spinning up the full WebApplication DI container" (each verb's own doc comment) — rewriting that pattern for all eight to fix a three-verb logging gap is a disproportionate, unreviewed-scope change | The narrow `CliLoggerFactory.Build` helper fixes exactly the gap (config not reaching the logger) without touching how any CLI verb builds its `DbContext`, `HttpClient`, or repositories |
| Add a `LogLevel` CLI argument/flag instead of relying on `Logging:LogLevel` config | Explicit, discoverable via `--help`-style output | A second, parallel way to control log level alongside the config-based one ADR-0052 already documented and every other ASP.NET Core component in this codebase uses — two mechanisms for the same knob is exactly the kind of drift-prone duplication CLAUDE.md's "shared configuration helpers" convention warns against | `Logging:LogLevel` config (env var override) is already this codebase's single established mechanism; the bug was that CLI verbs didn't honor it, not that the mechanism itself needed replacing |

## Consequences

- Positive: `Logging:LogLevel:Default` or a scoped category override (e.g.
  `Logging__LogLevel__XGArcade.DataSync.Wikidata.WikidataClient=Debug`) now
  actually reaches `warm-player-cache`/`import-player-name-index`/
  `backfill-player-photos`'s loggers, restoring ADR-0052's own documented
  troubleshooting path.
- Positive: `CliLoggerFactory.Build` is unit-testable
  (`CliLoggerFactoryTests.cs`), unlike the local function it replaces —
  regression coverage exists for the specific failure mode (a configured
  override silently not reaching the logger) that motivated this ADR.
- Negative / trade-off accepted: does not extend to `migrate-and-seed`,
  `verify-wikidata-player-data`, `clean-stale-club-attributes`,
  `clear-pair-lookup-failures`, or `purge-player-pool` — none of those five
  build an `ILoggerFactory` at all (they log via bare `Console.WriteLine`),
  so there was nothing for this fix to reach there. If any of them later
  gains structured/leveled logging, route it through `CliLoggerFactory.Build`
  from the start rather than reintroducing a hand-rolled
  `LoggerFactory.Create(...)`.
- Follow-up: this fix unblocks (but does not itself resolve) the separate,
  still-open investigation into ~118-125 persistently-failing Club×Club
  pairs in `warm-player-cache` — see the forthcoming ADR documenting that
  investigation's findings and fix, once real per-pair `Debug`-level
  evidence has actually been gathered using this fix.

## For AI agents

Do not add a new CLI verb's `ILoggerFactory` via a hand-rolled
`LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(...))` — use
`CliLoggerFactory.Build(configuration)` (`XGArcade.Api.Cli`) instead, the
same way `ConfigureWikidataHttpClient` is the single source of truth for
`WikidataClient`'s `HttpClient` configuration. A hardcoded minimum level
silently defeats every `Logging:LogLevel` override (config file or
environment variable) the same way this ADR's bug did — there is no
legitimate reason for a CLI verb's logger to ignore that configuration
section when every other component in this codebase honors it.

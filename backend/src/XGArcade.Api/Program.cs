using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using XGArcade.Api.Admin;
using XGArcade.Api.Auth;
using XGArcade.Api.Grid;
using XGArcade.Api.Guesses;
using XGArcade.Api.Leagues;
using XGArcade.Api.Players;
using XGArcade.Api.Rounds;
using XGArcade.Core.Auth;
using XGArcade.Core.Games;
using XGArcade.Core.Leagues;
using XGArcade.Core.Rounds;
using XGArcade.Core.Scoring;
using XGArcade.Data;
using XGArcade.Data.Repositories;
using XGArcade.Data.Seeding;
using XGArcade.DataSync.Wikidata;
using XGArcade.Games.XGGrid;

// `dotnet run -- migrate-and-seed` is a distinct CLI verb (not a normal
// server start) used by ci.yml's local E2E stack. Applies pending EF Core
// migrations against ConnectionStrings:Database, then seeds Tier 0's
// hand-curated reference data (S-005) — idempotent, safe to re-run.
if (args is ["migrate-and-seed"])
{
    var migrationConfig = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();

    var connectionString = migrationConfig.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

    var optionsBuilder = new DbContextOptionsBuilder<XGArcadeDbContext>()
        .UseNpgsql(connectionString);

    await using var migrationDbContext = new XGArcadeDbContext(optionsBuilder.Options);
    await migrationDbContext.Database.MigrateAsync();
    await ReferenceDataSeeder.SeedAsync(migrationDbContext);
    // S-009: backfills Player.NormalizedFullName for any row that predates
    // that column (or predates PlayerNameNormalizer's punctuation-stripping
    // fix) — see PlayerNormalizedFullNameBackfiller's own doc comment.
    await PlayerNormalizedFullNameBackfiller.BackfillAsync(migrationDbContext);
    // S-011: backfills User.DisplayName for any row that predates that
    // column — see UserDisplayNameBackfiller's own doc comment.
    await UserDisplayNameBackfiller.BackfillAsync(migrationDbContext);
    // S-011: backfills LeagueMembership for any User row that predates
    // REQ-401's auto-enrollment-at-signup — see LeagueMembershipBackfiller's
    // own doc comment.
    await LeagueMembershipBackfiller.BackfillAsync(migrationDbContext);

    Console.WriteLine("migrate-and-seed: migrations applied, reference data seeded.");
    return;
}

// REQ-110 (ADR-0023's follow-up): `dotnet run -- warm-player-cache` is a
// second distinct CLI verb, same shape as migrate-and-seed above but run
// by its own workflow (warm-player-cache.yml), manually, after any
// reference-data change — never inside a synchronous HTTP request (see
// PlayerCacheWarmingService's own doc comment for why). Builds its
// dependencies directly rather than spinning up the full WebApplication
// DI container, same as migrate-and-seed does.
if (args is ["warm-player-cache"])
{
    var warmingConfig = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();

    var warmingConnectionString = warmingConfig.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

    var warmingDbContextOptions = new DbContextOptionsBuilder<XGArcadeDbContext>()
        .UseNpgsql(warmingConnectionString)
        .Options;

    using var warmingLoggerFactory = LoggerFactory.Create(b => b
        .AddConsole()
        .SetMinimumLevel(LogLevel.Information));

    await using var warmingDbContext = new XGArcadeDbContext(warmingDbContextOptions);
    var warmingCategoryValueRepository = new CategoryValueRepository(warmingDbContext);
    var warmingPlayerStoreRepository = new PlayerStoreRepository(warmingDbContext);

    using var warmingHttpClient = new HttpClient();
    ConfigureWikidataHttpClient(warmingHttpClient);
    var warmingWikidataClient = new WikidataClient(warmingHttpClient, logger: warmingLoggerFactory.CreateLogger<WikidataClient>());
    var warmingWikidataLookupService = new WikidataLookupService(warmingWikidataClient, warmingPlayerStoreRepository);

    var warmingService = new PlayerCacheWarmingService(
        warmingCategoryValueRepository, warmingPlayerStoreRepository, warmingWikidataLookupService,
        new GridGenerationOptions(), warmingLoggerFactory.CreateLogger<PlayerCacheWarmingService>());

    await warmingService.WarmAsync();

    Console.WriteLine("warm-player-cache: complete.");
    return;
}

// S-032 (ADR-0007/REQ-207): `dotnet run -- import-player-name-index` is a
// fifth distinct CLI verb — same shape as warm-player-cache above (builds
// its dependencies directly rather than the full DI container, since it
// runs before WebApplication.CreateBuilder), run manually via its own
// workflow (import-player-name-index.yml, workflow_dispatch only, no
// schedule — ADR-0007's own follow-up note says start with a manual/
// periodic refresh, tighten only if names are noticeably missing). See
// PlayerNameIndexImporter's own doc comment for the full "why a CLI verb,
// not an HTTP endpoint or background task" reasoning (ADR-0024).
if (args is ["import-player-name-index"])
{
    var importConfig = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();

    var importConnectionString = importConfig.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

    var importDbContextOptions = new DbContextOptionsBuilder<XGArcadeDbContext>()
        .UseNpgsql(importConnectionString)
        .Options;

    using var importLoggerFactory = LoggerFactory.Create(b => b
        .AddConsole()
        .SetMinimumLevel(LogLevel.Information));

    await using var importDbContext = new XGArcadeDbContext(importDbContextOptions);
    var importRepository = new PlayerNameIndexRepository(importDbContext);

    using var importHttpClient = new HttpClient();
    ConfigureWikidataHttpClient(importHttpClient);
    // 60s, deliberately kept after the 2026-07-18 birth-year-slicing fix
    // (NOTES.md): WDQS enforces its own hard ~60s SERVER-side timeout, so a
    // client timeout above 60s can never help — 60s means this client only
    // gives up when WDQS itself would. The bounded one-year slice queries
    // normally answer well inside WikidataClient's 15s default (ADR-0011,
    // tuned for the per-cell intersection queries), but the densest recent
    // birth years return tens of thousands of label-joined rows and this is
    // a manually-triggered batch job with no request-latency constraint —
    // waiting the full server budget per slice costs nothing, while a
    // too-tight client timeout would spuriously fail slices the server was
    // still going to answer. Do NOT raise this above 60s: the server cap
    // binds first, so a larger number is pure self-deception (that mistake
    // was already made once — see NOTES.md's 2026-07-17/18 entries).
    var importWikidataClient = new WikidataClient(
        importHttpClient,
        queryTimeout: TimeSpan.FromSeconds(60),
        logger: importLoggerFactory.CreateLogger<WikidataClient>());

    // No timeProvider/retryBackoff overrides: TimeProvider.System bounds the
    // year range (fine for a CLI job) and the default retry backoff applies.
    // ImportAsync THROWS if any birth-year slice fails all its retries —
    // deliberately unhandled here so the process exits nonzero and the
    // import-player-name-index.yml run goes red instead of "exit 0,
    // imported 0" (the 2026-07-18 incident).
    var importer = new PlayerNameIndexImporter(
        importWikidataClient, importRepository, importLoggerFactory.CreateLogger<PlayerNameIndexImporter>());

    var importedCount = await importer.ImportAsync();

    Console.WriteLine($"import-player-name-index: upserted {importedCount} PlayerNameIndex row(s).");
    return;
}

// REQ-214 backfill (S-045): `dotnet run -- backfill-player-photos` is a
// sixth distinct CLI verb — same shape as warm-player-cache above (builds
// its dependencies directly rather than the full DI container, since it
// runs before WebApplication.CreateBuilder), run manually via its own
// workflow (backfill-player-photos.yml, workflow_dispatch only). See
// PlayerPhotoBackfillService's own doc comment for the full "why a CLI
// verb, not an HTTP endpoint or background task" reasoning — squarely
// inside ADR-0024's existing decision, not a new one.
if (args is ["backfill-player-photos"])
{
    var backfillConfig = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();

    var backfillConnectionString = backfillConfig.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

    var backfillDbContextOptions = new DbContextOptionsBuilder<XGArcadeDbContext>()
        .UseNpgsql(backfillConnectionString)
        .Options;

    using var backfillLoggerFactory = LoggerFactory.Create(b => b
        .AddConsole()
        .SetMinimumLevel(LogLevel.Information));

    await using var backfillDbContext = new XGArcadeDbContext(backfillDbContextOptions);
    var backfillPlayerStoreRepository = new PlayerStoreRepository(backfillDbContext);

    using var backfillHttpClient = new HttpClient();
    ConfigureWikidataHttpClient(backfillHttpClient);
    var backfillWikidataClient = new WikidataClient(
        backfillHttpClient, logger: backfillLoggerFactory.CreateLogger<WikidataClient>());

    var backfillService = new PlayerPhotoBackfillService(
        backfillPlayerStoreRepository, backfillWikidataClient,
        backfillLoggerFactory.CreateLogger<PlayerPhotoBackfillService>());

    var backfillResult = await backfillService.BackfillAsync();

    Console.WriteLine(
        $"backfill-player-photos: complete — {backfillResult.BatchesProcessed} batch(es) processed, " +
        $"{backfillResult.PlayersBackfilled} player(s) backfilled, {backfillResult.BatchesFailed} batch(es) failed.");
    return;
}

// ADR-0029: `dotnet run -- verify-wikidata-player-data` is a one-time
// backlog cleanup, run once after deploying the Confidence-by-origin change
// (WikidataLookupOrigin) so the admin review queue (REQ-503) doesn't stay
// stuck at whatever size it had already grown to under the old
// always-unverified rule. No PlayerData row records which code path
// created it (Source is always the literal "wikidata" either way), so
// there's no way to tell, after the fact, which historical rows came from
// a routine sync versus REQ-211's guess-time fallback — this bulk-verifies
// all of them, matching the new default for a Sync-origin lookup, the
// overwhelming majority of what actually created this backlog. A plain
// bulk `ExecuteUpdateAsync` (not the load-then-SaveChangesAsync pattern
// coding-guidelines.md otherwise requires) is fine here specifically
// because this is a standalone operational CLI verb never exercised by the
// InMemory-provider unit tests that rule exists to protect — same
// established exception as purge-player-pool's own `ExecuteDeleteAsync`
// above. Safe to re-run: a second run simply finds zero matching rows.
if (args is ["verify-wikidata-player-data"])
{
    var verifyConfig = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();

    var verifyConnectionString = verifyConfig.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

    var verifyDbContextOptions = new DbContextOptionsBuilder<XGArcadeDbContext>()
        .UseNpgsql(verifyConnectionString)
        .Options;

    await using var verifyDbContext = new XGArcadeDbContext(verifyDbContextOptions);
    var verifiedCount = await verifyDbContext.PlayerData
        .Where(d => d.Source == "wikidata" && d.Confidence == "unverified")
        .ExecuteUpdateAsync(setters => setters.SetProperty(d => d.Confidence, "verified"));

    Console.WriteLine($"verify-wikidata-player-data: marked {verifiedCount} PlayerData row(s) verified.");
    return;
}

// S-037: `dotnet run -- clean-stale-club-attributes "<comma-separated club names>"`
// is a third distinct CLI verb — see StaleClubAttributeCleaner's own doc
// comment for the full reasoning (why this exists, and why it's manual and
// argument-driven rather than wired into migrate-and-seed's automatic,
// safe-to-run-forever chain the way the other backfillers are). Club names
// are passed as one comma-separated argument, not one shell argument per
// name, so a name containing a space (e.g. "AS Roma") survives a
// GitHub Actions workflow_dispatch text input intact without any shell
// word-splitting/quoting risk.
//
// The literal argument `--all-clubs` (instead of a name list) resolves the
// club names from the ClubDefinition reference table at runtime — for
// recoveries that invalidate every seeded club at once (like the truthy
// wdt:P54 query bug; see StaleClubAttributeCleaner.CleanAllSeededClubsAsync),
// where hand-typing ~32 names is exactly the typo surface that silently
// leaves a misspelled club stale. Still the same manual, workflow_dispatch-
// only friction as the named mode — never wired into migrate-and-seed.
//
// Matched on the verb alone (not the full ["...", var arg] shape) so a
// malformed invocation — the names argument missing or blank, e.g. an empty
// workflow_dispatch text field — fails loudly via the explicit throw below
// instead of silently falling through to WebApplication.CreateBuilder and
// starting the full server, which would leave a workflow_dispatch job
// either hanging or exiting with no signal of what went wrong.
if (args is ["clean-stale-club-attributes", ..])
{
    var cleanClubNamesArg = args.Length > 1 ? args[1] : null;
    if (string.IsNullOrWhiteSpace(cleanClubNamesArg))
        throw new InvalidOperationException(
            "clean-stale-club-attributes requires a comma-separated club names argument (or the literal `--all-clubs`), " +
            "e.g. `clean-stale-club-attributes \"Napoli,AS Roma\"` or `clean-stale-club-attributes --all-clubs`.");

    var cleanConfig = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();

    var cleanConnectionString = cleanConfig.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

    var cleanDbContextOptions = new DbContextOptionsBuilder<XGArcadeDbContext>()
        .UseNpgsql(cleanConnectionString)
        .Options;

    await using var cleanDbContext = new XGArcadeDbContext(cleanDbContextOptions);

    int removedAttributeCount;
    int removedDataCount;
    IReadOnlyList<string> cleanClubNames;
    if (cleanClubNamesArg.Trim() == "--all-clubs")
    {
        (removedAttributeCount, removedDataCount, cleanClubNames) =
            await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(cleanDbContext);
    }
    else
    {
        cleanClubNames = cleanClubNamesArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // A mistyped flag (e.g. `--all-club`, or single-dash `-all-clubs`)
        // would otherwise fall through to the named mode, match no club,
        // and print a plausible-looking "removed 0 rows" success — the
        // exact silent-typo failure mode the `--all-clubs` mode exists to
        // close. REQ-111: any `-`-prefixed token fails loudly. No seeded
        // club name starts with `-`, so this can never reject a real club
        // list.
        var flagLikeToken = cleanClubNames.FirstOrDefault(name => name.StartsWith("-", StringComparison.Ordinal));
        if (flagLikeToken is not null)
            throw new InvalidOperationException(
                $"clean-stale-club-attributes got the flag-like token '{flagLikeToken}' (`-` prefix) — " +
                "the only supported flag is the exact literal `--all-clubs`.");

        (removedAttributeCount, removedDataCount) =
            await StaleClubAttributeCleaner.CleanAsync(cleanDbContext, cleanClubNames);
    }

    Console.WriteLine($"clean-stale-club-attributes: removed {removedAttributeCount} PlayerAttribute row(s) and {removedDataCount} PlayerData row(s) for: {string.Join(", ", cleanClubNames)}.");
    return;
}

// S-038 (ADR-0025): `dotnet run -- purge-player-pool "delete all player data"`
// is a fourth CLI verb — deletes every Player row (and, via ON DELETE
// CASCADE, every PlayerData/PlayerOverride/PlayerAttribute/PlayerAlias row
// with it) so the pool can be rebuilt from scratch entirely through the
// male-only/born-1939-or-later SPARQL filters WikidataClient
// now applies (REQ-112). A bulk, unscoped purge — unlike
// clean-stale-club-attributes above, which only ever touches the named
// clubs — needs its own, stronger safety gate: a required, exact
// confirmation-phrase argument, the same extra-friction-for-a-destructive-
// write pattern infra/scripts/promote-dev-to-prod.sh already uses
// ("promote to prod") for its own bulk write to real player-facing data.
// Run once, then trigger warm-player-cache.yml to repopulate the pool
// under the new filters. Reference tables (CountryDefinition/
// ClubDefinition/TrophyDefinition) and account/game-history tables (User/
// League/Round/GridInstance/GridCell/Guess) are deliberately untouched —
// Guess.PlayerAnswerId has no FK constraint on Player (see
// XGArcadeDbContext.cs's OnModelCreating), so an old Guess whose answer was
// one of the purged players keeps its already-computed IsCorrect/score, it
// just can no longer display which player that answer was.
if (args is ["purge-player-pool", ..])
{
    const string requiredConfirmationPhrase = "delete all player data";
    var purgeConfirmationArg = args.Length > 1 ? args[1] : null;
    if (purgeConfirmationArg != requiredConfirmationPhrase)
        throw new InvalidOperationException(
            $"purge-player-pool requires the exact confirmation phrase as its argument: `purge-player-pool \"{requiredConfirmationPhrase}\"`.");

    var purgeConfig = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();

    var purgeConnectionString = purgeConfig.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

    var purgeDbContextOptions = new DbContextOptionsBuilder<XGArcadeDbContext>()
        .UseNpgsql(purgeConnectionString)
        .Options;

    await using var purgeDbContext = new XGArcadeDbContext(purgeDbContextOptions);
    var purgedPlayerCount = await purgeDbContext.Players.ExecuteDeleteAsync();

    Console.WriteLine($"purge-player-pool: deleted {purgedPlayerCount} Player row(s) (and their cascaded PlayerData/PlayerOverride/PlayerAttribute/PlayerAlias rows).");
    return;
}

// Single source of truth for WikidataClient's HttpClient config — shared by
// the real AddHttpClient<IWikidataClient, WikidataClient> DI registration
// below and the warm-player-cache CLI verb above (which can't use that DI
// registration, since it returns before WebApplication.CreateBuilder ever
// runs). A local function declaration is visible throughout this file's
// top-level statements regardless of textual position, so this can stay
// defined here rather than duplicated at the top.
// REQ-606/REQ-717: the partition key the auth-signup/auth-login/auth-guest
// rate-limit policies above key their per-IP counters on. TestServer (WebApplicationFactory)
// leaves Connection.RemoteIpAddress null, so every request in a given test
// host collapses onto the same "unknown" partition — that's fine, it's what
// makes AuthEndpointTests.cs's REQ606 tests able to trip the limit
// deterministically with a same-process burst of requests rather than
// needing a real distinct client IP or a mocked clock.
static string GetClientIpPartitionKey(HttpContext httpContext) =>
    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

static void ConfigureWikidataHttpClient(HttpClient client)
{
    client.BaseAddress = new Uri("https://query.wikidata.org/");
    // WDQS's own etiquette guidance asks for an identifiable User-Agent
    // rather than a generic HttpClient default.
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "xG-Arcade/1.0 (Tier 0 grid data sync; see docs/decisions/0011-wikidata-first-lookup-waterfall.md)");
}

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

var corsAllowedOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    // REQ-606: restricted to known frontend origin(s), never a wildcard.
    // No configured origin (e.g. before DEV_FRONTEND_HOSTNAME is filled in
    // post-first-deploy) means the policy allows nothing rather than falling
    // back to permissive.
    options.AddPolicy("Frontend", policy => policy.WithOrigins(corsAllowedOrigins).AllowAnyHeader().AllowAnyMethod());
});

// REQ-606: rate limiting scoped narrowly to POST /auth/signup and
// POST /auth/login (AuthController's [EnableRateLimiting("auth-signup"/
// "auth-login")] attributes below) — not every endpoint, per REQ-606's own
// scoping. REQ-717/ADR-0036 added a third, POST /auth/guest ("auth-guest").
// Three separate named policies so exhausting one endpoint's limit never
// blocks the others. Partitioned per client IP (GetClientIpPartitionKey
// below): a fixed 1-minute window, no queueing — a request over
// the limit is rejected immediately with 429 (OnRejected/RejectionStatusCode
// below), never silently queued or left to fall through as a generic 500.
// Uses ASP.NET Core's built-in Microsoft.AspNetCore.RateLimiting middleware
// (available since .NET 7, part of the shared framework) — no new package.
// Configurable rather than a bare literal: REQ-606 fixes signup/login's
// production value at 10/min, but ci.yml's E2E job runs the whole Playwright
// suite (signup + auto-login per test, across every spec file) against one
// shared backend process from a single CI-runner IP within the same
// fixed window — a fundamentally different traffic shape than the
// abuse scenario REQ-606 targets. ci.yml overrides both signup/login values
// via RateLimiting__AuthSignupPermitLimit/AuthLoginPermitLimit env vars for
// that job only; every other environment (including local dev) falls
// back to REQ-606's specified 10, unchanged.
var authSignupPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:AuthSignupPermitLimit") ?? 10;
var authLoginPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:AuthLoginPermitLimit") ?? 10;
// REQ-717/ADR-0036: deliberately tighter than signup/login's 10/min default
// — an anonymous sign-in has no email step at all to slow down scripting
// (not even a plausible-looking address to type), making it the cheapest
// identity to mint at scale of the three endpoints here; a real person
// retrying a flaky network call a couple of times is still comfortably
// inside 3/min, while a scripted loop is capped far below what 10/min would
// allow. Same override mechanism as the other two
// (RateLimiting:AuthGuestPermitLimit) if this default ever needs tuning —
// ci.yml doesn't currently exercise POST /auth/guest at all (no frontend
// guest flow yet), so it needs no override today; add one the same way as
// the other two the moment an E2E spec starts calling this endpoint.
var authGuestPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:AuthGuestPermitLimit") ?? 3;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Errors as problem-details (docs/coding-guidelines.md): the framework's
    // own rejection response has no body by default, so this gives the
    // frontend the same {title, detail} shape every other error response
    // uses (AuthScreen.tsx's describeError already reads exactly this shape,
    // no special-casing needed there).
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new
            {
                title = "Too many attempts",
                detail = "Too many attempts. Please wait a minute and try again.",
                status = StatusCodes.Status429TooManyRequests,
            },
            cancellationToken);
    };

    options.AddPolicy("auth-signup", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        GetClientIpPartitionKey(httpContext),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authSignupPermitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    options.AddPolicy("auth-login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        GetClientIpPartitionKey(httpContext),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authLoginPermitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    options.AddPolicy("auth-guest", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        GetClientIpPartitionKey(httpContext),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authGuestPermitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var databaseConnectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

builder.Services.AddDbContext<XGArcadeDbContext>(options =>
    options.UseNpgsql(databaseConnectionString));

// COMP-06 (Data.PlayerStore) — the only path to category/player data;
// see architecture-document.md boundary rule 1.
builder.Services.AddScoped<ICategoryValueRepository, CategoryValueRepository>();
builder.Services.AddScoped<IPlayerStoreRepository, PlayerStoreRepository>();

// COMP-10 (Data.PlayerNameIndex) — REQ-207's autocomplete-only data source,
// deliberately a separate repository/interface from IPlayerStoreRepository
// above (never merged — see ADR-0007 and architecture-document.md boundary
// rule 5).
builder.Services.AddScoped<IPlayerNameIndexRepository, PlayerNameIndexRepository>();

// COMP-01 (Core.Users) — the only path to the local User profile table.
builder.Services.AddScoped<IUserRepository, UserRepository>();
// REQ-710: reusable anonymize/delete logic — AuthController's self-service
// DeleteAccount endpoint and (per docs/backlog.md S-026) a future
// admin-triggered endpoint both call this, never a second implementation.
builder.Services.AddScoped<IAccountDeletionService, AccountDeletionService>();

// COMP-02 (Core.Leagues) — S-011's REQ-401 (global league auto-membership)
// and the global-leaderboard read path. REQ-406/407/408 (S-053/S-054)
// extended ILeaderboardService to also depend on IRoundRepository (COMP-03)
// and ILiveRoundContributionService (COMP-04, registered below) — DI
// resolves the dependency graph regardless of registration order.
builder.Services.AddScoped<ILeagueRepository, LeagueRepository>();
builder.Services.AddScoped<ILeaderboardService, LeaderboardService>();
// REQ-402/403: custom league create/join — a stateless RNG-based generator
// (AddSingleton is fine, same as TimeProvider.System above) plus the
// scoped service that owns the collision-retry/membership logic around it.
builder.Services.AddSingleton<IInviteCodeGenerator, InviteCodeGenerator>();
builder.Services.AddScoped<ILeagueService, LeagueService>();

// COMP-07 (DataSync.Clients), Tier 0 half: SPARQL against Wikidata Query
// Service, per implementation-document.md §6a. No API-Football fallback
// client yet — that's Tier 1 (ADR-0011). ConfigureWikidataHttpClient (local
// function, defined below main's top-level statements) is the single
// source of truth for BaseAddress/User-Agent — also used by the
// `warm-player-cache` CLI verb above, which can't go through this DI
// registration since it returns before WebApplication.CreateBuilder ever
// runs. Keeping this in one place means the two can't silently drift.
builder.Services.AddHttpClient<IWikidataClient, WikidataClient>(ConfigureWikidataHttpClient);
builder.Services.AddScoped<IWikidataLookupService, WikidataLookupService>();

// COMP-05 (Games.XGGrid) — S-007's grid generation.
builder.Services.AddSingleton(new GridGenerationOptions());
builder.Services.AddScoped<IGridInstanceRepository, GridInstanceRepository>();
builder.Services.AddScoped<IGameModule, GridGameModule>();
builder.Services.AddScoped<IGameModuleResolver, GameModuleResolver>();

// COMP-03 (Core.Rounds) — S-008's round generation/scheduling (REQ-301) and
// round-close (EndTime pull-forward). RoundCloseService's REQ-205 score
// locking is delegated to Core.Scoring's IScoreLockingService, registered
// below — DI resolves the dependency graph regardless of registration
// order, so the forward reference here is fine.
builder.Services.AddSingleton(TimeProvider.System);
// RoundDuration's default is now appsettings-bound (same pattern as
// Internal:JobToken below) rather than hardcoded — REQ-301's "play
// frequency can be adjusted without a code change": change
// RoundScheduling:RoundDurationHours (or the deployed Container App's
// RoundScheduling__RoundDurationHours env var) instead of editing this
// file. generate-round.yml's cron is daily and, thanks to
// RoundGenerationService's own idempotency check, only actually generates a
// new round roughly every RoundDuration — it no longer needs hand-matching
// against this value the way the old Tue/Fri cadence did. See
// RoundSchedulingOptions' own doc comment and NOTES.md for the full
// derivation.
var roundDurationHours = builder.Configuration.GetValue<double?>("RoundScheduling:RoundDurationHours") ?? 48;
builder.Services.AddSingleton(new RoundSchedulingOptions
{
    GameKey = GridGameModule.XGGridGameKey,
    RoundDuration = TimeSpan.FromHours(roundDurationHours),
});
builder.Services.AddScoped<IRoundRepository, RoundRepository>();
builder.Services.AddScoped<IRoundGenerationService, RoundGenerationService>();
builder.Services.AddScoped<IRoundCloseService, RoundCloseService>();

// COMP-04 (Core.Scoring) — S-009's guess submission (REQ-201/202/203/208/210)
// and S-011's score locking (REQ-205, IScoreLockingService — Core.Rounds'
// RoundCloseService calls this rather than computing scores itself).
builder.Services.AddScoped<IGuessRepository, GuessRepository>();
builder.Services.AddScoped<IGuessSubmissionService, GuessSubmissionService>();
builder.Services.AddScoped<IScoreLockingService, ScoreLockingService>();
// REQ-406/407 (ADR-0031): the shared live per-cell contribution formula
// Core.Leagues' ILeaderboardService folds into the shared total (REQ-406)
// and exposes standalone (REQ-407) — recomputed on every call, never
// cached, per ADR-0031.
builder.Services.AddScoped<ILiveRoundContributionService, LiveRoundContributionService>();

// ci.yml's local E2E stack has no live Supabase project to call, so it sets
// Auth:Mode=local-e2e to swap in a fake ISupabaseAuthClient + a locally
// signed JWT instead. Re-check the environment here rather than trusting
// the config flag alone — same "never guarded only by config/an attribute"
// principle CLAUDE.md establishes for COMP-09's Testing.SeedManager
// (ADR-0006) — so this can never accidentally activate outside Development.
var useLocalE2EAuth = builder.Configuration["Auth:Mode"] == "local-e2e" && builder.Environment.IsDevelopment();

if (useLocalE2EAuth)
{
    builder.Services.AddSingleton<ISupabaseAuthClient, LocalE2EAuthClient>();
}
else
{
    // Signup/login are mediated through Supabase Auth's REST API rather
    // than the frontend calling Supabase directly — see ADR-0013.
    var supabaseUrl = builder.Configuration["Supabase:Url"]
        ?? throw new InvalidOperationException("Supabase:Url is not configured.");
    var supabaseAnonKey = builder.Configuration["Supabase:AnonKey"]
        ?? throw new InvalidOperationException("Supabase:AnonKey is not configured.");
    // REQ-710/ADR-0026: a separate, more-privileged key — never the anon key
    // above — required only for SupabaseAuthClient.DeleteUserAsync's call to
    // Supabase's Admin API. Registered as its own tiny DI type
    // (SupabaseServiceRoleKey) so it flows into SupabaseAuthClient's
    // constructor via the same AddHttpClient<,> typed-client activation as
    // httpClient itself — see that class's doc comment for why this call
    // doesn't get a second HttpClient. ci.yml's local E2E stack
    // (useLocalE2EAuth above) never constructs a SupabaseAuthClient at all,
    // so it never needs this registered.
    var supabaseServiceRoleKey = builder.Configuration["Supabase:ServiceRoleKey"]
        ?? throw new InvalidOperationException("Supabase:ServiceRoleKey is not configured.");
    builder.Services.AddSingleton(new SupabaseServiceRoleKey(supabaseServiceRoleKey));

    builder.Services.AddHttpClient<ISupabaseAuthClient, SupabaseAuthClient>(client =>
    {
        client.BaseAddress = new Uri(supabaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("apikey", supabaseAnonKey);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {supabaseAnonKey}");
    });
}

// JWT validation middleware (REQ-606's pipeline): backend never manages
// passwords, only validates the tokens Supabase Auth (or, in local-e2e
// mode, LocalE2EAuthClient) already issued.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep claim types as issued ("sub", "role", ...) instead of ASP.NET
        // Core's legacy remap to long XML-Soap URIs.
        options.MapInboundClaims = false;

        if (useLocalE2EAuth)
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = LocalE2EAuth.SigningKey,
                ValidateIssuer = true,
                ValidIssuer = LocalE2EAuth.Issuer,
                ValidateAudience = true,
                ValidAudience = LocalE2EAuth.Audience,
                ValidateLifetime = true,
            };
        }
        else
        {
            var supabaseUrl = builder.Configuration["Supabase:Url"]
                ?? throw new InvalidOperationException("Supabase:Url is not configured.");

            // ADR-0017: Supabase signs production tokens with its rotating
            // asymmetric JWT Signing Keys system (a `kid` header claim
            // identifies which key), verified via a JWKS endpoint — not a
            // static shared secret, which is what this branch assumed until
            // a real deployment surfaced IDX10503 "Number of keys in
            // Configuration: '0'" (NOTES.md, 2026-07-10). The path is
            // configurable (not a bare literal) so it can be corrected via
            // an env var alone, no rebuild, if live testing shows it wrong.
            var jwksPath = builder.Configuration["Auth:SupabaseJwksPath"] ?? "/auth/v1/.well-known/jwks.json";
            var jwksAddress = supabaseUrl.TrimEnd('/') + jwksPath;

            options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                jwksAddress,
                new SupabaseJwksConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = jwksAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase) });

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                ValidateIssuer = true,
                ValidIssuer = $"{supabaseUrl.TrimEnd('/')}/auth/v1",
                ValidateAudience = true,
                ValidAudience = "authenticated",
                ValidateLifetime = true,
            };

            // The one time this matters is exactly when the JWKS fetch/parse
            // itself is broken (e.g. wrong path) — the default JwtBearer
            // failure log gives no indication why. See ADR-0017's
            // rollout-risk note: this is the log line that turns the next
            // failed login into an actionable message instead of another
            // bare signature-mismatch dead end.
            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("XGArcade.Api.Auth.SupabaseJwt");
                    logger.LogError(context.Exception,
                        "JWT validation failed (JWKS endpoint: {JwksAddress}).", jwksAddress);
                    return Task.CompletedTask;
                },
            };
        }
    });

// S-012: admin-only endpoints (Admin/AdminEndpoints.cs) check the "Admin"
// policy below, backed by AdminAuthorizationHandler's Admin__UserIds check —
// see architecture-document.md's security pipeline and implementation-
// document.md §4.
builder.Services.AddSingleton<IAuthorizationHandler, AdminAuthorizationHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy => policy.Requirements.Add(new AdminRequirement()));
});

builder.Services.AddControllers();

var app = builder.Build();

// ADR-0017's rollout-risk mitigation: fires unconditionally at boot, before
// anyone can even attempt to log in, so the very first thing visible in the
// log stream after a deploy is the resolved JWKS address — if the path is
// wrong, that's visible within seconds of checking, not after a confused
// user reports a login failure.
if (!useLocalE2EAuth)
{
    // Safe: the AddJwtBearer setup above already did `?? throw` on this
    // exact key for this same (!useLocalE2EAuth) branch — if we reached
    // here, it was present.
    var configuredSupabaseUrl = app.Configuration["Supabase:Url"]!;
    var configuredJwksPath = app.Configuration["Auth:SupabaseJwksPath"] ?? "/auth/v1/.well-known/jwks.json";
    app.Logger.LogInformation(
        "JWT validation configured against Supabase JWKS endpoint {JwksAddress}.",
        configuredSupabaseUrl.TrimEnd('/') + configuredJwksPath);
}

// Configure the HTTP request pipeline.

app.UseHttpsRedirection();

app.UseCors("Frontend");

// REQ-606: before authentication, so an unauthenticated brute-force burst
// against /auth/signup or /auth/login is rejected as cheaply as possible —
// matches the recommended ordering for Microsoft.AspNetCore.RateLimiting
// (after routing/CORS, no requirement to run after authentication since the
// two endpoints it applies to are both anonymous anyway).
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapInternalGridEndpoints();
app.MapInternalRoundEndpoints();
app.MapRoundEndpoints();
app.MapGuessEndpoints();
app.MapLeaderboardEndpoints();
app.MapLeagueEndpoints();
app.MapAdminEndpoints();
// S-026: REQ-505/506, non-Production only — see that file's own doc comment
// for why these are kept separate from MapAdminEndpoints above.
app.MapAdminManagementEndpoints();
app.MapPlayerAutocompleteEndpoints();

app.Run();

// Marker partial (global namespace, matching the compiler-generated Program
// class from the top-level statements above) so WebApplicationFactory<Program>
// in XGArcade.Api.Tests can reference it across the assembly boundary.
public partial class Program;

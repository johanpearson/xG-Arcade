using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Repositories;
using XGArcade.Data.Seeding;
using XGArcade.DataSync;
using XGArcade.DataSync.Wikidata;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.CompositionRoot;

// Every `dotnet run -- <verb>` entry point the backend supports, besides the
// normal server start. Each of these builds its own dependencies directly
// rather than going through the full WebApplication DI container, since they
// all run before (and instead of) WebApplication.CreateBuilder. Extracted out
// of Program.cs (S-102) as a pure reorganization — no behavior change; see
// each verb's own comment for why it exists.
//
// S-112 (docs/backlog.md, pure refactor — no behavior change): restructured
// from a single ~667-line sequential TryHandleAsync into a lookup-table
// dispatch, same shape as WikidataClient's spec-table-plus-shared-driver
// refactor (S-100/S-101) — a Verbs dictionary maps each literal verb string
// to its own named handler method, so TryHandleAsync itself is just a
// lookup. Two match shapes existed before this refactor and both are
// preserved exactly:
//   - "exact-match" verbs (migrate-and-seed, warm-player-cache,
//     import-player-name-index, backfill-player-photos,
//     backfill-player-position-birthyear, prefetch-player-careers,
//     verify-wikidata-player-data, audit-club-gaps): the old code matched
//     `args is ["verb"]` only — any extra argument meant "not this verb,"
//     so TryHandleAsync returned false and the caller fell through to
//     starting the normal web server. Each such handler below now starts
//     with an explicit `if (args.Length != 1) return false;` to reproduce
//     that silent-fallthrough exactly, since the dictionary lookup itself
//     only keys on args[0].
//   - "prefix-match" verbs (clean-stale-club-attributes,
//     clear-pair-lookup-failures, clean-duplicate-career-stints,
//     purge-player-pool): the old code matched `args is ["verb", ..]` —
//     the verb alone, regardless of what followed — then validated the
//     remaining args itself and threw InvalidOperationException on a
//     malformed shape rather than ever falling through. Each such handler
//     below keeps that same internal validation and throw, unchanged.
public static class CliVerbDispatcher
{
    private static readonly Dictionary<string, Func<string[], Task<bool>>> Verbs = new()
    {
        ["migrate-and-seed"] = HandleMigrateAndSeedAsync,
        ["warm-player-cache"] = HandleWarmPlayerCacheAsync,
        ["import-player-name-index"] = HandleImportPlayerNameIndexAsync,
        ["backfill-player-photos"] = HandleBackfillPlayerPhotosAsync,
        ["backfill-player-position-birthyear"] = HandleBackfillPlayerPositionBirthYearAsync,
        ["prefetch-player-careers"] = HandlePrefetchPlayerCareersAsync,
        ["verify-wikidata-player-data"] = HandleVerifyWikidataPlayerDataAsync,
        ["audit-club-gaps"] = HandleAuditClubGapsAsync,
        ["clean-stale-club-attributes"] = HandleCleanStaleClubAttributesAsync,
        ["clear-pair-lookup-failures"] = HandleClearPairLookupFailuresAsync,
        ["clean-duplicate-career-stints"] = HandleCleanDuplicateCareerStintsAsync,
        ["purge-player-pool"] = HandlePurgePlayerPoolAsync,
    };

    // Returns true if args matched a known CLI verb and it was handled (the
    // caller should exit immediately after); false if args names no known
    // verb and the caller should fall through to starting the normal server.
    public static async Task<bool> TryHandleAsync(string[] args)
    {
        if (args.Length == 0 || !Verbs.TryGetValue(args[0], out var handler))
            return false;

        return await handler(args);
    }

    // S-114 (pure refactor, follow-up to S-112's own doc comment above): the
    // shared boilerplate every handler below used to repeat inline — read
    // ConnectionStrings:Database from environment variables and build a
    // configured XGArcadeDbContext, throwing the same
    // InvalidOperationException today's callers already depend on when it's
    // missing. One shared place instead of ten copies.
    private static XGArcadeDbContext BuildDbContext()
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var connectionString = config.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new XGArcadeDbContext(options);
    }

    // S-114: same shared-boilerplate extraction as BuildDbContext above, for
    // the 6 handlers that also need an ILoggerFactory.
    private static ILoggerFactory BuildLoggerFactory() =>
        LoggerFactory.Create(b => b
            .AddConsole()
            .SetMinimumLevel(LogLevel.Information));

    // `dotnet run -- migrate-and-seed` is a distinct CLI verb (not a normal
    // server start) used by ci.yml's local E2E stack. Applies pending EF Core
    // migrations against ConnectionStrings:Database, then seeds Tier 0's
    // hand-curated reference data (S-005) — idempotent, safe to re-run.
    private static async Task<bool> HandleMigrateAndSeedAsync(string[] args)
    {
        if (args.Length != 1)
            return false;

        await using var migrationDbContext = BuildDbContext();
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
        // Bug-bundle fix (2026-07-27): backfills PlayerNameIndexWord for any
        // PlayerNameIndex row imported before that table existed — see
        // PlayerNameIndexWordBackfiller's own doc comment.
        await PlayerNameIndexWordBackfiller.BackfillAsync(migrationDbContext);
        // Bug-bundle fix (2026-08-02): backfills PlayerAlias.NormalizedAlias for
        // any row persisted before PlayerNameNormalizer's non-decomposable-
        // Latin-letter fix (Ø/Æ/Œ/Đ/Ł/ß/Þ) — see
        // PlayerAliasNormalizedAliasBackfiller's own doc comment. Note:
        // Player.NormalizedFullName needs no equivalent new wiring here —
        // PlayerNormalizedFullNameBackfiller above already re-derives it from
        // Player.FullName under whatever PlayerNameNormalizer.Normalize
        // currently does, so this same fix is picked up for free on the very
        // next migrate-and-seed run (which deploy.yml already runs on every
        // push to main).
        await PlayerAliasNormalizedAliasBackfiller.BackfillAsync(migrationDbContext);

        Console.WriteLine("migrate-and-seed: migrations applied, reference data seeded.");
        return true;
    }

    // REQ-110 (ADR-0023's follow-up): `dotnet run -- warm-player-cache` is a
    // second distinct CLI verb, same shape as migrate-and-seed above but run
    // by its own workflow (warm-player-cache.yml), manually, after any
    // reference-data change — never inside a synchronous HTTP request (see
    // PlayerCacheWarmingService's own doc comment for why). Builds its
    // dependencies directly rather than spinning up the full WebApplication
    // DI container, same as migrate-and-seed does.
    private static async Task<bool> HandleWarmPlayerCacheAsync(string[] args)
    {
        if (args.Length != 1)
            return false;

        using var warmingLoggerFactory = BuildLoggerFactory();

        await using var warmingDbContext = BuildDbContext();
        var warmingCategoryValueRepository = new CategoryValueRepository(warmingDbContext);
        // S-106/S-107 (pure refactor): the sibling repositories
        // WikidataLookupService/PlayerCacheWarmingService now depend on,
        // split out of the original, now-deleted IPlayerStoreRepository
        // — same split as CompositionRoot/ServiceRegistration.cs's DI
        // registrations, just built by hand here since this verb runs
        // before WebApplication.CreateBuilder. See ADR-0067.
        var warmingPlayerCareerStintRepository = new PlayerCareerStintRepository(warmingDbContext);
        var warmingPlayerDataQualityRepository = new PlayerDataQualityRepository(warmingDbContext);
        var warmingPlayerRepository = new PlayerRepository(warmingDbContext);
        var warmingPlayerAttributeRepository = new PlayerAttributeRepository(warmingDbContext);
        var warmingPlayerAliasRepository = new PlayerAliasRepository(warmingDbContext);
        var warmingPlayerDataRepository = new PlayerDataRepository(warmingDbContext);

        using var warmingHttpClient = new HttpClient();
        WikidataHttpClientConfiguration.Configure(warmingHttpClient);
        var warmingWikidataClient = new WikidataClient(warmingHttpClient, logger: warmingLoggerFactory.CreateLogger<WikidataClient>());
        var warmingWikidataLookupService = new WikidataLookupService(
            warmingWikidataClient, warmingPlayerCareerStintRepository,
            warmingPlayerRepository, warmingPlayerAttributeRepository, warmingPlayerAliasRepository, warmingPlayerDataRepository);

        var warmingService = new PlayerCacheWarmingService(
            warmingCategoryValueRepository, warmingPlayerDataQualityRepository, warmingPlayerAttributeRepository, warmingWikidataLookupService,
            new GridGenerationOptions(), warmingLoggerFactory.CreateLogger<PlayerCacheWarmingService>());

        await warmingService.WarmAsync();

        Console.WriteLine("warm-player-cache: complete.");
        return true;
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
    private static async Task<bool> HandleImportPlayerNameIndexAsync(string[] args)
    {
        if (args.Length != 1)
            return false;

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
        WikidataHttpClientConfiguration.Configure(importHttpClient);
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
        return true;
    }

    // REQ-214 backfill (S-045): `dotnet run -- backfill-player-photos` is a
    // sixth distinct CLI verb — same shape as warm-player-cache above (builds
    // its dependencies directly rather than the full DI container, since it
    // runs before WebApplication.CreateBuilder), run manually via its own
    // workflow (backfill-player-photos.yml, workflow_dispatch only). See
    // PlayerPhotoBackfillService's own doc comment for the full "why a CLI
    // verb, not an HTTP endpoint or background task" reasoning — squarely
    // inside ADR-0024's existing decision, not a new one.
    private static async Task<bool> HandleBackfillPlayerPhotosAsync(string[] args)
    {
        if (args.Length != 1)
            return false;

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
        var backfillPlayerBackfillRepository = new PlayerBackfillRepository(backfillDbContext);

        using var backfillHttpClient = new HttpClient();
        WikidataHttpClientConfiguration.Configure(backfillHttpClient);
        var backfillWikidataClient = new WikidataClient(
            backfillHttpClient, logger: backfillLoggerFactory.CreateLogger<WikidataClient>());

        var backfillService = new PlayerPhotoBackfillService(
            backfillPlayerBackfillRepository, backfillWikidataClient,
            backfillLoggerFactory.CreateLogger<PlayerPhotoBackfillService>());

        var backfillResult = await backfillService.BackfillAsync();

        Console.WriteLine(
            $"backfill-player-photos: complete — {backfillResult.BatchesProcessed} batch(es) processed, " +
            $"{backfillResult.PlayersBackfilled} player(s) backfilled, {backfillResult.BatchesFailed} batch(es) failed.");
        return true;
    }

    // REQ-1207 backfill (bug-bundle fix, 2026-08-02): `dotnet run --
    // backfill-player-position-birthyear` — same shape as backfill-player-photos
    // above (builds its dependencies directly rather than the full DI
    // container), run manually via its own workflow
    // (backfill-player-position-birthyear.yml, workflow_dispatch only). See
    // PlayerPositionBirthYearBackfillService's own doc comment for the full "why
    // a CLI verb, not an HTTP endpoint or background task" reasoning — squarely
    // inside ADR-0024's existing decision, not a new one.
    private static async Task<bool> HandleBackfillPlayerPositionBirthYearAsync(string[] args)
    {
        if (args.Length != 1)
            return false;

        var positionBirthYearBackfillConfig = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var positionBirthYearBackfillConnectionString = positionBirthYearBackfillConfig.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

        var positionBirthYearBackfillDbContextOptions = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseNpgsql(positionBirthYearBackfillConnectionString)
            .Options;

        using var positionBirthYearBackfillLoggerFactory = LoggerFactory.Create(b => b
            .AddConsole()
            .SetMinimumLevel(LogLevel.Information));

        await using var positionBirthYearBackfillDbContext = new XGArcadeDbContext(positionBirthYearBackfillDbContextOptions);
        var positionBirthYearBackfillPlayerBackfillRepository = new PlayerBackfillRepository(positionBirthYearBackfillDbContext);

        using var positionBirthYearBackfillHttpClient = new HttpClient();
        WikidataHttpClientConfiguration.Configure(positionBirthYearBackfillHttpClient);
        var positionBirthYearBackfillWikidataClient = new WikidataClient(
            positionBirthYearBackfillHttpClient, logger: positionBirthYearBackfillLoggerFactory.CreateLogger<WikidataClient>());

        var positionBirthYearBackfillService = new PlayerPositionBirthYearBackfillService(
            positionBirthYearBackfillPlayerBackfillRepository, positionBirthYearBackfillWikidataClient,
            positionBirthYearBackfillLoggerFactory.CreateLogger<PlayerPositionBirthYearBackfillService>());

        var positionBirthYearBackfillResult = await positionBirthYearBackfillService.BackfillAsync();

        Console.WriteLine(
            $"backfill-player-position-birthyear: complete — {positionBirthYearBackfillResult.BatchesProcessed} batch(es) processed, " +
            $"{positionBirthYearBackfillResult.PlayersBackfilled} player(s) backfilled, {positionBirthYearBackfillResult.BatchesFailed} batch(es) failed.");
        return true;
    }

    // ADR-0055: `dotnet run -- prefetch-player-careers` — same shape as
    // warm-player-cache/backfill-player-photos above (builds its dependencies
    // directly rather than the full DI container, since it runs before
    // WebApplication.CreateBuilder), run via its own workflow
    // (prefetch-player-careers.yml, workflow_dispatch only for now — new and
    // unproven, unlike warm-player-cache/import-player-name-index which just
    // moved to a recurring cron on the same date; put this on a schedule too
    // once a real run has confirmed its cost/runtime). See
    // PlayerCareerPrefetchService's own doc comment for the full "why" —
    // squarely inside ADR-0024's existing "bulk job is a CLI verb" decision, not
    // a new one.
    private static async Task<bool> HandlePrefetchPlayerCareersAsync(string[] args)
    {
        if (args.Length != 1)
            return false;

        var prefetchConfig = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var prefetchConnectionString = prefetchConfig.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

        var prefetchDbContextOptions = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseNpgsql(prefetchConnectionString)
            .Options;

        using var prefetchLoggerFactory = LoggerFactory.Create(b => b
            .AddConsole()
            .SetMinimumLevel(LogLevel.Information));

        await using var prefetchDbContext = new XGArcadeDbContext(prefetchDbContextOptions);
        var prefetchCategoryValueRepository = new CategoryValueRepository(prefetchDbContext);
        var prefetchPlayerCareerStintRepository = new PlayerCareerStintRepository(prefetchDbContext);
        // S-106/S-107 (pure refactor): GetOrCreatePlayersByWikidataQidAsync's
        // new home — same split as CompositionRoot/ServiceRegistration.cs's
        // DI registration, built by hand here since this verb runs before
        // WebApplication.CreateBuilder. See ADR-0067.
        var prefetchPlayerRepository = new PlayerRepository(prefetchDbContext);

        using var prefetchHttpClient = new HttpClient();
        WikidataHttpClientConfiguration.Configure(prefetchHttpClient);
        // 60s, same reasoning as import-player-name-index's own standalone
        // client override just above: WikidataClient's 15s default is tuned
        // (ADR-0011) for the narrow per-cell intersection queries, not this
        // job's 200-QID VALUES-clause career-stint batches. Confirmed live
        // (2026-08-02, this job's first real run): 4 of many 200-player
        // career-fetch batches hit the 15s default and timed out — every
        // country's own pool query and the vast majority of career-fetch
        // batches easily finished well under 15s, so this genuinely was the
        // client default being too tight for the occasional heavier batch, not
        // WDQS's ~60s server-side cap (ADR-0055's own flagged risk, and a
        // different failure mode from this one — don't conflate the two next
        // time this job's log is read). 60s is still the right ceiling per
        // that same server-cap lesson: no reason to guess higher.
        var prefetchWikidataClient = new WikidataClient(
            prefetchHttpClient, queryTimeout: TimeSpan.FromSeconds(60),
            logger: prefetchLoggerFactory.CreateLogger<WikidataClient>());

        var prefetchService = new PlayerCareerPrefetchService(
            prefetchCategoryValueRepository, prefetchPlayerCareerStintRepository, prefetchPlayerRepository, prefetchWikidataClient,
            prefetchLoggerFactory.CreateLogger<PlayerCareerPrefetchService>());

        // Deliberately unhandled — PrefetchAsync throws only after every seeded
        // country has been attempted (see its own doc comment), so the process
        // exits nonzero and the workflow run goes red exactly when something
        // needs a re-run, same fail-loud-at-the-end contract as
        // import-player-name-index.
        var prefetchResult = await prefetchService.PrefetchAsync();

        Console.WriteLine(
            $"prefetch-player-careers: complete — {prefetchResult.CountriesProcessed} countr" +
            $"{(prefetchResult.CountriesProcessed == 1 ? "y" : "ies")} processed, " +
            $"{prefetchResult.PlayersTouched} player(s) touched, {prefetchResult.StintsAdded} stint(s) added.");
        return true;
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
    // below. Safe to re-run: a second run simply finds zero matching rows.
    private static async Task<bool> HandleVerifyWikidataPlayerDataAsync(string[] args)
    {
        if (args.Length != 1)
            return false;

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
        return true;
    }

    // `dotnet run -- audit-club-gaps` — a one-off, read-only diagnostic (no
    // REQ/ADR; see ClubGapAuditService's own doc comment for why) to help scope
    // a future seed-list widening decision, run via its own workflow
    // (audit-club-gaps.yml, workflow_dispatch only, no schedule). Same shape as
    // verify-wikidata-player-data above (builds its dependencies directly
    // rather than the full DI container, since it runs before
    // WebApplication.CreateBuilder ever runs) but needs an ILoggerFactory too,
    // since ClubGapAuditService logs its ranked candidate list via ILogger
    // rather than a single Console.WriteLine summary line — same
    // LoggerFactory.Create pattern warm-player-cache uses above. Read-only: no
    // SaveChangesAsync call anywhere on this path.
    private static async Task<bool> HandleAuditClubGapsAsync(string[] args)
    {
        if (args.Length != 1)
            return false;

        var auditConfig = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var auditConnectionString = auditConfig.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

        var auditDbContextOptions = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseNpgsql(auditConnectionString)
            .Options;

        using var auditLoggerFactory = LoggerFactory.Create(b => b
            .AddConsole()
            .SetMinimumLevel(LogLevel.Information));

        await using var auditDbContext = new XGArcadeDbContext(auditDbContextOptions);
        var auditPlayerDataQualityRepository = new PlayerDataQualityRepository(auditDbContext);

        var auditService = new ClubGapAuditService(auditPlayerDataQualityRepository, auditLoggerFactory.CreateLogger<ClubGapAuditService>());

        await auditService.RunAsync();

        Console.WriteLine("audit-club-gaps: complete.");
        return true;
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
    private static async Task<bool> HandleCleanStaleClubAttributesAsync(string[] args)
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
        return true;
    }

    // 2026-08-01 live-incident follow-up to ADR-0052: `dotnet run --
    // clear-pair-lookup-failures` is a seventh distinct CLI verb — see
    // PairLookupFailureCleaner's own doc comment for the full reasoning (why
    // this exists as a narrower, pair-scoped alternative to
    // clean-stale-club-attributes above, which is club-name-scoped and would
    // wipe far more cached data than intended for this specific incident).
    //
    // No required argument, unlike clean-stale-club-attributes: the whole point
    // of this tool is that it reads the stuck-pair list from the database
    // itself (every PairLookupFailure row at/above
    // PlayerCacheWarmingService.PersistentFailureThreshold), rather than
    // requiring an operator to hand-derive it — GitHub Actions log text only
    // ever names pairs that were queried and failed, never ones that were
    // skipped for already being past the threshold, so no log ever contains the
    // true full list.
    //
    // Matched on the verb alone (not exact-length) for the same reason as
    // clean-stale-club-attributes above: a malformed invocation should fail
    // loudly via an explicit exception rather than silently falling through to
    // WebApplication.CreateBuilder and starting the full server. This verb
    // takes no arguments, so any extra argument is itself the malformed case.
    private static async Task<bool> HandleClearPairLookupFailuresAsync(string[] args)
    {
        if (args.Length > 1)
            throw new InvalidOperationException(
                $"clear-pair-lookup-failures takes no arguments, got '{string.Join(" ", args.Skip(1))}'.");

        var clearFailuresConfig = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var clearFailuresConnectionString = clearFailuresConfig.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

        var clearFailuresDbContextOptions = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseNpgsql(clearFailuresConnectionString)
            .Options;

        await using var clearFailuresDbContext = new XGArcadeDbContext(clearFailuresDbContextOptions);

        var clearedPairNames = await PairLookupFailureCleaner.ClearPersistentFailuresAsync(clearFailuresDbContext);

        Console.WriteLine(clearedPairNames.Count > 0
            ? $"clear-pair-lookup-failures: removed {clearedPairNames.Count} PairLookupFailure row(s): {string.Join(", ", clearedPairNames)}."
            : "clear-pair-lookup-failures: removed 0 PairLookupFailure row(s) — nothing was at or above the persistent-failure threshold.");
        return true;
    }

    // Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203 follow-up,
    // ADR-0059): `dotnet run -- clean-duplicate-career-stints` — see
    // DuplicateCareerStintCleaner's own doc comment for the full "why this
    // exists and why it's a narrow, provable-only cleanup rather than a full
    // purge-and-reseed." No required argument, same "reads its own scope from
    // the database" shape as clear-pair-lookup-failures above — an already-
    // canonical ClubDefinition-vs-PlayerCareerStint comparison, not an
    // operator-supplied club list.
    private static async Task<bool> HandleCleanDuplicateCareerStintsAsync(string[] args)
    {
        if (args.Length > 1)
            throw new InvalidOperationException(
                $"clean-duplicate-career-stints takes no arguments, got '{string.Join(" ", args.Skip(1))}'.");

        var cleanDuplicateStintsConfig = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var cleanDuplicateStintsConnectionString = cleanDuplicateStintsConfig.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

        var cleanDuplicateStintsDbContextOptions = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseNpgsql(cleanDuplicateStintsConnectionString)
            .Options;

        await using var cleanDuplicateStintsDbContext = new XGArcadeDbContext(cleanDuplicateStintsDbContextOptions);

        var removedDuplicateStintCount = await DuplicateCareerStintCleaner.CleanAsync(cleanDuplicateStintsDbContext);

        Console.WriteLine($"clean-duplicate-career-stints: removed {removedDuplicateStintCount} PlayerCareerStint row(s) " +
            "provably duplicating an already-canonical row for the same player/stint.");
        return true;
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
    //
    // REQ-110 (2026-07-28 "persisted confirmed-low signal" extension): also
    // clears every ConfirmedLowMatchPair row — same hard invariant as
    // clean-stale-club-attributes above (a "purge and re-warm" cycle must force
    // a real, full re-check, never a warm run trusting a stale confirmed-low
    // marker), just unscoped here since this verb's own purge is unscoped too.
    // ConfirmedLowMatchPair has no FK to Player (see its own doc comment — a
    // confirmed-low pair often has no Player rows to reference at all), so
    // deleting Players above doesn't cascade into it; it needs its own explicit
    // delete.
    //
    // REQ-110 (2026-08-01 "persistent technical-failure tracking" extension,
    // ADR-0052): same reasoning again for PairLookupFailure — a "purge and
    // re-warm" cycle must force a real, full re-check, never a warm run
    // trusting a stale skip marker (confirmed-low OR persistent-failure) left
    // over from before the purge.
    private static async Task<bool> HandlePurgePlayerPoolAsync(string[] args)
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
        // Same established exception as purge-player-pool's own Players
        // ExecuteDeleteAsync above (see this verb's own doc comment referencing
        // it) — a standalone operational CLI verb never exercised by the
        // InMemory-provider unit tests that load-then-SaveChangesAsync exists to
        // protect.
        var purgedConfirmedLowCount = await purgeDbContext.ConfirmedLowMatchPairs.ExecuteDeleteAsync();
        var purgedLookupFailureCount = await purgeDbContext.PairLookupFailures.ExecuteDeleteAsync();

        Console.WriteLine($"purge-player-pool: deleted {purgedPlayerCount} Player row(s) (and their cascaded PlayerData/PlayerOverride/PlayerAttribute/PlayerAlias rows), " +
            $"{purgedConfirmedLowCount} ConfirmedLowMatchPair row(s), and {purgedLookupFailureCount} PairLookupFailure row(s).");
        return true;
    }
}

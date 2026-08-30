using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using XGArcade.Api.Auth;
using XGArcade.Api.Avatars;
using XGArcade.Core.Auth;
using XGArcade.Core.Games;
using XGArcade.Core.IncidentReporting;
using XGArcade.Core.Leagues;
using XGArcade.Core.Rounds;
using XGArcade.Core.Scoring;
using XGArcade.Core.Storage;
using XGArcade.Data;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.ApiFootball;
using XGArcade.DataSync.Wikidata;
using XGArcade.Games.XGGrid;
using XGArcade.Games.XGPath;
using XGArcade.Games.XGPredict;
using XGArcade.Storage.Supabase;

namespace XGArcade.Api.CompositionRoot;

// The DI container wiring for every domain service/repository the API
// depends on — database, Core (users/leagues/rounds/scoring), the game
// modules, Wikidata sync, and incident reporting. Extracted out of
// Program.cs (S-102) as a pure reorganization, no behavior change. CORS/rate
// limiting and Supabase/JWT auth have their own group — see AuthSetup.cs.
public static class ServiceRegistration
{
    public static void AddApplicationServices(this WebApplicationBuilder builder)
    {
        var databaseConnectionString = builder.Configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

        builder.Services.AddDbContext<XGArcadeDbContext>(options =>
            options.UseNpgsql(databaseConnectionString));

        // COMP-06 (Data.PlayerStore) — the only path to category/player data;
        // see architecture-document.md boundary rule 1. S-106+S-107 (pure
        // refactor, docs/backlog.md Epic 8) split the original, now-deleted
        // IPlayerStoreRepository's 43 methods into 8 narrower repositories
        // along their entity concern (Player/PlayerData/PlayerAttribute/
        // PlayerAlias/PlayerOverride/PlayerBackfill/PlayerCareerStint/
        // PlayerDataQuality) — all eight are still COMP-06/Data.PlayerStore,
        // and together are the only path to this data; see each interface's
        // own doc comment for its specific slice, and ADR-0067 for the full
        // split rationale.
        builder.Services.AddScoped<ICategoryValueRepository, CategoryValueRepository>();
        builder.Services.AddScoped<IPlayerRepository, PlayerRepository>();
        builder.Services.AddScoped<IPlayerDataRepository, PlayerDataRepository>();
        builder.Services.AddScoped<IPlayerAttributeRepository, PlayerAttributeRepository>();
        builder.Services.AddScoped<IPlayerAliasRepository, PlayerAliasRepository>();
        builder.Services.AddScoped<IPlayerOverrideRepository, PlayerOverrideRepository>();
        builder.Services.AddScoped<IPlayerBackfillRepository, PlayerBackfillRepository>();
        builder.Services.AddScoped<IPlayerCareerStintRepository, PlayerCareerStintRepository>();
        builder.Services.AddScoped<IPlayerDataQualityRepository, PlayerDataQualityRepository>();

        // COMP-10 (Data.PlayerNameIndex) — REQ-207's autocomplete-only data source,
        // deliberately a separate repository/interface from the COMP-06
        // repositories above (never merged — see ADR-0007 and architecture-
        // document.md boundary rule 5).
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
        // (AddSingleton is fine, same as TimeProvider.System below) plus the
        // scoped service that owns the collision-retry/membership logic around it.
        builder.Services.AddSingleton<IInviteCodeGenerator, InviteCodeGenerator>();
        builder.Services.AddScoped<ILeagueService, LeagueService>();

        // COMP-07 (DataSync.Clients), Tier 0 half: SPARQL against Wikidata Query
        // Service, per implementation-document.md §6a. No API-Football fallback
        // client yet — that's Tier 1 (ADR-0011). WikidataHttpClientConfiguration is
        // the single source of truth for BaseAddress/User-Agent — also used by the
        // CLI verbs in CliVerbDispatcher.cs, which can't go through this DI
        // registration since they run before WebApplication.CreateBuilder ever
        // runs. Keeping this in one place means the two can't silently drift.
        builder.Services.AddHttpClient<IWikidataClient, WikidataClient>(WikidataHttpClientConfiguration.Configure);
        builder.Services.AddScoped<IWikidataLookupService, WikidataLookupService>();

        // COMP-05 (Games.XGGrid) — S-007's grid generation. GridSize now lives here
        // (S-084/REQ-1202 follow-up), not on Core.Rounds' RoundSchedulingOptions —
        // see that type's own doc comment for why.
        builder.Services.AddSingleton(new GridGenerationOptions { GridSize = 3 });
        // ADR-0070/S-128: an operational kill switch for REQ-211's guess-time
        // live-lookup fallback only (GridGameModule.ScoreSubmissionAsync) —
        // never for REQ-103's grid-generation-time live lookup, which reads
        // no config from this class at all. Same appsettings-bound-with-
        // fallback-default pattern as RoundScheduling:RoundDurationHours
        // below: change GridLiveLookup:Enabled (or the deployed Container
        // App's GridLiveLookup__Enabled env var) to false to disable it
        // without a code change, defaulting to true (unchanged behavior) if
        // unset.
        var gridLiveLookupEnabled = builder.Configuration.GetValue<bool?>("GridLiveLookup:Enabled") ?? true;
        builder.Services.AddSingleton(new GridLiveLookupOptions { Enabled = gridLiveLookupEnabled });
        builder.Services.AddScoped<IGridInstanceRepository, GridInstanceRepository>();
        // S-119 (pure refactor, docs/backlog.md Epic 9): GridGameModule split
        // into three narrower classes along its own responsibility lines —
        // generation, name matching, and live-lookup dispatch — following the
        // same "independently registered, no facade" convention ADR-0067 used
        // for IPlayerStoreRepository's split. GridGameModule itself is now a
        // thin IGameModule adapter composing all three.
        builder.Services.AddScoped<IGridGenerationService, GridGenerationService>();
        builder.Services.AddScoped<IGridNameMatcher, GridNameMatcher>();
        builder.Services.AddScoped<IGridLiveLookupDispatcher, GridLiveLookupDispatcher>();
        builder.Services.AddScoped<IGameModule, GridGameModule>();
        // COMP-11 (Games.XGPath) — S-081's puzzle generation (REQ-1201/1202),
        // S-082's guess correctness/attempt-cap (REQ-1204/1205) and clue-reveal read
        // endpoint (REQ-1203, GET /path/current). Registered here so
        // IGameModuleResolver.Resolve("xg-path") returns a real module, same as xG
        // Grid above. S-084 adds the RoundSchedulingOptions registration below (a
        // second instance, keyed by GameKey via IRoundSchedulingOptionsResolver) so
        // "xg-path" rounds are now actually generated on a schedule, same as
        // "xg-grid"'s.
        builder.Services.AddScoped<IPathInstanceRepository, PathInstanceRepository>();
        // ADR-0054: XGPathGameModule.GenerateInstanceAsync's own direct Wikidata
        // career fetch — a Games.XGPath-only dependency, registered here rather than
        // alongside IWikidataLookupService above since it's XGPathGameModule's own
        // concern, not a general-purpose lookup service other callers share.
        builder.Services.AddScoped<IPlayerCareerStintRefreshService, PlayerCareerStintRefreshService>();
        // ADR-0056: xG Path's familiarity filter (REQ-1201's target-player
        // eligibility, "would a casual player recognize this name" half) — same
        // Games.XGPath-only, registered-alongside-its-sibling-service reasoning as
        // IPlayerCareerStintRefreshService immediately above.
        builder.Services.AddScoped<IPlayerFamiliarityService, PlayerFamiliarityService>();
        // S-154 (pure refactor, docs/backlog.md Epic 17): XGPathGameModule split —
        // REQ-1201's whole target-player eligibility pipeline extracted into its own
        // narrowly-scoped class, following the same "independently registered, no
        // facade" convention docs/decisions/0068-grid-game-module-responsibility-split.md
        // established for GridGameModule's own split. XGPathGameModule itself is now
        // a thin IGameModule adapter composing this alongside its other dependencies.
        builder.Services.AddScoped<IPathEligibilityService, PathEligibilityService>();
        builder.Services.AddScoped<IGameModule, XGPathGameModule>();
        // COMP-15 (Games.XGPredict)/ADR-0096: REQ-1301 (round generation) and
        // REQ-1302/1303 (prediction submission/round lock) are now real —
        // GenerateInstanceAsync/ScoreSubmissionAsync/GetCellIdsAsync persist
        // through IPredictInstanceRepository below. GetMaxAttemptsForCellAsync
        // remains a TODO (ADR-0096 doesn't decide xG Predict's attempt-cap
        // model, and nothing calls it yet — see that method's own comment).
        // Registered here so IGameModuleResolver.Resolve("xg-predict") returns
        // a real module, same as xG Grid/xG Path above.
        // REQ-1304/ADR-0095: the IScoringStrategy registration for
        // "xg-predict" now exists (below, alongside xG Grid/xG Path's own).
        // RoundSchedulingOptions for "xg-predict" is still deliberately NOT
        // registered (unlike xG Grid/xG Path's own registrations further
        // below) — IRoundSchedulingOptionsResolver iterates an IEnumerable,
        // so nothing requires an entry to exist for this GameKey to
        // compile, and InternalRoundEndpoints'/LeaderboardEndpoints' own
        // gameKey allow-lists don't yet include "xg-predict" either — real
        // round generation/scoring HTTP wiring is a separate, later story
        // (ADR-0096's own explicit scope; mirrors ADR-0051's precedent for
        // deferred scheduling-config wiring).
        builder.Services.AddScoped<IPredictInstanceRepository, PredictInstanceRepository>();
        builder.Services.AddScoped<IGameModule, XGPredictGameModule>();
        // S-084/REQ-1202: PathTemplateResolver's puzzle-count source — mirrors
        // GridGenerationOptions' role/precedent above for xG Path's own generation
        // config (deliberately not a field on RoundSchedulingOptions; see that
        // type's own doc comment).
        builder.Services.AddSingleton(new PathGenerationOptions());
        builder.Services.AddScoped<IGameModuleResolver, GameModuleResolver>();
        // ADR-0040: xG Grid's REQ-204/205 uniqueness formula, extracted into
        // Core.Scoring's IScoringStrategy abstraction. GameKey is supplied here
        // (the composition root), never hardcoded inside XGArcade.Core — same
        // boundary reason as RoundSchedulingOptions.GameKey below (ADR-0003).
        builder.Services.AddScoped<IScoringStrategy>(_ => new UniquenessScoringStrategy
        {
            GameKey = GridGameModule.XGGridGameKey,
        });
        // S-083/REQ-1206/ADR-0040 follow-up: xG Path's clue-efficiency formula,
        // registered against "xg-path" the same way UniquenessScoringStrategy is
        // registered against "xg-grid" above — GameKey supplied here, never
        // hardcoded inside XGArcade.Core (ADR-0003).
        builder.Services.AddScoped<IScoringStrategy>(_ => new ClueEfficiencyScoringStrategy
        {
            GameKey = XGPathGameModule.XGPathGameKey,
        });
        // REQ-1304/ADR-0095: xG Predict's three-component prediction
        // formula, registered against "xg-predict" the same way the two
        // strategies above are registered — GameKey supplied here, never
        // hardcoded inside XGArcade.Core (ADR-0003). Unlike the two above,
        // this strategy's ScoreCorrectGuess is unreachable in production
        // (ADR-0096: xG Predict never writes Guess rows) — see that
        // method's own doc comment.
        builder.Services.AddScoped<IScoringStrategy>(_ => new XGPredictScoringStrategy
        {
            GameKey = XGPredictGameModule.XGPredictGameKey,
        });
        builder.Services.AddScoped<IScoringStrategyResolver, ScoringStrategyResolver>();

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
        // file. generate-grid-round.yml's/generate-path-round.yml's cron
        // (split from a single generate-round.yml, S-136/ADR-0072) is daily
        // for each GameKey and, thanks to
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
        // S-084/REQ-1202: xG Path's own RoundSchedulingOptions instance, resolved
        // independently of xG Grid's via IRoundSchedulingOptionsResolver
        // (registered below) — a distinct config key
        // (RoundScheduling:XGPath:RoundDurationHours) so a lasting change to one
        // game's RoundDuration never affects the other's. Existing
        // RoundScheduling:RoundDurationHours (xG Grid's) is left untouched for
        // back-compat with any already-deployed Container App env var. Default is
        // also 48h — no product reason yet for xG Path to run on a different
        // cadence than xG Grid; change independently via this key (or the
        // deployed Container App's RoundScheduling__XGPath__RoundDurationHours env
        // var) if that changes.
        var xgPathRoundDurationHours = builder.Configuration.GetValue<double?>("RoundScheduling:XGPath:RoundDurationHours") ?? 48;
        builder.Services.AddSingleton(new RoundSchedulingOptions
        {
            GameKey = XGPathGameModule.XGPathGameKey,
            RoundDuration = TimeSpan.FromHours(xgPathRoundDurationHours),
        });
        builder.Services.AddScoped<IRoundSchedulingOptionsResolver, RoundSchedulingOptionsResolver>();
        builder.Services.AddScoped<IRoundRepository, RoundRepository>();
        builder.Services.AddScoped<IRoundGenerationService, RoundGenerationService>();
        builder.Services.AddScoped<IRoundCloseService, RoundCloseService>();

        // COMP-04 (Core.Scoring) — S-009's guess submission (REQ-201/202/203/208/210)
        // and S-011's score locking (REQ-205, IScoreLockingService — Core.Rounds'
        // RoundCloseService calls this rather than computing scores itself).
        builder.Services.AddScoped<IGuessRepository, GuessRepository>();
        builder.Services.AddScoped<IGuessSubmissionService, GuessSubmissionService>();
        builder.Services.AddScoped<IScoreLockingService, ScoreLockingService>();
        // REQ-215/ADR-0052 (S-089): PlayerSuggestion's own repository — see that
        // interface's own doc comment for why this is never folded into any of
        // the COMP-06 repositories above.
        builder.Services.AddScoped<IPlayerSuggestionRepository, PlayerSuggestionRepository>();
        // REQ-511: the site-wide announcement banner's own repository — see
        // IAnnouncementBannerRepository's own doc comment for why this table's
        // singleton invariant lives here rather than in any of the COMP-06
        // repositories or any other existing repository.
        builder.Services.AddScoped<IAnnouncementBannerRepository, AnnouncementBannerRepository>();
        // REQ-406/407 (ADR-0031): the shared live per-cell contribution formula
        // Core.Leagues' ILeaderboardService folds into the shared total (REQ-406)
        // and exposes standalone (REQ-407) — recomputed on every call, never
        // cached, per ADR-0031.
        builder.Services.AddScoped<ILiveRoundContributionService, LiveRoundContributionService>();

        builder.AddIncidentReportingServices();
        builder.AddAvatarStorageServices();
        builder.AddApiFootballServices();
    }

    // REQ-722/ADR-0087 (S-180): AvatarSubmission's own repository plus the
    // IAvatarStorage registration — its own method, same "one focused
    // helper per component" shape AddIncidentReportingServices below
    // already establishes, rather than growing the main body of
    // AddApplicationServices further.
    private static void AddAvatarStorageServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<IAvatarSubmissionRepository, AvatarSubmissionRepository>();

        // Same useLocalE2EAuth gate AuthSetup.ConfigureSupabaseAuthentication
        // uses for ISupabaseAuthClient — ci.yml's e2e-tests job has no live
        // Supabase project to call, so an unconditional real
        // SupabaseAvatarStorage registration would throw at startup reading
        // Supabase:Url/ServiceRoleKey, neither of which that job configures
        // (see AuthSetup.IsLocalE2EAuth's own doc comment for the full
        // "never guarded only by config alone" reasoning this reuses
        // unchanged). Re-checked here rather than assumed, exactly like
        // that method's own callers do.
        if (AuthSetup.IsLocalE2EAuth(builder.Configuration, builder.Environment))
        {
            builder.Services.AddSingleton<IAvatarStorage, LocalE2EAvatarStorage>();
            return;
        }

        var supabaseUrl = builder.Configuration["Supabase:Url"]
            ?? throw new InvalidOperationException("Supabase:Url is not configured.");
        // A separate, more-privileged key — same reasoning as
        // SupabaseAuthClient.DeleteUserAsync (REQ-710/ADR-0026): avatar
        // uploads/deletes are always backend-initiated writes to a bucket
        // with no public write policy, never the anon key.
        var supabaseServiceRoleKey = builder.Configuration["Supabase:ServiceRoleKey"]
            ?? throw new InvalidOperationException("Supabase:ServiceRoleKey is not configured.");
        // Non-secret, defaults to "avatars" — REQ-722 doesn't mandate a
        // specific bucket name, so this is configurable the same way
        // AddIncidentReportingServices' GitHub:IncidentReportOwner/Repo/Label
        // below default sensibly rather than requiring every environment to
        // set it explicitly.
        var avatarBucketName = builder.Configuration["Supabase:AvatarBucketName"] ?? "avatars";
        builder.Services.AddSingleton(new SupabaseAvatarBucketOptions(avatarBucketName));

        builder.Services.AddHttpClient<IAvatarStorage, SupabaseAvatarStorage>(client =>
        {
            client.BaseAddress = new Uri(supabaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Add("apikey", supabaseServiceRoleKey);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {supabaseServiceRoleKey}");
        });
    }

    private static void AddIncidentReportingServices(this WebApplicationBuilder builder)
    {
        // REQ-903/ADR-0064/COMP-12: the fine-grained GitHub PAT (Issues:write on
        // this one repo only), read once here — deliberately NOT `?? throw`, unlike
        // Supabase's secrets above: this is a Tier 1 pull-forward with no manual
        // secret guaranteed to be provisioned in every environment yet (see
        // CLAUDE.md's setup-info handoff for this story). An unset token means
        // POST /incidents fails closed per-request (GitHubIssueClient
        // .CreateIssueAsync's own check), not that the whole app refuses to start.
        builder.Services.AddSingleton(new GitHubIncidentReportToken(builder.Configuration["GitHub:IncidentReportToken"]));
        // ADR-0064: fixed server-side, never accepted from the client — resolved
        // once here (appsettings.json carries the real, non-secret defaults for
        // this repo) and passed into GitHubIssueClient as plain values, rather than
        // XGArcade.Core taking a direct dependency on IConfiguration itself (that
        // project has no existing reason to reference
        // Microsoft.Extensions.Configuration).
        builder.Services.AddSingleton(new GitHubIncidentReportOptions(
            builder.Configuration["GitHub:IncidentReportOwner"] ?? "johanpearson",
            builder.Configuration["GitHub:IncidentReportRepo"] ?? "xg-arcade",
            builder.Configuration["GitHub:IncidentReportLabel"] ?? "user-reported"));
        builder.Services.AddHttpClient<IGitHubIssueClient, GitHubIssueClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            // GitHub's REST API rejects requests with no User-Agent; Accept/
            // X-GitHub-Api-Version pin the response shape/version this class's
            // GitHubIssueResponse parsing assumes (GitHub's documented current
            // convention, not this project's own choice).
            client.DefaultRequestHeaders.Add("User-Agent", "xg-arcade-backend");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        });
        builder.Services.AddScoped<IIncidentReportService, IncidentReportService>();

        // REQ-904/ADR-0066: server-side cached poll of GitHub's open,
        // user-reported-labeled issues for the admin "Incident reports" entry
        // point. AddMemoryCache registers the in-process IMemoryCache singleton
        // this repo has had no prior reason to use — no distributed cache exists
        // (ADR-0066's own "premature, revisit if the backend ever runs more than
        // one instance" alternative). CachedIncidentIssueSummaryProvider is
        // registered as a singleton (not scoped/transient) so its single shared
        // cache entry and "last successful poll" state are genuinely shared across
        // every admin request, per ADR-0066's "one shared cache entry, not
        // per-admin/per-request" decision — a scoped/transient registration would
        // silently defeat the whole point of this cache.
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton(new IncidentReportCacheTtl(
            TimeSpan.FromSeconds(builder.Configuration.GetValue<double?>("GitHub:IncidentReportCacheTtlSeconds")
                ?? IncidentReportCacheTtl.DefaultValue.TotalSeconds)));
        builder.Services.AddSingleton<ICachedIncidentIssueSummaryProvider, CachedIncidentIssueSummaryProvider>();

        // REQ-903: per-user rate limit for POST /incidents — see
        // IncidentEndpoints.MapIncidentEndpoints's own comment for why this is a
        // plain PartitionedRateLimiter<Guid> (keyed on the resolved caller's
        // User.Id) checked directly in the endpoint, rather than a global named
        // RateLimiter policy like auth-signup/auth-login/auth-guest (AuthSetup.cs)
        // (those are IP-partitioned and evaluated before authentication runs — a
        // shape that doesn't fit a per-user key here). Same FixedWindowRateLimiter
        // shape as those three otherwise: fixed window, no queueing. Configurable
        // via the same RateLimiting:* override convention as the auth-* policies,
        // default left deliberately tight (ADR-0064: "a small number... exact
        // numbers left to implementation") since a valid submission always creates
        // a real, visible GitHub issue with no review queue in front of it.
        var incidentReportPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:IncidentReportPermitLimit") ?? 3;
        var incidentReportWindowMinutes = builder.Configuration.GetValue<double?>("RateLimiting:IncidentReportWindowMinutes") ?? 10;
        builder.Services.AddSingleton(PartitionedRateLimiter.Create<Guid, Guid>(userId =>
            RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = incidentReportPermitLimit,
                Window = TimeSpan.FromMinutes(incidentReportWindowMinutes),
                QueueLimit = 0,
            })));
    }

    // ADR-0094/COMP-15 (Games.XGPredict): the API-Football fixtures/results
    // REST client — REQ-1301's round generation (XGPredictGameModule.
    // GenerateInstanceAsync, registered above) is this client's first real
    // caller; REQ-1305's grading pass is a separate, later story. Same "one
    // focused helper per component" shape as AddIncidentReportingServices/
    // AddAvatarStorageServices above.
    private static void AddApiFootballServices(this WebApplicationBuilder builder)
    {
        // ADR-0094 item 3: the API-Football account/key precondition is
        // additive to MVP-SCOPE.md and specific to xG Predict — not
        // guaranteed provisioned in every environment yet, so this is
        // deliberately NOT `?? throw` (same reasoning as
        // GitHubIncidentReportToken above). An unset key means every
        // ApiFootballClient call fails closed per-call
        // (ApiFootballClientException), never a startup crash. Never log
        // this value anywhere.
        builder.Services.AddSingleton(new ApiFootballApiKey(builder.Configuration["ApiFootball:ApiKey"]));

        // Premier League's real API-Football league ID (39) — unverified
        // against a live fetch from this sandbox, same posture ADR-0094's
        // own Context section already took for egress to api-football.com;
        // flag for manual human verification.
        var leagueId = builder.Configuration.GetValue<int?>("ApiFootball:LeagueId") ?? 39;
        // Premier League's season is named by the year it starts (typically
        // August) — e.g. the 2026-27 season is "2026." This default needs a
        // human to sanity-check/override via ApiFootball:Season each
        // pre-season (same "small, deliberately manual, revisit later"
        // spirit as this repo's existing manual-QID-lookup precedent) — it
        // rolls over to the new season year every July 1st regardless of
        // whether API-Football has actually published that season's
        // fixtures yet.
        var defaultSeason = DateTime.UtcNow.Month >= 7 ? DateTime.UtcNow.Year : DateTime.UtcNow.Year - 1;
        var season = builder.Configuration.GetValue<int?>("ApiFootball:Season") ?? defaultSeason;
        builder.Services.AddSingleton(new ApiFootballOptions(leagueId, season));

        // BaseAddress only, no auth header at registration time — the real
        // x-apisports-key header is set per-request in ApiFootballClient
        // itself, the same "credential set per-request, never on
        // httpClient's own DefaultRequestHeaders" discipline
        // GitHubIssueClient's own registration above already follows.
        builder.Services.AddHttpClient<IApiFootballClient, ApiFootballClient>(client =>
        {
            client.BaseAddress = new Uri("https://v3.football.api-sports.io/");
        });
    }
}

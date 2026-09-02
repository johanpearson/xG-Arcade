using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using XGArcade.Api.Auth;
using XGArcade.Api.Avatars;
using XGArcade.Api.Social;
using XGArcade.Core.Auth;
using XGArcade.Core.Games;
using XGArcade.Core.IncidentReporting;
using XGArcade.Core.Leagues;
using XGArcade.Core.Rounds;
using XGArcade.Core.Scoring;
using XGArcade.Core.Social;
using XGArcade.Core.Storage;
using XGArcade.Data;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.FootballData;
using XGArcade.DataSync.Wikidata;
using XGArcade.Games.XGConnect;
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
        // S-201 (quality-gate fix): depends on IEnumerable<IGameModule> instead
        // of any one game's repository directly — every IGameModule registered
        // below (xG Grid/xG Path/xG Predict) is given a chance to purge its own
        // per-user data (IGameModule.PurgeUserDataAsync) rather than this
        // service reaching into a game-specific repository itself, which would
        // violate ADR-0003 (see AccountDeletionService's own doc comment). DI
        // registration order doesn't matter here — the container resolves
        // IEnumerable<IGameModule> from every AddScoped<IGameModule, ...> call
        // below lazily, not in registration order.
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
        // RoundSchedulingOptions for "xg-predict" is now registered too (this
        // story, see below, alongside xG Grid/xG Path's own) —
        // InternalRoundEndpoints'/LeaderboardEndpoints' own gameKey
        // allow-lists now include "xg-predict" as well, closing the gap this
        // comment used to describe as deferred (ADR-0051's 2026-08-30
        // amendment re-derived that the existing per-GameKey pattern still
        // applies unchanged for this third GameKey; no structural deviation).
        builder.Services.AddScoped<IPredictInstanceRepository, PredictInstanceRepository>();
        // ADR-0102: XGPredictGameModule's constructor now also takes
        // PredictGradingOptions (registered as a singleton immediately
        // below) — resolved automatically by the container regardless of
        // registration order (registration just populates the
        // IServiceCollection; the container itself is built once after
        // every Add* call here has run).
        builder.Services.AddScoped<IGameModule, XGPredictGameModule>();
        // REQ-1305/ADR-0097: PredictGradingOptions is a plain, non-
        // appsettings-bound constant (see that class's own doc comment,
        // mirrors GridGenerationOptions' registration above) — a singleton
        // is fine since it's immutable configuration, not per-request state.
        // ADR-0102: also now consumed by XGPredictGameModule itself, to
        // anchor GenerateInstanceAsync's SuggestedEndTime to the last
        // selected match's kickoff plus TypicalMatchDuration, reusing this
        // existing constant rather than inventing a new one.
        builder.Services.AddSingleton(new PredictGradingOptions());
        // ADR-0097 Decision §2: PredictGradingService takes the CONCRETE
        // XGPredictScoringStrategy type directly, registered as itself
        // alongside its existing IScoringStrategy registration further
        // below — a deliberate, ADR-approved choice (not an
        // interface-widening workaround; see that ADR's Alternatives
        // table for why IScoringStrategy itself is not widened for this
        // one caller).
        builder.Services.AddScoped(_ => new XGPredictScoringStrategy
        {
            GameKey = XGPredictGameModule.XGPredictGameKey,
        });
        builder.Services.AddScoped<IPredictGradingService, PredictGradingService>();
        // S-084/REQ-1202: PathTemplateResolver's puzzle-count source — mirrors
        // GridGenerationOptions' role/precedent above for xG Path's own generation
        // config (deliberately not a field on RoundSchedulingOptions; see that
        // type's own doc comment).
        builder.Services.AddSingleton(new PathGenerationOptions());
        // This story: PredictTemplateResolver's match-count source — mirrors
        // GridGenerationOptions'/PathGenerationOptions' role/precedent above
        // for xG Predict's own generation config (deliberately not a field on
        // RoundSchedulingOptions; see that type's own doc comment). Default
        // MatchCount (5, REQ-1301) is fine as-is, no override needed here.
        builder.Services.AddSingleton(new PredictGenerationOptions());
        // Core.Social (COMP-16)/ADR-0103, S-208: REQ-1401-1403's friends/
        // challenge/matchmaking persistence. Arcade-level, registered
        // alongside Core.Users/Core.Leagues repositories, not behind
        // IGameModule — see ADR-0103 for the component-split reasoning.
        // No IGameModule registration for xG Connect yet (this story is
        // schema + CRUD only, no game logic).
        builder.Services.AddScoped<IFriendRepository, FriendRepository>();
        // REQ-1401/S-209: send/accept/decline business logic layered on top
        // of IFriendRepository above — see FriendService's own doc comment.
        builder.Services.AddScoped<IFriendService, FriendService>();
        builder.Services.AddScoped<IChallengeRepository, ChallengeRepository>();
        // REQ-1402/S-210: send/accept/decline business logic layered on top
        // of IChallengeRepository above — see ChallengeService's own doc
        // comment. Note this does NOT create the resulting ConnectMatch row
        // (ADR-0103) — that's XGArcade.Api.Social.ChallengeEndpoints' own
        // accept-handler orchestration, registered separately below.
        builder.Services.AddScoped<IChallengeService, ChallengeService>();
        builder.Services.AddScoped<IMatchmakingOptInRepository, MatchmakingOptInRepository>();
        // REQ-1403/S-210: the opt-in half only, layered on top of
        // IMatchmakingOptInRepository above — see MatchmakingService's own
        // doc comment for why the pairing sweep is a separate type below,
        // not part of this interface.
        builder.Services.AddScoped<IMatchmakingService, MatchmakingService>();
        // Games.XGConnect (COMP-17)/ADR-0103, S-208: REQ-1404-1407/1410's
        // match/target-pick/chain-step/chat persistence.
        builder.Services.AddScoped<IConnectMatchRepository, ConnectMatchRepository>();
        builder.Services.AddScoped<IConnectChatMessageRepository, ConnectChatMessageRepository>();
        // S-211 scaffold: IGameModuleResolver.Resolve("xg-connect") now
        // returns a real module — GenerateInstanceAsync/ScoreSubmissionAsync/
        // GetCellIdsAsync/GetMaxAttemptsForCellAsync/GetCellCategoryTypesAsync
        // all throw NotSupportedException (ADR-0103: these are permanently
        // inapplicable to xG Connect's non-Round shape, not TODOs), only
        // PurgeUserDataAsync is a real implementation. Deliberately NOT
        // added to RoundSchedulingOptions/IScoringStrategy/
        // GuessSubmissionAllowedGameKeys registrations above/below — xG
        // Connect never uses Core.Rounds/Core.Scoring's Guess-based
        // submission path (see XGConnectGameModule's own doc comment).
        builder.Services.AddScoped<IGameModule, XGConnectGameModule>();
        // REQ-1404/S-211: the shared career-overlap check
        // (IPlayerCareerOverlapService) is registered independently of
        // IConnectTargetPickService below — no facade, same "independently
        // registered" convention IPlayerCareerStintRefreshService/
        // IPathEligibilityService already establish for Games.XGPath's own
        // services. S-213's chain-step validation will inject this same
        // registration, not a second copy.
        builder.Services.AddScoped<IPlayerCareerOverlapService, PlayerCareerOverlapService>();
        // REQ-1404/S-211: target-pick selection business logic, layered on
        // top of IConnectMatchRepository above and
        // IPlayerCareerOverlapService immediately above — see
        // ConnectTargetPickService's own doc comment.
        builder.Services.AddScoped<IConnectTargetPickService, ConnectTargetPickService>();
        // REQ-1403/ADR-0103, S-210: orchestrates IMatchmakingOptInRepository
        // (Core.Social) together with IConnectMatchRepository above
        // (Games.XGConnect) for the periodic pairing sweep — lives in
        // XGArcade.Api.Social, not Core.Social, for the same ADR-0103
        // reason ChallengeEndpoints' own accept handler does its
        // ConnectMatch write here rather than in ChallengeService.
        builder.Services.AddScoped<MatchmakingSweepService>();

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
        // file. generate-grid-round.yml's/generate-path-round.yml's/
        // generate-predict-round.yml's cron (split from a single
        // generate-round.yml, S-136/ADR-0072; extended to a third file for
        // "xg-predict" per that ADR's 2026-08-30 amendment) is daily
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
        // xG Predict's own RoundSchedulingOptions instance, resolved
        // independently of xG Grid's/xG Path's via
        // IRoundSchedulingOptionsResolver (registered below) — a distinct
        // config key (RoundScheduling:XGPredict:RoundDurationHours), same
        // "independent config key per GameKey" reasoning as xG Path's own
        // registration immediately above.
        //
        // ADR-0102 (S-204): this RoundDuration value is now a DEAD FALLBACK
        // for "xg-predict" specifically — XGPredictGameModule always
        // supplies GameInstance.SuggestedStartTime/SuggestedEndTime once it
        // returns a non-null instance, and RoundGenerationService prefers
        // those over chain-math (startTime + RoundDuration) unconditionally.
        // It is NOT read for xg-predict's actual round timing anymore. Kept
        // registered anyway — RoundSchedulingOptionsResolver.Resolve is
        // called unconditionally for every GameKey RoundGenerationService
        // handles, regardless of whether that GameKey's chain-math EndTime
        // ever actually gets used; removing this registration would throw
        // InvalidOperationException the next time xg-predict's round is
        // generated. Default (48h) is otherwise inert for this GameKey —
        // only relevant again if XGPredictGameModule is ever changed to
        // return a null SuggestedEndTime (it never does today).
        var xgPredictRoundDurationHours = builder.Configuration.GetValue<double?>("RoundScheduling:XGPredict:RoundDurationHours") ?? 48;
        builder.Services.AddSingleton(new RoundSchedulingOptions
        {
            GameKey = XGPredictGameModule.XGPredictGameKey,
            RoundDuration = TimeSpan.FromHours(xgPredictRoundDurationHours),
        });
        builder.Services.AddScoped<IRoundSchedulingOptionsResolver, RoundSchedulingOptionsResolver>();
        builder.Services.AddScoped<IRoundRepository, RoundRepository>();
        builder.Services.AddScoped<IRoundGenerationService, RoundGenerationService>();
        builder.Services.AddScoped<IRoundCloseService, RoundCloseService>();

        // COMP-04 (Core.Scoring) — S-009's guess submission (REQ-201/202/203/208/210)
        // and S-011's score locking (REQ-205, IScoreLockingService — Core.Rounds'
        // RoundCloseService calls this rather than computing scores itself).
        builder.Services.AddScoped<IGuessRepository, GuessRepository>();
        // S-200/ADR-0098 Consequences: the explicit allow-list of GameKeys
        // this Guess-based submission path serves, supplied here (never
        // hardcoded inside Core.Scoring, per ADR-0003) — "xg-predict" is
        // deliberately absent, closing the risk ADR-0098's Consequences
        // section flagged (REQ-1306's confirm-lock, enforced only in
        // PredictEndpoints, must never become reachable through this path).
        builder.Services.AddSingleton(new GuessSubmissionAllowedGameKeys
        {
            GameKeys = [GridGameModule.XGGridGameKey, XGPathGameModule.XGPathGameKey],
        });
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
        // ADR-0100: LeaderboardService now sources every scope's round
        // totals through a per-GameKey IRoundScoreSource instead of calling
        // IGuessRepository/ILiveRoundContributionService directly.
        // IRoundScoreSource carries no GameKey property of its own (unlike
        // IScoringStrategy), so — unlike ScoringStrategyResolver's own
        // FirstOrDefault(s => s.GameKey == ...) lookup over a plain
        // IEnumerable<IScoringStrategy> DI registration — this resolver is
        // built directly from an explicit GameKey -> IRoundScoreSource
        // dictionary here at the composition root, rather than via a second
        // multi-registration of IRoundScoreSource itself. GuessRoundScoreSource
        // is constructed twice (once per GameKey it serves, mirroring
        // UniquenessScoringStrategy/ClueEfficiencyScoringStrategy's own two
        // registrations above); PredictRoundScoreSource once, wrapping only
        // IPredictInstanceRepository (registered above, COMP-15) — never
        // IRoundRepository/IUserRepository (ADR-0100's "For AI agents" rule).
        builder.Services.AddScoped<IRoundScoreSourceResolver>(sp =>
        {
            var guessRoundScoreSource = new GuessRoundScoreSource(
                sp.GetRequiredService<IGuessRepository>(), sp.GetRequiredService<ILiveRoundContributionService>())
            {
                GameKey = GridGameModule.XGGridGameKey,
            };
            var xgPathGuessRoundScoreSource = new GuessRoundScoreSource(
                sp.GetRequiredService<IGuessRepository>(), sp.GetRequiredService<ILiveRoundContributionService>())
            {
                GameKey = XGPathGameModule.XGPathGameKey,
            };
            var predictRoundScoreSource = new PredictRoundScoreSource(sp.GetRequiredService<IPredictInstanceRepository>());

            return new RoundScoreSourceResolver(new Dictionary<string, IRoundScoreSource>
            {
                [GridGameModule.XGGridGameKey] = guessRoundScoreSource,
                [XGPathGameModule.XGPathGameKey] = xgPathGuessRoundScoreSource,
                [XGPredictGameModule.XGPredictGameKey] = predictRoundScoreSource,
            });
        });

        builder.AddIncidentReportingServices();
        builder.AddAvatarStorageServices();
        builder.AddFootballDataServices();
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

    // ADR-0099/COMP-15 (Games.XGPredict): the football-data.org fixtures/
    // results REST client — REQ-1301's round generation (XGPredictGameModule.
    // GenerateInstanceAsync, registered above) is this client's first real
    // caller; REQ-1305's grading pass is a separate, later story. Same "one
    // focused helper per component" shape as AddIncidentReportingServices/
    // AddAvatarStorageServices above. Replaces the former
    // AddApiFootballServices (ADR-0094) — see ADR-0099 for why: API-Football's
    // free plan turned out to restrict season access to a rolling
    // historical window that excludes the current season entirely, making
    // it structurally unusable for this game on the free tier.
    private static void AddFootballDataServices(this WebApplicationBuilder builder)
    {
        // ADR-0099 (carrying forward ADR-0094 item 3's reasoning): the
        // football-data.org account/token precondition is additive to
        // MVP-SCOPE.md and specific to xG Predict — not guaranteed
        // provisioned in every environment yet, so this is deliberately NOT
        // `?? throw` (same reasoning as GitHubIncidentReportToken above).
        // An unset key means every FootballDataClient call fails closed
        // per-call (FootballDataClientException), never a startup crash.
        // Never log this value anywhere.
        builder.Services.AddSingleton(new FootballDataApiKey(builder.Configuration["FootballData:ApiKey"]));

        // football-data.org's Premier League competition code — unverified
        // against a live fetch from this sandbox, same posture ADR-0094's
        // own Context section already took for egress to api-football.com
        // (football-data.org is blocked here too); flag for manual human
        // verification. Unlike ADR-0094's ApiFootballOptions, there is no
        // separate season config here — football-data.org's competition
        // endpoint exposes the current season's current matchday directly,
        // so this client never has to compute or configure a season year.
        var competitionCode = builder.Configuration["FootballData:CompetitionCode"] ?? "PL";
        builder.Services.AddSingleton(new FootballDataOptions(competitionCode));

        // BaseAddress only, no auth header at registration time — the real
        // X-Auth-Token header is set per-request in FootballDataClient
        // itself, the same "credential set per-request, never on
        // httpClient's own DefaultRequestHeaders" discipline
        // GitHubIssueClient's own registration above already follows.
        builder.Services.AddHttpClient<IFootballDataClient, FootballDataClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.football-data.org/v4/");
        });
    }
}

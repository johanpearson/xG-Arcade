using XGArcade.Api.Grid;
using XGArcade.Api.Internal;
using XGArcade.Api.Path;
using XGArcade.Api.Predict;
using XGArcade.Core.Games;
using XGArcade.Core.Rounds;
using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGGrid;
using XGArcade.Games.XGPath;
using XGArcade.Games.XGPredict;

namespace XGArcade.Api.Rounds;

public static class InternalRoundEndpoints
{
    public static void MapInternalRoundEndpoints(this WebApplication app)
    {
        // REQ-301: a legitimate scheduled-job endpoint (CONT-05, Round
        // Scheduler Job) — unlike the test-data-flavored endpoint below,
        // this is meant to run in every environment (including a future real
        // Production), so it's protected by the shared INTERNAL_JOB_TOKEN
        // bearer token instead of an environment gate. See
        // docs/review-2026-07-07-design.md's judged "fine at this scale"
        // note on this auth approach.
        app.MapPost("/internal/generate-round", async (
            HttpContext httpContext,
            IConfiguration configuration,
            IGridInstanceRepository gridInstanceRepository,
            IPathInstanceRepository pathInstanceRepository,
            IPredictInstanceRepository predictInstanceRepository,
            IRoundGenerationService roundGenerationService,
            GridGenerationOptions gridGenerationOptions,
            PathGenerationOptions pathGenerationOptions,
            PredictGenerationOptions predictGenerationOptions,
            ILogger<RoundGenerationLogCategory> logger,
            double? roundDurationHours,
            // S-084/REQ-1202: defaults to xG Grid's GameKey when omitted so
            // any existing caller that doesn't pass it keeps today's
            // behavior unchanged — generate-grid-round.yml/
            // generate-path-round.yml/generate-predict-round.yml (split
            // from a single generate-round.yml, S-136/ADR-0072; extended to
            // a third file for "xg-predict" per that ADR's 2026-08-30
            // amendment) each always pass it explicitly for their own
            // GameKey, but a stray/older manual call (e.g. a bookmarked
            // workflow_dispatch run) must not silently start generating an
            // unexpected game's rounds.
            string gameKey = GridGameModule.XGGridGameKey,
            CancellationToken cancellationToken = default) =>
        {
            if (!InternalJobAuthorization.IsAuthorized(httpContext.Request, configuration))
                return Results.Unauthorized();

            // Optional per-call override (e.g. generate-grid-round.yml's,
            // generate-path-round.yml's, or generate-predict-round.yml's own
            // workflow_dispatch input, each scoped to its own GameKey as of
            // S-136/ADR-0072) — takes precedence over
            // RoundSchedulingOptions.RoundDuration for this one generation
            // call only, never mutating the shared singleton.
            // This is a system boundary (bearer-token-gated, but still an
            // external caller), so it's validated here rather than trusted.
            //
            // Floor is 24, not 0: ADR-0027's safety invariant is
            // `RoundDuration >= that GameKey's own round-generation cron`'s
            // max gap between firings, which is a constant 24h now that each
            // cron is daily. A shorter override would let a round close
            // before the next scheduled run generates its successor —
            // REQ-301's "dead app" failure mode, reproduced via this override
            // instead of the cron/duration coupling ADR-0027 fixed. If any of
            // the three workflows' cron cadence ever changes, this floor must
            // be re-derived by hand the same way (see ADR-0027's "For AI
            // agents" section, ADR-0072 and its 2026-08-30 amendment, and
            // NOTES.md's 2026-07-10 entry) — don't just bump the number.
            if (roundDurationHours is < 24)
            {
                return Results.Problem(
                    title: "Invalid roundDurationHours",
                    detail: "roundDurationHours must be at least 24 (the daily cron's maximum gap — see ADR-0027) to avoid a round closing before the next scheduled run can generate its successor.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // S-084/REQ-1202 (quality-gate follow-up): an unrecognized
            // gameKey is malformed caller input (a bad query-string value),
            // not a round-generation failure — validated up front via
            // Results.Problem at 400, the same discipline the
            // roundDurationHours check above already uses, rather than
            // relying on the switch below's defensive throw to fall through
            // into the generic 500 catch-all.
            if (gameKey is not (GridGameModule.XGGridGameKey or XGPathGameModule.XGPathGameKey or XGPredictGameModule.XGPredictGameKey))
            {
                return Results.Problem(
                    title: "Invalid gameKey",
                    detail: $"Unknown gameKey '{gameKey}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var roundDurationOverride = roundDurationHours is { } hours ? TimeSpan.FromHours(hours) : (TimeSpan?)null;

            try
            {
                // Tier 0 has no admin-driven template management yet for any
                // of the three games — same find-or-create-by-config-value
                // pattern /internal/grid/generate uses. Moved inside the try
                // (previously ran unguarded before it) so a DB failure here
                // gets the same problem-details treatment as everything
                // below instead of an opaque, empty 500.
                //
                // S-084/REQ-1202 (extended to a third arm for "xg-predict" by
                // this story; see ADR-0051's 2026-08-30 amendment for the
                // re-derivation confirming this switch is still preferable to
                // a fully-generic IGameModule alternative): this switch is
                // the ONLY place that branches on gameKey in this handler —
                // its sole job is producing the opaque TemplateId RoundConfig
                // carries; everything else below (auth, duration validation,
                // calling roundGenerationService, exception handling,
                // response shape) is unchanged and generic across every
                // GameKey. The guard above already rules out any
                // unrecognized gameKey reaching here, so this default arm is
                // defensive only ("should never happen"), not a real
                // "unrecognized value" path.
                var templateId = gameKey switch
                {
                    GridGameModule.XGGridGameKey => (await GridTemplateResolver.GetOrCreateBySizeAsync(
                        gridInstanceRepository, gridGenerationOptions.GridSize, cancellationToken)).Id,
                    XGPathGameModule.XGPathGameKey => (await PathTemplateResolver.GetOrCreateByPuzzleCountAsync(
                        pathInstanceRepository, pathGenerationOptions.PuzzleCount, cancellationToken)).Id,
                    XGPredictGameModule.XGPredictGameKey => (await PredictTemplateResolver.GetOrCreateByMatchCountAsync(
                        predictInstanceRepository, predictGenerationOptions.MatchCount, cancellationToken)).Id,
                    _ => throw new ArgumentException($"Unknown gameKey '{gameKey}'."),
                };

                var round = await roundGenerationService.GenerateNextRoundIfNeededAsync(
                    gameKey, new RoundConfig { TemplateId = templateId }, roundDurationOverride, cancellationToken);

                return Results.Ok(new GenerateRoundResponse(round.Id, round.SequenceNumber, round.GameKey, round.StartTime, round.EndTime));
            }
            catch (Exception ex) when (ex is GridGenerationException or PathGenerationException or PredictGenerationException)
            {
                // REQ-101/REQ-1202/REQ-1301's abort paths, surfacing through
                // round generation — GridGenerationException (xg-grid),
                // PathGenerationException (xg-path), and
                // PredictGenerationException (xg-predict, thrown by
                // XGPredictGameModule.GenerateInstanceAsync's "PredictTemplate
                // not found"/"not enough upcoming fixtures" abort paths)
                // don't share a base type (unlike GameEntityNotFoundException,
                // which only covers the scoring-side "id doesn't resolve"
                // failure mode), so all three are caught here via a
                // type-pattern filter rather than three near-identical catch
                // blocks — same precedent as
                // XGArcade.DataSync.Wikidata.WikidataClient's
                // `catch (Exception ex) when (ex is HttpRequestException or
                // JsonException)`.
                logger.LogError(ex, "Round generation aborted.");

                return Results.Problem(
                    title: "Round generation failed",
                    // Non-Production-only convention doesn't apply here (this
                    // endpoint runs in every environment) — detail is still
                    // the exception's own hand-authored message, never a raw
                    // stack trace, matching docs/coding-guidelines.md.
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
            catch (Exception ex)
            {
                // Anything else (a DB blip, a Supabase/Wikidata/API-Football
                // client failure that wasn't itself swallowed, ...)
                // previously fell through as an opaque, empty 500 —
                // indistinguishable in that GameKey's round-generation
                // workflow's log (generate-grid-round.yml/
                // generate-path-round.yml/generate-predict-round.yml as of
                // S-136/ADR-0072, extended to a third file per that ADR's
                // 2026-08-30 amendment) from every other failure mode.
                // REQ-902's failure alerting is Tier 1 (not built yet), so
                // REQ-301 already leans on someone noticing and checking a
                // failed run manually (see REQ-301's own acceptance
                // criteria) — this is what makes that check possible from
                // the workflow's own log, not just Container App logs.
                // ex.Message in `detail` is the documented narrow exception
                // in docs/coding-guidelines.md's error-handling rule (added
                // alongside this fix) for a bearer-token-gated /internal/*
                // endpoint whose only caller is a scheduled job, not a
                // player-facing surface — the default "no raw exception text
                // to the client" rule still applies everywhere else.
                logger.LogError(ex, "Round generation failed unexpectedly.");

                return Results.Problem(
                    title: "Round generation failed unexpectedly",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // REQ-806: absent entirely when ASPNETCORE_ENVIRONMENT == Production
        // (returns 404, not "access denied") — checked here before the route
        // is even registered, same discipline ADR-0006 requires for COMP-09,
        // never guarded only by an attribute.
        if (app.Environment.IsProduction())
            return;

        app.MapPost("/internal/test-data/force-close-round/{roundId:guid}", async (
            Guid roundId,
            IRoundCloseService roundCloseService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var round = await roundCloseService.CloseRoundAsync(roundId, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);

            return round is null
                ? Results.NotFound()
                : Results.Ok(new ForceCloseRoundResponse(round.Id, round.EndTime));
        });

        // REQ-807: unlike guesses/users (created via the real signup/guess
        // endpoints — REQ-806's own convention), a playable round's grid
        // content can't be created deterministically without either a live,
        // timing-variable Wikidata call (ADR-0011's addendum) or direct
        // database access — and Playwright, running against a separately-
        // started API process, has neither. Every write below goes through
        // the same repository each owning component normally uses (ADR-0006
        // boundary rule 4), never a raw table write.
        app.MapPost("/internal/test-data/seed-guessable-round", async (
            IGridInstanceRepository gridInstanceRepository,
            // S-106 (pure refactor): every call this endpoint makes
            // (AddPlayerAsync via CreateUniqueTestPlayerAsync,
            // AddPlayerAttributeAsync) moved out of IPlayerStoreRepository —
            // no remaining reason to inject that interface here.
            IPlayerRepository playerRepository,
            IPlayerAttributeRepository playerAttributeRepository,
            IRoundRepository roundRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;

            var instanceId = Guid.NewGuid();
            var cellId = Guid.NewGuid();
            var instance = await gridInstanceRepository.AddInstanceAsync(new GridInstance
            {
                Id = instanceId,
                TemplateId = Guid.NewGuid(),
                Cells =
                [
                    new GridCell
                    {
                        Id = cellId,
                        GridInstanceId = instanceId,
                        Row = 0,
                        Col = 0,
                        RowCategoryType = CategoryPairingRules.Country,
                        RowCategoryValue = "France",
                        ColCategoryType = CategoryPairingRules.Club,
                        ColCategoryValue = "Arsenal",
                    },
                ],
            }, cancellationToken);

            // REQ-209 fallout, found via a real CI failure: this endpoint
            // never reused/cleaned up prior calls' rows, so repeated or
            // concurrent test runs against the same CI Postgres instance
            // accumulated multiple France/Arsenal "Thierry Henry" players
            // over time. FindMatchAsync's category-fit check isn't scoped to
            // one grid instance (Player/PlayerAttribute are global), so
            // every one of those rows matched — REQ-209's now-correct
            // disambiguation prompt surfaced this latent collision instead
            // of the old auto-accept-lowest-id behavior silently masking it.
            // A short unique tag per call (same purpose as the WikidataQid
            // suffix below, which was already unique) keeps each call's
            // players hermetic; every caller reads the actual generated name
            // back from this response rather than assuming a literal, so no
            // test file needed to change.
            var player = await CreateUniqueTestPlayerAsync(playerRepository, "Thierry Henry", cancellationToken);
            var correctPlayerName = player.FullName;
            await playerAttributeRepository.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" },
                cancellationToken);
            await playerAttributeRepository.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" },
                cancellationToken);

            // REQ-807 originally seeded only one valid player — enough for
            // REQ-201/203/210/303, but REQ-204's live uniqueness needs at
            // least two distinct correct answers to demonstrate anything
            // other than "0% unique" (every correct guesser necessarily
            // sharing the one and only valid answer). A second, equally
            // real Arsenal/France player added here so S-011's E2E suite can
            // have two players each pick a different correct answer.
            var alternatePlayer = await CreateUniqueTestPlayerAsync(playerRepository, "Robert Pires", cancellationToken);
            var alternateCorrectPlayerName = alternatePlayer.FullName;
            await playerAttributeRepository.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = alternatePlayer.Id, AttributeType = "nationality", AttributeValue = "France" },
                cancellationToken);
            await playerAttributeRepository.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = alternatePlayer.Id, AttributeType = "club", AttributeValue = "Arsenal" },
                cancellationToken);

            // REQ-304: this test-data endpoint bypasses RoundGenerationService
            // entirely, so it computes the same MAX(SequenceNumber)+1 scoped
            // to this GameKey itself — the (GameKey, SequenceNumber) unique
            // index still applies to every Round row regardless of which
            // path created it.
            var sequenceNumber = (await roundRepository.GetMaxSequenceNumberByGameKeyAsync(GridGameModule.XGGridGameKey, cancellationToken) ?? 0) + 1;
            var round = await roundRepository.AddAsync(new Round
            {
                Id = Guid.NewGuid(),
                GameKey = GridGameModule.XGGridGameKey,
                GameInstanceId = instance.Id,
                SequenceNumber = sequenceNumber,
                StartTime = now.AddMinutes(-1),
                EndTime = now.AddHours(1),
                AllowGuessChange = true,
            }, cancellationToken);

            return Results.Ok(new SeedGuessableRoundResponse(round.Id, cellId, correctPlayerName, alternateCorrectPlayerName));
        });

        // S-088/REQ-807 extension: the xg-path counterpart to
        // seed-guessable-round above — REQ-807's own doc text ("only
        // grid/round content is seeded this way") was true only because no
        // second game existed yet to need it. S-088's E2E coverage for xG
        // Path's full loop (generation -> clue reveal -> guess -> round
        // close -> leaderboard) needs a deterministic, guessable xg-path
        // round the same way S-011's grid E2E suite needed
        // seed-guessable-round — same reasoning, same repository-only write
        // discipline (ADR-0006 boundary rule 4), just against
        // IPathInstanceRepository instead of IGridInstanceRepository.
        //
        // This bypasses XGPathGameModule.GenerateInstanceAsync entirely
        // (writes PathInstance/PathPuzzle directly), so REQ-1201's
        // seeded-club/appearance-count eligibility rules never apply here —
        // same "bypass the module's own generation-time eligibility logic"
        // reasoning the grid seed endpoint above already relies on for
        // GridGameModule.
        app.MapPost("/internal/test-data/seed-guessable-path-round", async (
            IPathInstanceRepository pathInstanceRepository,
            // S-106/S-107 (pure refactor): CreateUniqueTestPlayerAsync's own
            // AddPlayerAsync call moved out of the original, now-deleted
            // IPlayerStoreRepository — AddCareerStintsAsync now lives on
            // IPlayerCareerStintRepository (see ADR-0067).
            IPlayerCareerStintRepository playerCareerStintRepository,
            IPlayerRepository playerRepository,
            IRoundRepository roundRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;

            // Same unique-tag-per-call convention as seed-guessable-round
            // above (REQ-209 fallout) — keeps repeated/concurrent test runs
            // hermetic against a shared CI Postgres instance.
            var player = await CreateUniqueTestPlayerAsync(playerRepository, "Path Test Player", cancellationToken);
            var correctPlayerName = player.FullName;

            // At least 3 chronologically distinct, non-overlapping stints so
            // PathClueSequenceBuilder.BuildSequence (via GET /path/current)
            // has real content for all 3 club-reveal turns — SequenceOrder
            // here is illustrative only; AddCareerStintsAsync recomputes it
            // for the player's full stint set by (StartYear, EndYear), never
            // trusting the caller's own value.
            await playerCareerStintRepository.AddCareerStintsAsync(
                player.Id,
                [
                    new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Ajax", StartYear = 2010, EndYear = 2013, SequenceOrder = 0 },
                    new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Juventus", StartYear = 2013, EndYear = 2016, SequenceOrder = 1 },
                    new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Real Madrid", StartYear = 2016, EndYear = 2019, SequenceOrder = 2 },
                ],
                cancellationToken);

            var instanceId = Guid.NewGuid();
            var puzzleId = Guid.NewGuid();
            var instance = await pathInstanceRepository.AddInstanceAsync(new PathInstance
            {
                Id = instanceId,
                TemplateId = Guid.NewGuid(),
                Puzzles =
                [
                    new PathPuzzle
                    {
                        Id = puzzleId,
                        PathInstanceId = instanceId,
                        TargetPlayerId = player.Id,
                    },
                ],
            }, cancellationToken);

            // REQ-304: see seed-guessable-round's identical note above.
            var sequenceNumber = (await roundRepository.GetMaxSequenceNumberByGameKeyAsync(XGPathGameModule.XGPathGameKey, cancellationToken) ?? 0) + 1;
            var round = await roundRepository.AddAsync(new Round
            {
                Id = Guid.NewGuid(),
                GameKey = XGPathGameModule.XGPathGameKey,
                GameInstanceId = instance.Id,
                SequenceNumber = sequenceNumber,
                StartTime = now.AddMinutes(-1),
                EndTime = now.AddHours(1),
                AllowGuessChange = true,
            }, cancellationToken);

            return Results.Ok(new SeedGuessablePathRoundResponse(round.Id, puzzleId, correctPlayerName));
        });

        // S-203/REQ-1301: the xg-predict counterpart to seed-guessable-round/
        // seed-guessable-path-round above — same "bypass the module's own
        // generation-time logic, write instance content directly via the
        // owning repository" reasoning (this bypasses
        // XGPredictGameModule.GenerateInstanceAsync entirely, so REQ-1301's
        // real-fixture/tightest-kickoff-cluster selection never runs here).
        //
        // Unlike the two seed endpoints above, this one exposes a single
        // caller-controlled knob: firstKickoffMinutesFromNow. REQ-1303's
        // round-wide lock (PredictInstance.LockInstant = the earliest of the
        // 5 matches' own KickoffUtc) is exactly what an E2E spec needs to
        // control directly to exercise both sides of that lock — a positive
        // value (the default, 60 minutes) seeds a round that is still open
        // (for viewing the slate, submitting predictions, and REQ-1306's
        // confirm-and-lock flow); a zero-or-negative value seeds one that is
        // already locked (for REQ-1303's round-wide-lock notice and rejected
        // submissions).
        app.MapPost("/internal/test-data/seed-guessable-predict-round", async (
            IPredictInstanceRepository predictInstanceRepository,
            IRoundRepository roundRepository,
            TimeProvider timeProvider,
            double? firstKickoffMinutesFromNow,
            CancellationToken cancellationToken) =>
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var firstKickoff = now.AddMinutes(firstKickoffMinutesFromNow ?? 60);

            // Same unique-tag-per-call convention as the two seed endpoints
            // above (REQ-209 fallout) — keeps repeated/concurrent test runs
            // hermetic against a shared CI Postgres instance. ExternalFixtureId
            // has no DB uniqueness constraint (see this entity's own
            // migration), but a negative, tick-derived base keeps these test
            // fixtures obviously distinct from any real football-data.org id
            // (always positive) regardless.
            var tag = Guid.NewGuid().ToString("N")[..8];
            var externalFixtureIdBase = -(int)(DateTime.UtcNow.Ticks % 1_000_000);

            var instanceId = Guid.NewGuid();
            var matches = Enumerable.Range(0, 5)
                .Select(i => new PredictMatch
                {
                    Id = Guid.NewGuid(),
                    PredictInstanceId = instanceId,
                    ExternalFixtureId = externalFixtureIdBase - i,
                    HomeTeamName = $"Predict Test Home {tag} {i}",
                    AwayTeamName = $"Predict Test Away {tag} {i}",
                    // Only the first match's kickoff controls REQ-1303's lock
                    // instant (LockInstant = Min(KickoffUtc)) — the other 4
                    // are spaced a few minutes later; order is otherwise
                    // unused (the response below re-sorts by kickoff anyway).
                    KickoffUtc = firstKickoff.AddMinutes(i * 5),
                })
                .ToList();

            var instance = await predictInstanceRepository.AddInstanceAsync(new PredictInstance
            {
                Id = instanceId,
                TemplateId = Guid.NewGuid(),
                Matches = matches,
            }, cancellationToken);

            // REQ-304: see seed-guessable-round's identical note above.
            var sequenceNumber = (await roundRepository.GetMaxSequenceNumberByGameKeyAsync(XGPredictGameModule.XGPredictGameKey, cancellationToken) ?? 0) + 1;
            var round = await roundRepository.AddAsync(new Round
            {
                Id = Guid.NewGuid(),
                GameKey = XGPredictGameModule.XGPredictGameKey,
                GameInstanceId = instance.Id,
                SequenceNumber = sequenceNumber,
                StartTime = now.AddMinutes(-1),
                // Generous enough that the round stays Active for the whole
                // E2E run regardless of firstKickoffMinutesFromNow's value —
                // REQ-1303's lock is driven entirely by PredictInstance.
                // LockInstant, independent of Round.EndTime/Closed.
                EndTime = now.AddHours(4),
                AllowGuessChange = true,
            }, cancellationToken);

            var responseMatches = instance.Matches
                .OrderBy(m => m.KickoffUtc)
                .Select(m => new SeedGuessablePredictMatchResponse(m.Id, m.HomeTeamName, m.AwayTeamName))
                .ToList();

            return Results.Ok(new SeedGuessablePredictRoundResponse(round.Id, responseMatches));
        });

        // S-203/REQ-1305: grades one match directly via
        // IPredictInstanceRepository's own normal write path
        // (GetPredictionsForMatchAsync + GradeMatchAsync), bypassing
        // IFootballDataClient/PredictGradingService entirely — an E2E spec
        // has no deterministic way to make a real football-data.org fixture
        // finish with a specific score, the same reason the seed endpoint
        // above bypasses XGPredictGameModule's own real generation logic.
        // Mirrors PredictGradingService.GradeReadyMatchesAsync's own
        // Finished-outcome branch exactly (backend/src/XGArcade.Games.
        // XGPredict/PredictGradingService.cs), just with the actual score
        // supplied by the caller instead of fetched from the external
        // client. Uses the concrete XGPredictScoringStrategy type, not
        // IScoringStrategy/IScoringStrategyResolver — same ADR-0097
        // Decision §2 registration the real grading service depends on.
        app.MapPost("/internal/test-data/grade-predict-match/{matchId:guid}", async (
            Guid matchId,
            GradePredictMatchTestDataRequest request,
            IPredictInstanceRepository predictInstanceRepository,
            XGPredictScoringStrategy scoringStrategy,
            CancellationToken cancellationToken) =>
        {
            var predictions = await predictInstanceRepository.GetPredictionsForMatchAsync(matchId, cancellationToken);
            var finalPointsByPredictionId = predictions.ToDictionary(
                prediction => prediction.Id,
                prediction => scoringStrategy
                    .ScorePrediction(prediction.HomeGoals, prediction.AwayGoals, request.HomeGoals, request.AwayGoals)
                    .FinalPoints);

            await predictInstanceRepository.GradeMatchAsync(
                matchId, request.HomeGoals, request.AwayGoals, finalPointsByPredictionId, cancellationToken);

            return Results.Ok(new GradePredictMatchTestDataResponse(matchId, request.HomeGoals, request.AwayGoals));
        });
    }

    // Shared boilerplate for the three test-data seed call sites above
    // (seed-guessable-round's two players, seed-guessable-path-round's one)
    // — same unique-tag-per-call convention (REQ-209 fallout) and
    // WikidataQid uniqueness, differing only by the caller's own name
    // prefix. Each call site still owns its own attribute/stint writes,
    // since those differ by game.
    private static async Task<Player> CreateUniqueTestPlayerAsync(
        IPlayerRepository playerRepository,
        string namePrefix,
        CancellationToken cancellationToken)
    {
        var nameTag = Guid.NewGuid().ToString("N")[..8];
        return await playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = $"{namePrefix} {nameTag}", WikidataQid = $"Qtest-{Guid.NewGuid()}" },
            cancellationToken);
    }
}

// REQ-304: SequenceNumber is a display-only label alongside RoundId.
public record GenerateRoundResponse(Guid RoundId, int SequenceNumber, string GameKey, DateTime StartTime, DateTime EndTime);

public record ForceCloseRoundResponse(Guid RoundId, DateTime EndTime);

public record SeedGuessableRoundResponse(Guid RoundId, Guid CellId, string CorrectPlayerName, string AlternateCorrectPlayerName);

// S-088/REQ-807 extension: PuzzleId is the "cell id" an E2E test submits
// guesses against via the existing game-agnostic
// POST /rounds/{roundId}/cells/{cellId}/guesses (XGArcade.Api.Guesses.
// GuessEndpoints) — same PathPuzzle.Id-is-the-cell-id contract
// IGameModule.GetCellIdsAsync already documents for xg-path.
public record SeedGuessablePathRoundResponse(Guid RoundId, Guid PuzzleId, string CorrectPlayerName);

// S-203: seed-guessable-predict-round's response — Matches is ordered by
// KickoffUtc (never internal insertion order) so a caller can reliably
// address "the first match" without depending on how this endpoint
// happened to build the list.
public record SeedGuessablePredictRoundResponse(Guid RoundId, IReadOnlyList<SeedGuessablePredictMatchResponse> Matches);

public record SeedGuessablePredictMatchResponse(Guid MatchId, string HomeTeamName, string AwayTeamName);

// S-203: grade-predict-match's request/response — mirrors
// PredictEndpoints.SubmitPredictionRequest's shape (two non-negative goal
// counts), just for the match's real final score rather than a player's
// predicted one.
public record GradePredictMatchTestDataRequest(int HomeGoals, int AwayGoals);

public record GradePredictMatchTestDataResponse(Guid MatchId, int HomeGoals, int AwayGoals);

// Pure log-category marker for ILogger<T> — same pattern as
// InternalGridEndpoints.GridGenerationLogCategory.
internal sealed class RoundGenerationLogCategory;

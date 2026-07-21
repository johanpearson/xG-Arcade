using System.Security.Cryptography;
using System.Text;
using XGArcade.Api.Grid;
using XGArcade.Core.Games;
using XGArcade.Core.Rounds;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGGrid;

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
            IRoundGenerationService roundGenerationService,
            RoundSchedulingOptions options,
            ILogger<RoundGenerationLogCategory> logger,
            double? roundDurationHours,
            CancellationToken cancellationToken) =>
        {
            if (!IsAuthorized(httpContext.Request, configuration))
                return Results.Unauthorized();

            // Optional per-call override (e.g. generate-round.yml's
            // workflow_dispatch input) — takes precedence over
            // RoundSchedulingOptions.RoundDuration for this one generation
            // call only, never mutating the shared singleton. This is a
            // system boundary (bearer-token-gated, but still an external
            // caller), so it's validated here rather than trusted.
            //
            // Floor is 24, not 0: ADR-0027's safety invariant is
            // `RoundDuration >= generate-round.yml`'s cron's max gap between
            // firings, which is a constant 24h now that the cron is daily.
            // A shorter override would let a round close before the next
            // scheduled run generates its successor — REQ-301's "dead app"
            // failure mode, reproduced via this override instead of the
            // cron/duration coupling ADR-0027 fixed. If generate-round.yml's
            // cron cadence ever changes, this floor must be re-derived by
            // hand the same way (see ADR-0027's "For AI agents" section and
            // NOTES.md's 2026-07-10 entry) — don't just bump the number.
            if (roundDurationHours is < 24)
            {
                return Results.Problem(
                    title: "Invalid roundDurationHours",
                    detail: "roundDurationHours must be at least 24 (the daily cron's maximum gap — see ADR-0027) to avoid a round closing before the next scheduled run can generate its successor.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var roundDurationOverride = roundDurationHours is { } hours ? TimeSpan.FromHours(hours) : (TimeSpan?)null;

            try
            {
                // Tier 0 has no admin-driven template management yet — same
                // find-or-create-by-size pattern /internal/grid/generate
                // uses. Moved inside the try (previously ran unguarded
                // before it) so a DB failure here gets the same
                // problem-details treatment as everything below instead of
                // an opaque, empty 500.
                var template = await GridTemplateResolver.GetOrCreateBySizeAsync(
                    gridInstanceRepository, options.GridSize, cancellationToken);

                var round = await roundGenerationService.GenerateNextRoundIfNeededAsync(
                    new RoundConfig { TemplateId = template.Id }, roundDurationOverride, cancellationToken);

                return Results.Ok(new GenerateRoundResponse(round.Id, round.GameKey, round.StartTime, round.EndTime));
            }
            catch (GridGenerationException ex)
            {
                // REQ-101's abort path, surfacing through round generation.
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
                // Anything else (a DB blip, a Supabase/Wikidata client
                // failure that wasn't itself swallowed, ...) previously fell
                // through as an opaque, empty 500 — indistinguishable in
                // generate-round.yml's log from every other failure mode.
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
            IPlayerStoreRepository playerStoreRepository,
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
            var nameTag = Guid.NewGuid().ToString("N")[..8];
            var correctPlayerName = $"Thierry Henry {nameTag}";
            var player = await playerStoreRepository.AddPlayerAsync(
                new Player { Id = Guid.NewGuid(), FullName = correctPlayerName, WikidataQid = $"Qtest-{Guid.NewGuid()}" },
                cancellationToken);
            await playerStoreRepository.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" },
                cancellationToken);
            await playerStoreRepository.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" },
                cancellationToken);

            // REQ-807 originally seeded only one valid player — enough for
            // REQ-201/203/210/303, but REQ-204's live uniqueness needs at
            // least two distinct correct answers to demonstrate anything
            // other than "0% unique" (every correct guesser necessarily
            // sharing the one and only valid answer). A second, equally
            // real Arsenal/France player added here so S-011's E2E suite can
            // have two players each pick a different correct answer.
            var alternateNameTag = Guid.NewGuid().ToString("N")[..8];
            var alternateCorrectPlayerName = $"Robert Pires {alternateNameTag}";
            var alternatePlayer = await playerStoreRepository.AddPlayerAsync(
                new Player { Id = Guid.NewGuid(), FullName = alternateCorrectPlayerName, WikidataQid = $"Qtest-{Guid.NewGuid()}" },
                cancellationToken);
            await playerStoreRepository.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = alternatePlayer.Id, AttributeType = "nationality", AttributeValue = "France" },
                cancellationToken);
            await playerStoreRepository.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = alternatePlayer.Id, AttributeType = "club", AttributeValue = "Arsenal" },
                cancellationToken);

            var round = await roundRepository.AddAsync(new Round
            {
                Id = Guid.NewGuid(),
                GameKey = GridGameModule.XGGridGameKey,
                GameInstanceId = instance.Id,
                StartTime = now.AddMinutes(-1),
                EndTime = now.AddHours(1),
                AllowGuessChange = true,
            }, cancellationToken);

            return Results.Ok(new SeedGuessableRoundResponse(round.Id, cellId, correctPlayerName, alternateCorrectPlayerName));
        });
    }

    private static bool IsAuthorized(HttpRequest request, IConfiguration configuration)
    {
        var expectedToken = configuration["Internal:JobToken"];
        if (string.IsNullOrEmpty(expectedToken))
            return false;

        if (!request.Headers.TryGetValue("Authorization", out var authHeader))
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes($"Bearer {expectedToken}");
        var actualBytes = Encoding.UTF8.GetBytes(authHeader.ToString());

        // FixedTimeEquals rejects a length mismatch immediately on its own —
        // this token authorizes a real write action, so constant-time
        // comparison is used rather than a plain ==, not for any extra
        // protection the explicit length check below would add.
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}

public record GenerateRoundResponse(Guid RoundId, string GameKey, DateTime StartTime, DateTime EndTime);

public record ForceCloseRoundResponse(Guid RoundId, DateTime EndTime);

public record SeedGuessableRoundResponse(Guid RoundId, Guid CellId, string CorrectPlayerName, string AlternateCorrectPlayerName);

// Pure log-category marker for ILogger<T> — same pattern as
// InternalGridEndpoints.GridGenerationLogCategory.
internal sealed class RoundGenerationLogCategory;

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
            CancellationToken cancellationToken) =>
        {
            if (!IsAuthorized(httpContext.Request, configuration))
                return Results.Unauthorized();

            // Tier 0 has no admin-driven template management yet — same
            // find-or-create-by-size pattern /internal/grid/generate uses.
            var template = await GridTemplateResolver.GetOrCreateBySizeAsync(
                gridInstanceRepository, options.GridSize, cancellationToken);

            try
            {
                var round = await roundGenerationService.GenerateNextRoundIfNeededAsync(
                    new RoundConfig { TemplateId = template.Id }, cancellationToken);

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

            const string correctPlayerName = "Thierry Henry";
            var player = await playerStoreRepository.AddPlayerAsync(
                new Player { Id = Guid.NewGuid(), FullName = correctPlayerName, WikidataQid = $"Qtest-{Guid.NewGuid()}" },
                cancellationToken);
            await playerStoreRepository.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" },
                cancellationToken);
            await playerStoreRepository.AddPlayerAttributeAsync(
                new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" },
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

            return Results.Ok(new SeedGuessableRoundResponse(round.Id, cellId, correctPlayerName));
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

public record SeedGuessableRoundResponse(Guid RoundId, Guid CellId, string CorrectPlayerName);

// Pure log-category marker for ILogger<T> — same pattern as
// InternalGridEndpoints.GridGenerationLogCategory.
internal sealed class RoundGenerationLogCategory;

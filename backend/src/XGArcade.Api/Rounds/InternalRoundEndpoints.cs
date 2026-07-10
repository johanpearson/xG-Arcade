using System.Security.Cryptography;
using System.Text;
using XGArcade.Api.Grid;
using XGArcade.Core.Games;
using XGArcade.Core.Rounds;
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

// Pure log-category marker for ILogger<T> — same pattern as
// InternalGridEndpoints.GridGenerationLogCategory.
internal sealed class RoundGenerationLogCategory;

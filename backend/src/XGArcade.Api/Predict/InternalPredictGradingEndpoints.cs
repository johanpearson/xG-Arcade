using XGArcade.Api.Internal;
using XGArcade.Games.XGPredict;

namespace XGArcade.Api.Predict;

// REQ-1305/ADR-0097 Decision §1: a bearer-token-gated /internal/* endpoint,
// registered unconditionally (every environment, including a future real
// Production) — same posture as /internal/generate-round
// (InternalRoundEndpoints.cs, REQ-301's own InternalJobAuthorization
// pattern), never an environment-gated ADR-0006 test-data endpoint. Its
// only caller is .github/workflows/grade-predict-matches.yml's hourly cron.
public static class InternalPredictGradingEndpoints
{
    public static void MapInternalPredictGradingEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/grade-predict-matches", async (
            HttpContext httpContext,
            IConfiguration configuration,
            IPredictGradingService predictGradingService,
            ILogger<PredictGradingLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            if (!InternalJobAuthorization.IsAuthorized(httpContext.Request, configuration))
                return Results.Unauthorized();

            try
            {
                var result = await predictGradingService.GradeReadyMatchesAsync(cancellationToken);

                return Results.Ok(new GradePredictMatchesResponse(
                    result.Graded, result.Voided, result.StillPending, result.Failed));
            }
            catch (Exception ex)
            {
                // PredictGradingService itself already catches and counts
                // per-match FootballDataClientException failures (ADR-0097
                // Decision §3) — anything reaching here is unexpected (e.g.
                // a DB failure fetching the ready-to-grade match list
                // itself), the same "opaque, empty 500 would otherwise be
                // indistinguishable" reasoning InternalRoundEndpoints'
                // own generic catch documents. detail is the exception's
                // own message — the documented narrow exception in
                // docs/coding-guidelines.md for a bearer-token-gated
                // /internal/* endpoint whose only caller is a scheduled
                // job's own CI log, not a player-facing surface.
                logger.LogError(ex, "xG Predict grading run failed unexpectedly.");

                return Results.Problem(
                    title: "xG Predict grading failed unexpectedly",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }
}

public record GradePredictMatchesResponse(int Graded, int Voided, int StillPending, int Failed);

// Pure log-category marker for ILogger<T> — same pattern as
// InternalRoundEndpoints.RoundGenerationLogCategory.
internal sealed class PredictGradingLogCategory;

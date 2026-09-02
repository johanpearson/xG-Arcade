using XGArcade.Api.Internal;

namespace XGArcade.Api.Social;

// REQ-1403/ADR-0103, S-210: a bearer-token-gated /internal/* endpoint,
// registered unconditionally (every environment, including a future real
// Production) — same posture/shape as
// XGArcade.Api.Predict.InternalPredictGradingEndpoints. Its only caller is
// .github/workflows/sweep-matchmaking-pairings.yml's hourly cron; see that
// workflow's own header comment for why this uses the curl+cron
// /internal/* pattern (grade-predict-matches.yml/purge-guest-accounts.yml)
// rather than the CLI-verb pattern
// (.github/workflows/sweep-recent-transfers.yml) the backlog story text
// mentions — ADR-0024 reserves the CLI-verb pattern for long-running,
// multiple-live-external-API-call work, which this fast, bounded,
// pure-in-database sweep is not.
public static class InternalMatchmakingSweepEndpoints
{
    public static void MapInternalMatchmakingSweepEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/sweep-matchmaking-pairings", async (
            HttpContext httpContext,
            IConfiguration configuration,
            MatchmakingSweepService matchmakingSweepService,
            ILogger<MatchmakingSweepLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            if (!InternalJobAuthorization.IsAuthorized(httpContext.Request, configuration))
                return Results.Unauthorized();

            try
            {
                var result = await matchmakingSweepService.RunSweepAsync(cancellationToken);

                return Results.Ok(new SweepMatchmakingPairingsResponse(
                    result.Paired, result.Expired, result.StillWaiting));
            }
            catch (Exception ex)
            {
                // detail is the exception's own message — the documented
                // narrow exception in docs/coding-guidelines.md for a
                // bearer-token-gated /internal/* endpoint whose only caller
                // is a scheduled job's own CI log, not a player-facing
                // surface. Same reasoning as
                // InternalPredictGradingEndpoints' own generic catch.
                logger.LogError(ex, "Matchmaking pairing sweep failed unexpectedly.");

                return Results.Problem(
                    title: "Matchmaking pairing sweep failed unexpectedly",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }
}

public record SweepMatchmakingPairingsResponse(int Paired, int Expired, int StillWaiting);

// Pure log-category marker for ILogger<T> — same pattern as
// InternalPredictGradingEndpoints.PredictGradingLogCategory.
internal sealed class MatchmakingSweepLogCategory;

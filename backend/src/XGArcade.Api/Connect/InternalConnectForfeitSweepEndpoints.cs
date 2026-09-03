using XGArcade.Api.Internal;
using XGArcade.Games.XGConnect;

namespace XGArcade.Api.Connect;

// REQ-1405/ADR-0103, S-212: a bearer-token-gated /internal/* endpoint,
// registered unconditionally (every environment, including a future real
// Production) — same posture/shape as
// XGArcade.Api.Social.InternalMatchmakingSweepEndpoints. Its only caller is
// .github/workflows/sweep-connect-forfeits.yml's hourly cron; see that
// workflow's own header comment for why this uses the curl+cron
// /internal/* pattern rather than the CLI-verb pattern — ADR-0024 reserves
// the CLI-verb pattern for long-running, multiple-live-external-API-call
// work, which this fast, bounded, pure-in-database sweep is not. Injects
// IConnectMatchLifecycleService directly (not a separate XGArcade.Api-level
// sweep service) — unlike MatchmakingSweepService, this sweep needs no
// cross-component orchestration; it's entirely Games.XGConnect-internal.
public static class InternalConnectForfeitSweepEndpoints
{
    public static void MapInternalConnectForfeitSweepEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/sweep-connect-forfeits", async (
            HttpContext httpContext,
            IConfiguration configuration,
            IConnectMatchLifecycleService connectMatchLifecycleService,
            ILogger<ConnectForfeitSweepLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            if (!InternalJobAuthorization.IsAuthorized(httpContext.Request, configuration))
                return Results.Unauthorized();

            try
            {
                var result = await connectMatchLifecycleService.RunForfeitSweepAsync(cancellationToken);

                return Results.Ok(new SweepConnectForfeitsResponse(
                    result.PlayersForfeited, result.MatchesResolved));
            }
            catch (Exception ex)
            {
                // detail is the exception's own message — the documented
                // narrow exception in docs/coding-guidelines.md for a
                // bearer-token-gated /internal/* endpoint whose only caller
                // is a scheduled job's own CI log, not a player-facing
                // surface. Same reasoning as
                // InternalMatchmakingSweepEndpoints' own generic catch.
                logger.LogError(ex, "Connect forfeit sweep failed unexpectedly.");

                return Results.Problem(
                    title: "Connect forfeit sweep failed unexpectedly",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }
}

public record SweepConnectForfeitsResponse(int PlayersForfeited, int MatchesResolved);

// Pure log-category marker for ILogger<T> — same pattern as
// XGArcade.Api.Social.MatchmakingSweepLogCategory.
internal sealed class ConnectForfeitSweepLogCategory;

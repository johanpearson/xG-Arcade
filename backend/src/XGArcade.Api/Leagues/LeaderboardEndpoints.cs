using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Leagues;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Leagues;

// COMP-02 (Core.Leagues): REQ-401/404's Tier 0 slice — the global league is
// the only league that exists yet (custom leagues, REQ-402-404's full
// per-league picker, are deferred per MVP-SCOPE.md), so there is exactly
// one leaderboard to read today. All aggregation logic lives in
// ILeaderboardService (Core.Leagues) — this endpoint only resolves the
// caller and shapes the response, same thin-endpoint pattern GuessEndpoints
// uses around GuessSubmissionService.
public static class LeaderboardEndpoints
{
    public static void MapLeaderboardEndpoints(this WebApplication app)
    {
        app.MapGet("/leagues/global/leaderboard", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            ILeaderboardService leaderboardService,
            CancellationToken cancellationToken) =>
        {
            // Authenticated, not "own data only" — a leaderboard is
            // inherently every member's rank/score, unlike REQ-303's
            // own-guesses-only rule.
            var authProviderUserId = principal.GetAuthProviderUserId();
            if (authProviderUserId is null)
                return Results.Unauthorized();

            var requestingUser = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var entries = await leaderboardService.GetGlobalLeaderboardAsync(requestingUser.Id, cancellationToken);
            var rows = entries
                .Select(entry => new LeaderboardRowResponse(entry.UserId, entry.DisplayName, entry.TotalPoints, entry.IsRequestingUser))
                .ToList();

            return Results.Ok(new LeaderboardResponse(rows));
        }).RequireAuthorization();
    }
}

public record LeaderboardResponse(IReadOnlyList<LeaderboardRowResponse> Rows);

public record LeaderboardRowResponse(Guid UserId, string DisplayName, int TotalPoints, bool IsRequestingUser);

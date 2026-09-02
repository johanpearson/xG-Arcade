using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Api.Leagues;
using XGArcade.Core.Leagues;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Users;

// COMP-01/COMP-02 (Core.Users/Core.Leagues): REQ-411/S-178's read-only
// stats/profile view — GET /users/{userId}/stats. All aggregation still
// lives in ILeaderboardService (Core.Leagues), same thin-endpoint pattern
// LeaderboardEndpoints/GuessEndpoints already establish; this endpoint only
// resolves the caller, validates gameKey, looks up the target user, and
// shapes the response.
//
// No privacy toggle (REQ-411's own "Out of scope"): the handler makes
// exactly the same ILeaderboardService.GetUserStatsAsync call and returns
// exactly the same shape regardless of whether userId is the requesting
// user's own id or someone else's — there is deliberately no branch here
// that special-cases "is this me?".
public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        app.MapGet("/users/{userId:guid}/stats", async (
            Guid userId,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            ILeaderboardService leaderboardService,
            string? gameKey,
            CancellationToken cancellationToken) =>
        {
            // Same known-gameKey allowlist LeaderboardEndpoints already
            // validates against — shared rather than duplicated (ADR-0003:
            // gameKey stays an opaque string at this layer too).
            var validationError = LeaderboardEndpoints.ValidateGameKey(gameKey);
            if (validationError is not null)
                return validationError;

            // REQ-411: this view isn't reachable by a fully logged-out
            // visitor (unlike REQ-511's banner) — no session/unresolvable
            // claim is a 401, same RequestingUserResolver every other
            // *Endpoints.cs file uses.
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var targetUser = await userRepository.GetByIdAsync(userId, cancellationToken);
            if (targetUser is null)
            {
                return Results.Problem(
                    title: "User not found",
                    detail: $"No user with id '{userId}' exists.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            var resolvedGameKey = gameKey ?? GridGameModule.XGGridGameKey;
            var stats = await leaderboardService.GetUserStatsAsync(userId, resolvedGameKey, cancellationToken);

            return Results.Ok(new UserStatsResponse(
                stats.HasRoundsPlayed,
                stats.RoundsPlayed,
                stats.BestFinalPoints,
                stats.AverageFinalPoints,
                stats.Rank));
        }).RequireAuthorization();
    }
}

public record UserStatsResponse(
    bool HasRoundsPlayed,
    int RoundsPlayed,
    int? BestFinalPoints,
    double? AverageFinalPoints,
    int? Rank);

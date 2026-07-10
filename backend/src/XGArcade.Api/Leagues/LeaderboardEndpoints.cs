using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Leagues;

// COMP-02 (Core.Leagues): REQ-401/404's Tier 0 slice — the global league is
// the only league that exists yet (custom leagues, REQ-402-404's full
// per-league picker, are deferred per MVP-SCOPE.md), so there is exactly
// one leaderboard to read today.
public static class LeaderboardEndpoints
{
    public static void MapLeaderboardEndpoints(this WebApplication app)
    {
        app.MapGet("/leagues/global/leaderboard", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            ILeagueRepository leagueRepository,
            IGuessRepository guessRepository,
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

            var globalLeague = await leagueRepository.GetOrCreateGlobalLeagueAsync(cancellationToken);
            var memberUserIds = await leagueRepository.GetMemberUserIdsAsync(globalLeague.Id, cancellationToken);
            var members = await userRepository.GetByIdsAsync(memberUserIds, cancellationToken);
            var totalsByUserId = await guessRepository.GetTotalFinalPointsByUserIdsAsync(memberUserIds, cancellationToken);

            // REQ-404: sorted descending by total score. A member with no
            // locked FinalPoints yet (no rounds closed for them) is absent
            // from totalsByUserId — treated as 0, not omitted from the list.
            var rows = members
                .Select(member => new LeaderboardRowResponse(
                    member.Id,
                    member.DisplayName,
                    totalsByUserId.GetValueOrDefault(member.Id, 0),
                    member.Id == requestingUser.Id))
                .OrderByDescending(row => row.TotalPoints)
                .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Ok(new LeaderboardResponse(rows));
        }).RequireAuthorization();
    }
}

public record LeaderboardResponse(IReadOnlyList<LeaderboardRowResponse> Rows);

public record LeaderboardRowResponse(Guid UserId, string DisplayName, int TotalPoints, bool IsRequestingUser);

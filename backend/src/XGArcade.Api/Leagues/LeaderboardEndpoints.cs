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
// caller, validates the paging query params, and shapes the response, same
// thin-endpoint pattern GuessEndpoints uses around GuessSubmissionService.
//
// REQ-607/S-034: paginated per implementation-document.md §6 —
// `cursor`/`pageSize` query params, cursor-shaped contract even though the
// underlying implementation is still a simple in-memory offset at MVP
// scale (see LeaderboardService's own comment).
public static class LeaderboardEndpoints
{
    // implementation-document.md §6's example (`pageSize=50`) as the
    // default; capped at MaxPageSize to keep REQ-607's "never return an
    // entire league's membership in one unbounded response" guarantee even
    // if a caller asks for more.
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static void MapLeaderboardEndpoints(this WebApplication app)
    {
        app.MapGet("/leagues/global/leaderboard", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            ILeaderboardService leaderboardService,
            int? cursor,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            // REQ-606: validate server-side regardless of client-side
            // validation. A negative cursor is malformed input (not just a
            // stale/out-of-range one — that case is handled downstream as
            // an empty page, not rejected) so it's a 400, not a fallback.
            if (cursor is < 0)
            {
                return Results.Problem(
                    title: "Invalid cursor",
                    detail: "cursor must not be negative.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (pageSize is <= 0 or > MaxPageSize)
            {
                return Results.Problem(
                    title: "Invalid pageSize",
                    detail: $"pageSize must be between 1 and {MaxPageSize}.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Authenticated, not "own data only" — a leaderboard is
            // inherently every member's rank/score, unlike REQ-303's
            // own-guesses-only rule.
            var authProviderUserId = principal.GetAuthProviderUserId();
            if (authProviderUserId is null)
                return Results.Unauthorized();

            var requestingUser = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var page = await leaderboardService.GetGlobalLeaderboardAsync(
                requestingUser.Id, cursor ?? 0, pageSize ?? DefaultPageSize, cancellationToken);

            var rows = page.Rows.Select(ToRowResponse).ToList();
            var requestingUserRow = page.RequestingUserEntry is null ? null : ToRowResponse(page.RequestingUserEntry);

            return Results.Ok(new LeaderboardResponse(rows, requestingUserRow, page.NextCursor, page.HasMore));
        }).RequireAuthorization();
    }

    private static LeaderboardRowResponse ToRowResponse(LeaderboardEntry entry) =>
        new(entry.Rank, entry.UserId, entry.DisplayName, entry.TotalPoints, entry.IsRequestingUser);
}

public record LeaderboardResponse(
    IReadOnlyList<LeaderboardRowResponse> Rows,
    LeaderboardRowResponse? RequestingUserRow,
    int? NextCursor,
    bool HasMore);

public record LeaderboardRowResponse(int Rank, Guid UserId, string DisplayName, int TotalPoints, bool IsRequestingUser);

using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Leagues;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGGrid;

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
//
// REQ-406/407/408 (2026-07-19, ADR-0031/backlog S-053/S-054): three more
// routes alongside the original all-time one, all under
// /leagues/global/leaderboard/* — active-round (live), and past-closed-round
// browsing (list + one round's locked total). Resolving "the active round"
// is exactly RoundEndpoints' own REQ-303 pattern
// (IRoundRepository.GetActiveByGameKeyAsync(GridGameModule.XGGridGameKey, now),
// "no active round" -> 404) — this is the Api (outer composition) layer, so
// hardcoding GridGameModule.XGGridGameKey here is fine (ADR-0003); Core.Leagues
// itself never references it.
//
// REQ-405 (2026-07-20, backlog S-027): one more route,
// /leagues/global/leaderboard/window/{resolution}, for the
// round/week/month/year time-window resolutions alongside the all-time
// default — same auth/paging/gameKey shape as every route above.
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
            IRoundRepository roundRepository,
            ILeaderboardService leaderboardService,
            TimeProvider timeProvider,
            int? cursor,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            var validationError = ValidatePaging(cursor, pageSize);
            if (validationError is not null)
                return validationError;

            // Authenticated, not "own data only" — a leaderboard is
            // inherently every member's rank/score, unlike REQ-303's
            // own-guesses-only rule.
            var requestingUser = await ResolveRequestingUserAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            // REQ-406: folded on top of the locked total when a round is
            // currently active; unaffected (identical to pre-REQ-406
            // behavior) when none is.
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var activeRound = await roundRepository.GetActiveByGameKeyAsync(GridGameModule.XGGridGameKey, now, cancellationToken);

            var page = await leaderboardService.GetGlobalLeaderboardAsync(
                requestingUser.Id, cursor ?? 0, pageSize ?? DefaultPageSize, activeRound, cancellationToken);

            return Results.Ok(ToResponse(page));
        }).RequireAuthorization();

        // REQ-407: participant-only, live, active-round-scoped leaderboard —
        // a genuinely different, recomputed-on-every-read number from the
        // route above (ADR-0031), not this round's eventual locked total
        // (that's REQ-408, reachable only once the round closes).
        app.MapGet("/leagues/global/leaderboard/active-round", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IRoundRepository roundRepository,
            ILeaderboardService leaderboardService,
            TimeProvider timeProvider,
            int? cursor,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            var validationError = ValidatePaging(cursor, pageSize);
            if (validationError is not null)
                return validationError;

            var requestingUser = await ResolveRequestingUserAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var activeRound = await roundRepository.GetActiveByGameKeyAsync(GridGameModule.XGGridGameKey, now, cancellationToken);
            if (activeRound is null)
            {
                // Mirrors RoundEndpoints' own REQ-303 "no active round"
                // response exactly (same title/detail/status shape).
                return Results.Problem(
                    title: "No active round",
                    detail: "There is no active round to show a live leaderboard for right now.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            var page = await leaderboardService.GetActiveRoundLeaderboardAsync(
                requestingUser.Id, activeRound, cursor ?? 0, pageSize ?? DefaultPageSize, cancellationToken);

            return Results.Ok(ToResponse(page));
        }).RequireAuthorization();

        // REQ-408: the browsable-past-rounds list — closed rounds only,
        // most recently closed first, same REQ-607 cursor/pageSize shape.
        app.MapGet("/leagues/global/leaderboard/closed-rounds", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            ILeaderboardService leaderboardService,
            int? cursor,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            var validationError = ValidatePaging(cursor, pageSize);
            if (validationError is not null)
                return validationError;

            var requestingUser = await ResolveRequestingUserAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var page = await leaderboardService.GetClosedRoundsAsync(
                GridGameModule.XGGridGameKey, cursor ?? 0, pageSize ?? DefaultPageSize, cancellationToken);

            var rounds = page.Rounds
                .Select(r => new ClosedRoundSummaryResponse(r.RoundId, r.StartTime, r.EndTime, r.ClosedAt))
                .ToList();

            return Results.Ok(new ClosedRoundListResponse(rounds, page.NextCursor, page.HasMore));
        }).RequireAuthorization();

        // REQ-408: one specific closed round's permanently-locked
        // leaderboard. Not-found and not-closed-yet are distinct, correctly
        // coded responses — never silently served as if complete.
        app.MapGet("/leagues/global/leaderboard/closed-rounds/{roundId:guid}", async (
            Guid roundId,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            ILeaderboardService leaderboardService,
            int? cursor,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            var validationError = ValidatePaging(cursor, pageSize);
            if (validationError is not null)
                return validationError;

            var requestingUser = await ResolveRequestingUserAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await leaderboardService.GetClosedRoundLeaderboardAsync(
                roundId, requestingUser.Id, cursor ?? 0, pageSize ?? DefaultPageSize, cancellationToken);

            return result.Status switch
            {
                ClosedRoundLeaderboardStatus.RoundNotFound => Results.Problem(
                    title: "Round not found",
                    detail: $"No round with id '{roundId}' exists.",
                    statusCode: StatusCodes.Status404NotFound),
                ClosedRoundLeaderboardStatus.RoundNotClosedYet => Results.Problem(
                    title: "Round not closed yet",
                    detail: "This round has not closed yet — its leaderboard is only reachable live, via /leagues/global/leaderboard/active-round, until it closes.",
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.Ok(ToResponse(result.Page!)),
            };
        }).RequireAuthorization();

        // REQ-405: round/week/month/year resolutions alongside the all-time
        // default above. {resolution} is a route string parsed
        // case-insensitively; anything else is a 400, same
        // Results.Problem pattern ValidatePaging already uses.
        app.MapGet("/leagues/global/leaderboard/window/{resolution}", async (
            string resolution,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            ILeaderboardService leaderboardService,
            TimeProvider timeProvider,
            int? cursor,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<LeaderboardWindowResolution>(resolution, ignoreCase: true, out var parsedResolution))
            {
                return Results.Problem(
                    title: "Invalid resolution",
                    detail: $"'{resolution}' is not a valid leaderboard resolution. Expected one of: round, week, month, year.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var validationError = ValidatePaging(cursor, pageSize);
            if (validationError is not null)
                return validationError;

            var requestingUser = await ResolveRequestingUserAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var page = await leaderboardService.GetWindowedLeaderboardAsync(
                requestingUser.Id, GridGameModule.XGGridGameKey, parsedResolution, now, cursor ?? 0, pageSize ?? DefaultPageSize, cancellationToken);

            return Results.Ok(ToResponse(page));
        }).RequireAuthorization();
    }

    // REQ-606: validate server-side regardless of client-side validation. A
    // negative cursor is malformed input (not just a stale/out-of-range one
    // — that case is handled downstream as an empty page, not rejected) so
    // it's a 400, not a fallback. Shared by every route above.
    private static IResult? ValidatePaging(int? cursor, int? pageSize)
    {
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

        return null;
    }

    // Shared by every route above: resolve the authenticated caller's
    // XGArcade.Data.Entities.User row from the auth-provider claim, or null
    // if either step fails — callers translate a null into
    // Results.Unauthorized() themselves (kept out of this helper so it stays
    // a plain resolver, not a response-shaping one, matching ValidatePaging's
    // split of "figure out if something's wrong" from "shape the response").
    private static async Task<User?> ResolveRequestingUserAsync(
        ClaimsPrincipal principal, IUserRepository userRepository, CancellationToken cancellationToken)
    {
        var authProviderUserId = principal.GetAuthProviderUserId();
        if (authProviderUserId is null)
            return null;

        return await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
    }

    private static LeaderboardResponse ToResponse(LeaderboardPage page)
    {
        var rows = page.Rows.Select(ToRowResponse).ToList();
        var requestingUserRow = page.RequestingUserEntry is null ? null : ToRowResponse(page.RequestingUserEntry);
        return new LeaderboardResponse(rows, requestingUserRow, page.NextCursor, page.HasMore);
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

// REQ-408: one browsable closed round for the round-selection list.
public record ClosedRoundSummaryResponse(Guid RoundId, DateTime StartTime, DateTime EndTime, DateTime ClosedAt);

public record ClosedRoundListResponse(
    IReadOnlyList<ClosedRoundSummaryResponse> Rounds,
    int? NextCursor,
    bool HasMore);

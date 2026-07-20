using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Leagues;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Leagues;

// COMP-02 (Core.Leagues): REQ-402/403 — create a custom league and join one
// via its invite code. All business logic (invite code generation/collision
// handling, membership) lives in ILeagueService (Core.Leagues) — this
// endpoint only resolves the caller, validates the request shape, and
// shapes the response, same thin-endpoint/owning-Core-service pattern
// LeaderboardEndpoints.cs already establishes around ILeaderboardService.
//
// Deliberately does not include a per-league leaderboard route or a
// GET /leagues/{id} route — REQ-404's full per-custom-league leaderboard
// (tab switching, per-league reads) is separate, tracked follow-up work;
// this file's scope is create, join, and "list my custom leagues" only.
public static class LeagueEndpoints
{
    // REQ-402: no REQ specifies an exact bound for a custom league's name —
    // chosen generously (longer than DisplayName's 30) since a league name
    // is a shared, one-time choice rather than a per-account identity, but
    // still bounded so it can't be used to submit unbounded text.
    public const int MaxNameLength = 50;

    public static void MapLeagueEndpoints(this WebApplication app)
    {
        // REQ-402: creates a League(Type="custom") with a unique invite
        // code and enrolls the creator as its first member in the same
        // call.
        app.MapPost("/leagues", async (
            CreateLeagueRequest request,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            ILeagueService leagueService,
            CancellationToken cancellationToken) =>
        {
            var name = request.Name.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            {
                return Results.Problem(
                    title: "Invalid league name",
                    detail: $"name must be between 1 and {MaxNameLength} characters.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var requestingUser = await ResolveRequestingUserAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var league = await leagueService.CreateCustomLeagueAsync(name, requestingUser.Id, cancellationToken);

            return Results.Ok(ToResponse(league));
        }).RequireAuthorization();

        // REQ-403: joins the caller to the League identified by inviteCode.
        // An unrecognized code is a 404 with a specific, clear detail
        // message — no membership is ever created on that path. Re-entering
        // a code for a league the caller already belongs to is a 200, not
        // an error (LeagueService.JoinByInviteCodeAsync's own idempotent
        // "already a member" case).
        app.MapPost("/leagues/join", async (
            JoinLeagueRequest request,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            ILeagueService leagueService,
            CancellationToken cancellationToken) =>
        {
            // Trimmed and upper-cased before lookup: invite codes are only
            // ever generated in uppercase (InviteCodeGenerator), so a code
            // typed or pasted in lowercase must still resolve — the caller
            // shouldn't have to match casing exactly to join.
            var inviteCode = request.InviteCode.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(inviteCode))
            {
                return Results.Problem(
                    title: "Invite code required",
                    detail: "inviteCode must not be empty.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var requestingUser = await ResolveRequestingUserAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await leagueService.JoinByInviteCodeAsync(inviteCode, requestingUser.Id, cancellationToken);

            return result.Outcome switch
            {
                JoinLeagueOutcome.InvalidCode => Results.Problem(
                    title: "Invalid invite code",
                    detail: $"No league found with invite code '{inviteCode}'.",
                    statusCode: StatusCodes.Status404NotFound),
                _ => Results.Ok(ToResponse(result.League!)),
            };
        }).RequireAuthorization();

        // This story's "simple list" of the caller's own custom leagues —
        // no per-league leaderboard data, just enough to show which
        // league(s) exist and their invite code for re-sharing.
        app.MapGet("/leagues/mine", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            ILeagueService leagueService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await ResolveRequestingUserAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var leagues = await leagueService.GetMemberLeaguesAsync(requestingUser.Id, cancellationToken);

            return Results.Ok(leagues.Select(ToResponse).ToList());
        }).RequireAuthorization();
    }

    // Same resolver shape as LeaderboardEndpoints.ResolveRequestingUserAsync
    // — kept as its own copy in this file rather than shared, matching how
    // every other *Endpoints.cs file in this codebase already resolves the
    // caller inline/locally rather than through a shared helper.
    private static async Task<User?> ResolveRequestingUserAsync(
        ClaimsPrincipal principal, IUserRepository userRepository, CancellationToken cancellationToken)
    {
        var authProviderUserId = principal.GetAuthProviderUserId();
        if (authProviderUserId is null)
            return null;

        return await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
    }

    private static LeagueResponse ToResponse(League league) =>
        new(league.Id, league.Name, league.InviteCode!);
}

public record CreateLeagueRequest(string Name);

public record JoinLeagueRequest(string InviteCode);

// InviteCode is always non-null here — every League this endpoint file ever
// returns is Type="custom" (LeagueService.CreateCustomLeagueAsync/
// GetMemberLeaguesAsync never surface the Type="global" league).
public record LeagueResponse(Guid Id, string Name, string InviteCode);

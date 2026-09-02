using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Social;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Social;

// COMP-16 (Core.Social)/ADR-0103, S-210: REQ-1403's player-facing surface —
// opting into random matchmaking. Opting in is itself the consent (no
// accept/decline step, see IMatchmakingService's own doc comment), so this
// file is a single write endpoint — the created row's own response body
// (Status/ExpiresAt) is enough to verify the flow, no separate listing
// endpoint is needed for this story's scope. Pairing/expiry is a separate,
// scheduled concern (InternalMatchmakingSweepEndpoints), never triggered
// from a player request.
public static class MatchmakingEndpoints
{
    public static void MapMatchmakingEndpoints(this WebApplication app)
    {
        // REQ-1403: creates a new Waiting MatchmakingOptIn for the caller,
        // with a 12-hour pairing window from now.
        app.MapPost("/matchmaking/opt-in", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IMatchmakingService matchmakingService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var optIn = await matchmakingService.OptInAsync(requestingUser.Id, cancellationToken);

            return Results.Created($"/matchmaking/opt-in/{optIn.Id}", ToResponse(optIn));
        }).RequireAuthorization();
    }

    private static MatchmakingOptInResponse ToResponse(MatchmakingOptIn optIn) =>
        new(
            optIn.Id,
            optIn.OptedInAt,
            optIn.ExpiresAt,
            optIn.Status.ToString(),
            optIn.ResultingMatchId);
}

public record MatchmakingOptInResponse(
    Guid Id, DateTime OptedInAt, DateTime ExpiresAt, string Status, Guid? ResultingMatchId);

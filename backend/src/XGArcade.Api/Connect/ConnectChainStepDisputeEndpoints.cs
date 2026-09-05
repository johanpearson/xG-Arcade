using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGConnect;

namespace XGArcade.Api.Connect;

// COMP-17 (Games.XGConnect)/ADR-0103/ADR-0109, REQ-1412/1413: raising and
// reviewing a dispute of a failed chain step, plus a read of every dispute
// in a match. Every precondition check lives in
// IConnectChainStepDisputeService (Games.XGConnect) — this file only
// resolves the caller and shapes the response, same thin-endpoint/owning-
// service pattern as every other file in this directory. Approve/Deny are
// two distinct endpoints, mirroring Core.Social's FriendEndpoints
// accept/decline precedent (POST .../accept, POST .../decline), rather than
// a single endpoint taking an "approve" flag in the body.
public static class ConnectChainStepDisputeEndpoints
{
    public static void MapConnectChainStepDisputeEndpoints(this WebApplication app)
    {
        // REQ-1412: raise a dispute against one of the caller's own failed
        // chain steps.
        app.MapPost("/matches/{matchId:guid}/chain-steps/{chainStepId:guid}/dispute", async (
            Guid matchId,
            Guid chainStepId,
            RaiseChainStepDisputeRequest request,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IConnectChainStepDisputeService connectChainStepDisputeService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await connectChainStepDisputeService.RaiseDisputeAsync(
                matchId, chainStepId, requestingUser.Id, request.ClaimedClubName, cancellationToken);

            return result.Outcome switch
            {
                RaiseChainStepDisputeOutcome.Raised => Results.Ok(ToResponse(result.Dispute!)),
                RaiseChainStepDisputeOutcome.MatchNotFound => Results.NotFound(),
                RaiseChainStepDisputeOutcome.StepNotFound => Results.NotFound(),
                RaiseChainStepDisputeOutcome.NotAParticipant => Results.Problem(
                    title: "Not a participant",
                    detail: "Only the two players in this match may dispute a chain step in it.",
                    statusCode: StatusCodes.Status403Forbidden),
                RaiseChainStepDisputeOutcome.NotStepOwner => Results.Problem(
                    title: "Not your step",
                    detail: "You may only dispute your own chain step.",
                    statusCode: StatusCodes.Status403Forbidden),
                RaiseChainStepDisputeOutcome.StepNotInvalid => Results.Problem(
                    title: "Step is not invalid",
                    detail: "Only a failed chain-step submission may be disputed.",
                    statusCode: StatusCodes.Status409Conflict),
                RaiseChainStepDisputeOutcome.AlreadyDisputed => Results.Problem(
                    title: "Already disputed",
                    detail: "This step has already been disputed — a step may be disputed at most once.",
                    statusCode: StatusCodes.Status409Conflict),
                RaiseChainStepDisputeOutcome.StepSuperseded => Results.Problem(
                    title: "Step superseded",
                    detail: "This is no longer your most recent invalid step at this position — an old, " +
                            "superseded failure can't be disputed.",
                    statusCode: StatusCodes.Status409Conflict),
                RaiseChainStepDisputeOutcome.InvalidClaimedClubName => Results.Problem(
                    title: "Invalid claimed club",
                    detail: "claimedClubName is required.",
                    statusCode: StatusCodes.Status400BadRequest),
                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
            };
        }).RequireAuthorization();

        // REQ-1413: only the match's other participant may approve.
        app.MapPost("/matches/{matchId:guid}/disputes/{disputeId:guid}/approve", async (
            Guid matchId,
            Guid disputeId,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IConnectChainStepDisputeService connectChainStepDisputeService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await connectChainStepDisputeService.ReviewDisputeAsync(
                matchId, disputeId, requestingUser.Id, approve: true, cancellationToken);

            return ToReviewResult(result);
        }).RequireAuthorization();

        // REQ-1413: only the match's other participant may deny.
        app.MapPost("/matches/{matchId:guid}/disputes/{disputeId:guid}/deny", async (
            Guid matchId,
            Guid disputeId,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IConnectChainStepDisputeService connectChainStepDisputeService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await connectChainStepDisputeService.ReviewDisputeAsync(
                matchId, disputeId, requestingUser.Id, approve: false, cancellationToken);

            return ToReviewResult(result);
        }).RequireAuthorization();

        // REQ-1412/1413: every dispute in this match, in the caller's own
        // perspective — backs both "what's the status of my own dispute"
        // and "what do I need to review as the opponent."
        app.MapGet("/matches/{matchId:guid}/disputes", async (
            Guid matchId,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IConnectChainStepDisputeService connectChainStepDisputeService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await connectChainStepDisputeService.GetDisputesForMatchAsync(matchId, requestingUser.Id, cancellationToken);

            return result.Outcome switch
            {
                GetChainStepDisputesOutcome.Found => Results.Ok(result.Disputes.Select(ToResponse).ToList()),
                GetChainStepDisputesOutcome.MatchNotFound => Results.NotFound(),
                GetChainStepDisputesOutcome.NotAParticipant => Results.Problem(
                    title: "Not a participant",
                    detail: "Only the two players in this match may view its disputes.",
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
            };
        }).RequireAuthorization();
    }

    private static IResult ToReviewResult(ReviewChainStepDisputeResult result) => result.Outcome switch
    {
        ReviewChainStepDisputeOutcome.Approved => Results.Ok(ToResponse(result.Dispute!)),
        ReviewChainStepDisputeOutcome.Denied => Results.Ok(ToResponse(result.Dispute!)),
        ReviewChainStepDisputeOutcome.MatchNotFound => Results.NotFound(),
        ReviewChainStepDisputeOutcome.DisputeNotFound => Results.NotFound(),
        ReviewChainStepDisputeOutcome.NotAParticipant => Results.Problem(
            title: "Not a participant",
            detail: "Only the two players in this match may review a dispute in it.",
            statusCode: StatusCodes.Status403Forbidden),
        ReviewChainStepDisputeOutcome.CannotReviewOwnDispute => Results.Problem(
            title: "Cannot review your own dispute",
            detail: "Only the other participant in this match may approve or deny a dispute.",
            statusCode: StatusCodes.Status403Forbidden),
        ReviewChainStepDisputeOutcome.AlreadyReviewed => Results.Problem(
            title: "Already reviewed",
            detail: "This dispute has already been approved or denied.",
            statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
    };

    private static ChainStepDisputeResponse ToResponse(ConnectChainStepDispute dispute) =>
        new(dispute.Id, dispute.ConnectChainStepId, dispute.ClaimedClubName, dispute.Status.ToString(), dispute.RaisedAt, dispute.ReviewedAt);

    private static ChainStepDisputeListItemResponse ToResponse(ChainStepDisputeView view) =>
        new(view.DisputeId, view.ChainStepId, view.Position, view.ClaimedClubName, view.Status.ToString(),
            view.RaisedAt, view.ReviewedAt, view.RaisedByMe);
}

public record RaiseChainStepDisputeRequest(string ClaimedClubName);

// Status is a string (Enum.ToString()) — same convention
// ConnectMatchQueryEndpoints/ChallengeEndpoints already use for their own
// Status/Outcome fields.
public record ChainStepDisputeResponse(
    Guid DisputeId, Guid ChainStepId, string ClaimedClubName, string Status, DateTime RaisedAt, DateTime? ReviewedAt);

public record ChainStepDisputeListItemResponse(
    Guid DisputeId, Guid ChainStepId, int Position, string ClaimedClubName, string Status,
    DateTime RaisedAt, DateTime? ReviewedAt, bool RaisedByMe);

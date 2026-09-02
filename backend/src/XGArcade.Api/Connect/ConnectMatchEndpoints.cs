using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGConnect;

namespace XGArcade.Api.Connect;

// COMP-17 (Games.XGConnect)/ADR-0103, S-211: REQ-1404's target-pick
// selection surface. Every precondition check (participant, already-locked,
// trivial-pair rejection, live-lookup-unavailable) lives in
// IConnectTargetPickService (Games.XGConnect) — this file only resolves the
// caller and shapes the response, same thin-endpoint/owning-service pattern
// XGArcade.Api.Social.ChallengeEndpoints already establishes.
public static class ConnectMatchEndpoints
{
    public static void MapConnectMatchEndpoints(this WebApplication app)
    {
        // REQ-1404: either player in an xG Connect match selects (or, before
        // the match officially starts, replaces) their own target pick.
        app.MapPost("/matches/{matchId:guid}/target-pick", async (
            Guid matchId,
            SubmitTargetPickRequest request,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IConnectTargetPickService connectTargetPickService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await connectTargetPickService.SubmitTargetPickAsync(
                matchId, requestingUser.Id, request.TargetPlayerId, cancellationToken);

            return result.Outcome switch
            {
                SubmitTargetPickOutcome.RecordedAwaitingOther => Results.Ok(ToResponse(result.TargetPick!)),
                SubmitTargetPickOutcome.RecordedAndLocked => Results.Ok(ToResponse(result.TargetPick!)),
                SubmitTargetPickOutcome.MatchNotFound => Results.NotFound(),
                SubmitTargetPickOutcome.NotAParticipant => Results.Problem(
                    title: "Not a participant",
                    detail: "Only the two players in this match may select a target pick for it.",
                    statusCode: StatusCodes.Status403Forbidden),
                SubmitTargetPickOutcome.AlreadyLocked => Results.Problem(
                    title: "Target pick already locked",
                    detail: "Your target pick is already locked in for this match and can no longer be changed.",
                    statusCode: StatusCodes.Status409Conflict),
                // REQ-1404: the second (completing) selection is rejected —
                // the first player's own selection is unaffected, and the
                // match does not officially start until a non-trivially-
                // connected pair is in place.
                SubmitTargetPickOutcome.TriviallyConnected => Results.Problem(
                    title: "Target picks are already connected",
                    detail: "These two target players already share a club with an overlapping time period. " +
                            "Pick a different target instead.",
                    statusCode: StatusCodes.Status409Conflict),
                // ADR-0010/0011: same shape GuessEndpoints.cs uses for
                // GuessSubmissionOutcome.LiveLookupUnavailable — the shared
                // career-overlap check's live Wikidata refresh didn't
                // complete in time, so this pair's connectivity is
                // genuinely unknown, not a rejection of anything the player
                // did. Nothing was persisted; the client should simply
                // retry.
                SubmitTargetPickOutcome.LiveLookupUnavailable => Results.Problem(
                    title: "Live verification unavailable",
                    detail: "We couldn't verify this target pick against our live data source in time. Please try again.",
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
            };
        }).RequireAuthorization();
    }

    private static SubmitTargetPickResponse ToResponse(ConnectTargetPick targetPick) =>
        new(targetPick.TargetPlayerId, targetPick.SelectedAt, targetPick.IsLocked);
}

public record SubmitTargetPickRequest(Guid TargetPlayerId);

// Locked (REQ-1404/1405): true only once BOTH players' target picks are
// fixed (this submission was the completing, non-trivial one) — false
// whenever this is the first-in/awaiting-the-other-player pick, or a
// pre-lock resubmission replacing an earlier unlocked pick. Does not itself
// mean the match has officially started (ConnectMatch.Status) — that's
// S-212's own separate concept, derived from this same underlying
// IsLocked flag but not exposed by this endpoint.
public record SubmitTargetPickResponse(Guid TargetPlayerId, DateTime SelectedAt, bool Locked);

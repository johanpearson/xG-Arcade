using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGConnect;

namespace XGArcade.Api.Connect;

// COMP-17 (Games.XGConnect)/ADR-0103, S-213: REQ-1406's incremental
// connection-chain-step submission surface. Same thin-endpoint/owning-
// service pattern as ConnectMatchEndpoints.cs (own file, same "one feature
// area, one file" split as ChallengeEndpoints/MatchmakingEndpoints) — every
// precondition check and the live overlap validation itself lives in
// IConnectChainStepService (Games.XGConnect); this file only resolves the
// caller and shapes the response.
//
// GuessEndpoints.cs precedent (REQ-1406 mirrors it deliberately): a step
// that fails live validation is a normal 200 OK with IsValid: false in the
// body, never a 4xx/5xx — only genuine precondition failures (match not
// found/not a participant/not active/chain already complete) or a technical
// live-lookup failure get a non-200 status.
public static class ConnectChainStepEndpoints
{
    public static void MapConnectChainStepEndpoints(this WebApplication app)
    {
        app.MapPost("/matches/{matchId:guid}/chain-steps", async (
            Guid matchId,
            SubmitChainStepRequest request,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IConnectChainStepService connectChainStepService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await connectChainStepService.SubmitChainStepAsync(
                matchId, requestingUser.Id, request.CandidatePlayerName, request.ClaimedClubName, cancellationToken);

            return result.Outcome switch
            {
                SubmitChainStepOutcome.StepAccepted => Results.Ok(ToResponse(result.ChainStep!, chainComplete: false)),
                SubmitChainStepOutcome.ChainClosed => Results.Ok(ToResponse(result.ChainStep!, chainComplete: true)),
                SubmitChainStepOutcome.InvalidStep => Results.Ok(ToResponse(result.ChainStep!, chainComplete: false)),
                // REQ-1407/S-214: still a normal 200 — same "your
                // submission's outcome, not an error" shape as
                // InvalidStep/StepAccepted/ChainClosed (GuessEndpoints'
                // precedent) — but Busted: true distinguishes it from an
                // ordinary first-attempt failure for a caller that needs to
                // know its match participation just ended.
                SubmitChainStepOutcome.Busted => Results.Ok(ToResponse(result.ChainStep!, chainComplete: false, busted: true)),
                // REQ-1406/ADR-0007: candidatePlayerName didn't resolve to
                // any known player at all — nothing was (or could be)
                // persisted, since ConnectChainStep.CandidatePlayerId is a
                // required FK. Still a normal 200, same "your submission
                // just didn't work out" shape as InvalidStep, not a 4xx —
                // the player mistyped or picked an unknown name, not a
                // precondition failure about the match/caller/state.
                SubmitChainStepOutcome.CandidateNotFound => Results.Ok(
                    new SubmitChainStepResponse(false, false, null, null, null, null)),
                SubmitChainStepOutcome.MatchNotFound => Results.NotFound(),
                SubmitChainStepOutcome.NotAParticipant => Results.Problem(
                    title: "Not a participant",
                    detail: "Only the two players in this match may submit chain steps for it.",
                    statusCode: StatusCodes.Status403Forbidden),
                // Mirrors GuessEndpoints.RoundNotActive's Problem shape/wording style.
                SubmitChainStepOutcome.MatchNotActive => Results.Problem(
                    title: "Match is not active",
                    detail: "Chain steps can only be submitted while the match is active.",
                    statusCode: StatusCodes.Status409Conflict),
                // Mirrors GuessEndpoints.CellAlreadySolved's Problem shape/wording style.
                SubmitChainStepOutcome.ChainAlreadyComplete => Results.Problem(
                    title: "Chain already complete",
                    detail: "Your chain for this match is already complete and locked — no further steps may be submitted.",
                    statusCode: StatusCodes.Status409Conflict),
                // REQ-1407/S-214: same Problem shape as ChainAlreadyComplete
                // above — the caller's own slot already reached a terminal
                // state (busted or timed out) before this submission.
                SubmitChainStepOutcome.AlreadyForfeited => Results.Problem(
                    title: "Already forfeited",
                    detail: "Your participation in this match has already ended (busted or timed out) — no further steps may be submitted.",
                    statusCode: StatusCodes.Status409Conflict),
                // ADR-0010/0011: same shape ConnectMatchEndpoints.cs uses for
                // SubmitTargetPickOutcome.LiveLookupUnavailable.
                SubmitChainStepOutcome.LiveLookupUnavailable => Results.Problem(
                    title: "Live verification unavailable",
                    detail: "We couldn't verify this step against our live data source in time. Please try again.",
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
            };
        }).RequireAuthorization();
    }

    private static SubmitChainStepResponse ToResponse(ConnectChainStep chainStep, bool chainComplete, bool busted = false) =>
        new(chainStep.IsValid, chainComplete, chainStep.Position, chainStep.AttemptNumber,
            chainStep.CandidatePlayerId, chainStep.ClaimedClubName, busted);
}

public record SubmitChainStepRequest(string CandidatePlayerName, string ClaimedClubName);

// IsValid/ChainComplete/Position/AttemptNumber/CandidatePlayerId/
// ClaimedClubName cover what a test needs to assert on for this
// backend-only story — the frontend chain-builder UI consuming this is
// S-218, so this DTO is deliberately not over-designed for a UI that
// doesn't exist yet. CandidatePlayerId/ClaimedClubName/Position/
// AttemptNumber are null only for CandidateNotFound, where nothing was
// persisted at all. Busted (REQ-1407/S-214) defaults to false and is true
// only for the Busted outcome — see SubmitChainStepOutcome.Busted's own doc
// comment.
public record SubmitChainStepResponse(
    bool IsValid, bool ChainComplete, int? Position, int? AttemptNumber, Guid? CandidatePlayerId, string? ClaimedClubName,
    bool Busted = false);

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
                matchId, requestingUser.Id, request.CandidatePlayerName, request.CandidateWikidataQid, cancellationToken);

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
                    new SubmitChainStepResponse(null, false, false, null, null, null, null, null, null)),
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
        new(chainStep.Id, chainStep.IsValid, chainComplete, chainStep.Position, chainStep.AttemptNumber, chainStep.CandidatePlayerId,
            chainStep.MatchedClubName, chainStep.MatchedOverlapStartYear, chainStep.MatchedOverlapEndYear, busted);
}

// Design change (2026-09-04, REQ-1406, ADR-0104): no longer takes a
// ClaimedClubName — see IConnectChainStepService.SubmitChainStepAsync's own
// doc comment.
//
// CandidateWikidataQid (bug fix, 2026-09-05, ADR-0107): optional — sent by
// the frontend only when the player picked a real /players/autocomplete
// suggestion (ChainBuilder.tsx now requires a click for exactly this
// reason). Null when absent/omitted, which falls back to
// CandidatePlayerName-only resolution — see
// IConnectChainStepService.SubmitChainStepAsync's own doc comment for why
// that fallback still exists.
public record SubmitChainStepRequest(string CandidatePlayerName, string? CandidateWikidataQid = null);

// IsValid/ChainComplete/Position/AttemptNumber/CandidatePlayerId/
// MatchedClubName/MatchedOverlapStartYear/MatchedOverlapEndYear cover what
// a test needs to assert on for this backend-only story — the frontend
// chain-builder UI consuming this is S-218, so this DTO is deliberately not
// over-designed for a UI that doesn't exist yet. CandidatePlayerId/Position/
// AttemptNumber are null only for CandidateNotFound, where nothing was
// persisted at all; MatchedClubName/MatchedOverlapStartYear/
// MatchedOverlapEndYear are additionally null whenever IsValid is false (no
// club was found at all — see ConnectChainStep's own doc comment). Busted
// (REQ-1407/S-214) defaults to false and is true only for the Busted
// outcome — see SubmitChainStepOutcome.Busted's own doc comment.
//
// ChainStepId (REQ-1412, 2026-09-05): the persisted step's own id, null only
// for CandidateNotFound (same as CandidatePlayerId/Position/AttemptNumber
// above — nothing was persisted at all). Added specifically so the client
// can immediately offer "dispute this ruling" on a just-returned invalid or
// Busted outcome without a separate round-trip to GET /matches/{matchId}
// first (whose myChainSteps items carry the same id via
// ConnectChainStepDetailResponse.ChainStepId) — see
// IConnectChainStepDisputeService.RaiseDisputeAsync's own doc comment for
// what this id is used for.
public record SubmitChainStepResponse(
    Guid? ChainStepId, bool IsValid, bool ChainComplete, int? Position, int? AttemptNumber, Guid? CandidatePlayerId,
    string? MatchedClubName, int? MatchedOverlapStartYear, int? MatchedOverlapEndYear,
    bool Busted = false);

using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGConnect;

namespace XGArcade.Api.Connect;

// COMP-17 (Games.XGConnect)/ADR-0103, S-218 prep: the read-only surface
// unblocking S-218's frontend gameplay screen — every xG Connect endpoint
// before this (ConnectMatchEndpoints/ConnectChainStepEndpoints/
// ConnectChatEndpoints) was write-only, leaving no way to read a match's
// current state or discover which matchIds belong to the caller. Every
// non-trivial rule (perspective translation, terminal-state derivation,
// REQ-1404's mutual-invisibility-until-locked rule) lives in
// IConnectMatchQueryService (Games.XGConnect) — this file only resolves
// the caller and shapes the response, same thin-endpoint/owning-service
// pattern as every other file in this directory.
public static class ConnectMatchQueryEndpoints
{
    public static void MapConnectMatchQueryEndpoints(this WebApplication app)
    {
        // REQ-1411: every match (open or resolved) the caller participates
        // in, in the caller's own perspective.
        app.MapGet("/matches", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IConnectMatchQueryService connectMatchQueryService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var matches = await connectMatchQueryService.GetMatchesForUserAsync(requestingUser.Id, cancellationToken);

            return Results.Ok(matches.Select(ToResponse).ToList());
        }).RequireAuthorization();

        // REQ-1404/1405/1406/1409: full single-match detail for the
        // gameplay screen.
        app.MapGet("/matches/{matchId:guid}", async (
            Guid matchId,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IConnectMatchQueryService connectMatchQueryService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await connectMatchQueryService.GetMatchDetailAsync(matchId, requestingUser.Id, cancellationToken);

            return result.Outcome switch
            {
                ConnectMatchDetailOutcome.Found => Results.Ok(ToResponse(result.Detail!)),
                ConnectMatchDetailOutcome.MatchNotFound => Results.NotFound(),
                // Mirrors ConnectMatchEndpoints/ConnectChainStepEndpoints/
                // ConnectChatEndpoints' own NotAParticipant Problem
                // shape/wording style.
                ConnectMatchDetailOutcome.NotAParticipant => Results.Problem(
                    title: "Not a participant",
                    detail: "Only the two players in this match may view its detail.",
                    statusCode: StatusCodes.Status403Forbidden),
                _ => throw new InvalidOperationException($"Unhandled ConnectMatchDetailOutcome '{result.Outcome}'."),
            };
        }).RequireAuthorization();
    }

    private static ConnectMatchListItemResponse ToResponse(ConnectMatchSummary summary) =>
        new(
            summary.MatchId,
            summary.OpponentUserId,
            summary.OpponentDisplayName,
            summary.Status.ToString(),
            summary.CreatedAt,
            summary.StartedAt,
            summary.DeadlineUtc,
            summary.ResolvedAt,
            summary.Outcome.ToString(),
            summary.AwaitingMyAction);

    private static ConnectTargetPickResponse ToResponse(ConnectTargetPickView view) =>
        new(view.TargetPlayerId, view.TargetPlayerName, view.Locked);

    private static ConnectChainStepDetailResponse ToResponse(ConnectChainStepView view) =>
        new(view.Position, view.AttemptNumber, view.CandidatePlayerId, view.CandidatePlayerName,
            view.MatchedClubName, view.MatchedOverlapStartYear, view.MatchedOverlapEndYear,
            view.IsValid, view.ClosesChain, view.SubmittedAt);

    private static ConnectTerminalStateResponse ToResponse(ConnectTerminalState state) =>
        new(state.Busted, state.TimedOut, state.Completed);

    private static ConnectMatchDetailResponse ToResponse(ConnectMatchDetail detail) =>
        new(
            detail.Status.ToString(),
            detail.CreatedAt,
            detail.StartedAt,
            detail.DeadlineUtc,
            detail.ResolvedAt,
            detail.Outcome.ToString(),
            detail.OpponentUserId,
            detail.OpponentDisplayName,
            detail.MyTargetPick is null ? null : ToResponse(detail.MyTargetPick),
            detail.OpponentTargetPick is null ? null : ToResponse(detail.OpponentTargetPick),
            detail.MyChainSteps.Select(ToResponse).ToList(),
            ToResponse(detail.MyTerminalState),
            ToResponse(detail.OpponentTerminalState),
            detail.MyScore,
            detail.OpponentScore);
}

// Status/Outcome are strings (Enum.ToString()) — same convention
// ChallengeEndpoints.ChallengeResponse already uses for its own Status
// field, rather than exposing the raw enum's numeric JSON value.
// OpponentDisplayName mirrors OpponentUserId's own nullability exactly —
// null whenever OpponentUserId is null (REQ-710 anonymization), never a
// placeholder — see ConnectMatchSummary.OpponentDisplayName's own doc
// comment (Games.XGConnect) for the batch-resolve this is a pass-through of.
public record ConnectMatchListItemResponse(
    Guid MatchId,
    Guid? OpponentUserId,
    string? OpponentDisplayName,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? DeadlineUtc,
    DateTime? ResolvedAt,
    string Outcome,
    bool AwaitingMyAction);

public record ConnectTargetPickResponse(Guid TargetPlayerId, string TargetPlayerName, bool Locked);

public record ConnectChainStepDetailResponse(
    int Position,
    int AttemptNumber,
    Guid CandidatePlayerId,
    string CandidatePlayerName,
    string? MatchedClubName,
    int? MatchedOverlapStartYear,
    int? MatchedOverlapEndYear,
    bool IsValid,
    bool ClosesChain,
    DateTime SubmittedAt);

public record ConnectTerminalStateResponse(bool Busted, bool TimedOut, bool Completed);

// OpponentTargetPick is null both while the opponent hasn't picked yet and
// (REQ-1404) whenever Status is still "AwaitingTargetPicks" — see
// ConnectMatchDetail.OpponentTargetPick's own doc comment (Games.
// XGConnect) for why. OpponentTerminalState never carries the opponent's
// actual chain steps, only whether they've reached a terminal state — see
// IConnectMatchQueryService's own doc comment.
public record ConnectMatchDetailResponse(
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? DeadlineUtc,
    DateTime? ResolvedAt,
    string Outcome,
    Guid? OpponentUserId,
    string? OpponentDisplayName,
    ConnectTargetPickResponse? MyTargetPick,
    ConnectTargetPickResponse? OpponentTargetPick,
    IReadOnlyList<ConnectChainStepDetailResponse> MyChainSteps,
    ConnectTerminalStateResponse MyTerminalState,
    ConnectTerminalStateResponse OpponentTerminalState,
    int? MyScore,
    int? OpponentScore);

using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Social;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Social;

// COMP-16 (Core.Social)/ADR-0103, S-210: REQ-1402's direct-challenge
// send/accept/decline surface. Every precondition check (friendship,
// duplicate-pending, found/responder/still-pending) lives in
// IChallengeService (Core.Social) — this file only resolves the caller,
// shapes the response, and (accept only) performs the one piece of
// orchestration ADR-0103 requires to live in XGArcade.Api rather than
// Core.Social: creating the actual ConnectMatch row via
// IConnectMatchRepository once IChallengeService confirms the accept is
// valid. Same thin-endpoint/owning-Core-service pattern FriendEndpoints.cs
// already establishes.
public static class ChallengeEndpoints
{
    public static void MapChallengeEndpoints(this WebApplication app)
    {
        // REQ-1402: creates a Pending Challenge from the caller to
        // request.ChallengedUserId — only allowed between existing friends.
        app.MapPost("/challenges", async (
            SendChallengeRequest request,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IChallengeService challengeService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await challengeService.SendChallengeAsync(requestingUser.Id, request.ChallengedUserId, cancellationToken);

            return result.Outcome switch
            {
                SendChallengeOutcome.Sent => Results.Created(
                    $"/challenges/{result.Challenge!.Id}", ToResponse(result.Challenge)),
                SendChallengeOutcome.NotFriends => Results.Problem(
                    title: "Not friends",
                    detail: "A direct challenge can only be sent to an existing friend.",
                    statusCode: StatusCodes.Status403Forbidden),
                SendChallengeOutcome.DuplicatePending => Results.Problem(
                    title: "Duplicate pending challenge",
                    detail: "A pending challenge already exists between you and this user.",
                    statusCode: StatusCodes.Status409Conflict),
                _ => throw new InvalidOperationException($"Unhandled SendChallengeOutcome '{result.Outcome}'."),
            };
        }).RequireAuthorization();

        // REQ-1402: the challenged user accepts — resolves the challenge as
        // Accepted and, only once that resolution succeeds, creates the new
        // active ConnectMatch (COMP-17) directly here. matchId is
        // pre-generated so IChallengeService can validate every accept
        // precondition and persist Accepted+ResultingMatchId in the same
        // repository call, before this handler ever writes a
        // Games.XGConnect-owned row — an invalid accept attempt (not found/
        // not-yours/already-resolved) therefore never leaves an orphan
        // ConnectMatch nobody points to. See ADR-0103's "For AI agents"
        // section for why this write can never move into ChallengeService.
        app.MapPost("/challenges/{id:guid}/accept", async (
            Guid id,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IChallengeService challengeService,
            IConnectMatchRepository connectMatchRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var matchId = Guid.NewGuid();
            var result = await challengeService.AcceptChallengeAsync(id, requestingUser.Id, matchId, cancellationToken);

            if (result.Outcome != ResolveChallengeOutcome.Resolved)
                return ToProblem(result.Outcome);

            await connectMatchRepository.AddMatchAsync(new ConnectMatch
            {
                Id = matchId,
                PlayerAUserId = result.Challenge!.ChallengerUserId,
                PlayerBUserId = result.Challenge.ChallengedUserId,
                CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            }, cancellationToken);

            return Results.Ok(ToResponse(result.Challenge));
        }).RequireAuthorization();

        // REQ-1402: the challenged user declines — resolves the challenge
        // as Declined, no ConnectMatch is ever created, and the challenger
        // remains free to send a new challenge later.
        app.MapPost("/challenges/{id:guid}/decline", async (
            Guid id,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IChallengeService challengeService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await challengeService.DeclineChallengeAsync(id, requestingUser.Id, cancellationToken);

            return result.Outcome == ResolveChallengeOutcome.Resolved
                ? Results.Ok(ToResponse(result.Challenge!))
                : ToProblem(result.Outcome);
        }).RequireAuthorization();

        // REQ-1402: every challenge currently Pending where the caller is
        // the challenged party — lets a player see who's challenged them.
        app.MapGet("/challenges/pending", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IChallengeService challengeService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var pending = await challengeService.GetPendingChallengesAsync(requestingUser.Id, cancellationToken);

            return Results.Ok(pending.Select(ToResponse).ToList());
        }).RequireAuthorization();
    }

    private static IResult ToProblem(ResolveChallengeOutcome outcome) => outcome switch
    {
        ResolveChallengeOutcome.NotFound => Results.Problem(
            title: "Challenge not found",
            detail: "No challenge found with that id.",
            statusCode: StatusCodes.Status404NotFound),
        ResolveChallengeOutcome.NotYourChallenge => Results.Problem(
            title: "Not your challenge",
            detail: "Only the challenged player can accept or decline it.",
            statusCode: StatusCodes.Status403Forbidden),
        ResolveChallengeOutcome.AlreadyResolved => Results.Problem(
            title: "Already resolved",
            detail: "This challenge has already been accepted or declined.",
            statusCode: StatusCodes.Status409Conflict),
        _ => throw new InvalidOperationException($"Unhandled ResolveChallengeOutcome '{outcome}'."),
    };

    private static ChallengeResponse ToResponse(Challenge challenge) =>
        new(
            challenge.Id,
            challenge.ChallengerUserId,
            challenge.ChallengedUserId,
            challenge.Status.ToString(),
            challenge.CreatedAt,
            challenge.ResolvedAt,
            challenge.ResultingMatchId);
}

public record SendChallengeRequest(Guid ChallengedUserId);

public record ChallengeResponse(
    Guid Id,
    Guid ChallengerUserId,
    Guid ChallengedUserId,
    string Status,
    DateTime CreatedAt,
    DateTime? ResolvedAt,
    Guid? ResultingMatchId);

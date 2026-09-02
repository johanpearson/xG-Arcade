using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Social;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Social;

// COMP-16 (Core.Social)/ADR-0103, S-209: REQ-1401's send/accept/decline
// friend-request surface. All business logic (duplicate-pending/self-
// request/already-friends rejection, symmetric Friendship creation on
// accept) lives in IFriendService (Core.Social) — this endpoint file only
// resolves the caller, validates the request shape, and shapes the
// response, same thin-endpoint/owning-Core-service pattern
// LeagueEndpoints.cs already establishes around ILeagueService.
//
// Deliberately does not touch Games.XGConnect (COMP-17) or any
// match/challenge concept — REQ-1402+ is separate, tracked follow-up work
// (S-210+); this file's scope is send/accept/decline plus enough listing
// (pending requests, current friendships) to exercise and verify that flow.
public static class FriendEndpoints
{
    public static void MapFriendEndpoints(this WebApplication app)
    {
        // REQ-1401: creates a Pending FriendRequest from the caller to
        // request.RecipientUserId.
        app.MapPost("/friends/requests", async (
            SendFriendRequestRequest request,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IFriendService friendService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await friendService.SendFriendRequestAsync(requestingUser.Id, request.RecipientUserId, cancellationToken);

            return result.Outcome switch
            {
                SendFriendRequestOutcome.Sent => Results.Created(
                    $"/friends/requests/{result.FriendRequest!.Id}", ToResponse(result.FriendRequest)),
                SendFriendRequestOutcome.SelfRequest => Results.Problem(
                    title: "Cannot friend yourself",
                    detail: "You cannot send a friend request to your own account.",
                    statusCode: StatusCodes.Status400BadRequest),
                SendFriendRequestOutcome.RecipientNotFound => Results.Problem(
                    title: "Recipient not found",
                    detail: $"No user found with id '{request.RecipientUserId}'.",
                    statusCode: StatusCodes.Status404NotFound),
                SendFriendRequestOutcome.AlreadyFriends => Results.Problem(
                    title: "Already friends",
                    detail: "You are already friends with this user.",
                    statusCode: StatusCodes.Status409Conflict),
                SendFriendRequestOutcome.DuplicatePending => Results.Problem(
                    title: "Duplicate pending request",
                    detail: "A pending friend request already exists between you and this user.",
                    statusCode: StatusCodes.Status409Conflict),
                _ => throw new InvalidOperationException($"Unhandled SendFriendRequestOutcome '{result.Outcome}'."),
            };
        }).RequireAuthorization();

        // REQ-1401: the recipient accepts — resolves the request as
        // Accepted and creates the symmetric Friendship row in the same
        // call (IFriendService.AcceptFriendRequestAsync).
        app.MapPost("/friends/requests/{id:guid}/accept", async (
            Guid id,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IFriendService friendService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await friendService.AcceptFriendRequestAsync(id, requestingUser.Id, cancellationToken);

            return ToResult(result);
        }).RequireAuthorization();

        // REQ-1401: the recipient declines — resolves the request as
        // Declined, no Friendship row is created, and the requester remains
        // free to send a new request later ("declining is not a permanent
        // block").
        app.MapPost("/friends/requests/{id:guid}/decline", async (
            Guid id,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IFriendService friendService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await friendService.DeclineFriendRequestAsync(id, requestingUser.Id, cancellationToken);

            return ToResult(result);
        }).RequireAuthorization();

        // REQ-1401: every request currently Pending where the caller is the
        // recipient — lets a player see who's asked to friend them, and is
        // what "no longer appears in either player's pending list" (post
        // accept/decline) is verified against.
        app.MapGet("/friends/requests/pending", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IFriendService friendService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var pending = await friendService.GetPendingFriendRequestsAsync(requestingUser.Id, cancellationToken);

            return Results.Ok(pending.Select(ToResponse).ToList());
        }).RequireAuthorization();

        // REQ-1401's accepted outcome: every current friendship of the
        // caller's, shown as the other user's id (Friendship.UserAId/
        // UserBId is a normalized pair, not "requester/recipient" — see
        // Friendship's own doc comment).
        app.MapGet("/friends", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IFriendService friendService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var friendships = await friendService.GetFriendshipsAsync(requestingUser.Id, cancellationToken);

            return Results.Ok(friendships.Select(f => ToResponse(f, requestingUser.Id)).ToList());
        }).RequireAuthorization();
    }

    private static IResult ToResult(ResolveFriendRequestResult result) => result.Outcome switch
    {
        ResolveFriendRequestOutcome.Resolved => Results.Ok(ToResponse(result.FriendRequest!)),
        ResolveFriendRequestOutcome.NotFound => Results.Problem(
            title: "Friend request not found",
            detail: "No friend request found with that id.",
            statusCode: StatusCodes.Status404NotFound),
        ResolveFriendRequestOutcome.NotYourRequest => Results.Problem(
            title: "Not your request",
            detail: "Only the recipient of a friend request can accept or decline it.",
            statusCode: StatusCodes.Status403Forbidden),
        ResolveFriendRequestOutcome.AlreadyResolved => Results.Problem(
            title: "Already resolved",
            detail: "This friend request has already been accepted or declined.",
            statusCode: StatusCodes.Status409Conflict),
        _ => throw new InvalidOperationException($"Unhandled ResolveFriendRequestOutcome '{result.Outcome}'."),
    };

    private static FriendRequestResponse ToResponse(FriendRequest friendRequest) =>
        new(
            friendRequest.Id,
            friendRequest.RequesterUserId,
            friendRequest.RecipientUserId,
            friendRequest.Status.ToString(),
            friendRequest.CreatedAt,
            friendRequest.ResolvedAt);

    private static FriendshipResponse ToResponse(Friendship friendship, Guid requestingUserId) =>
        new(
            friendship.Id,
            friendship.UserAId == requestingUserId ? friendship.UserBId : friendship.UserAId,
            friendship.CreatedAt);
}

public record SendFriendRequestRequest(Guid RecipientUserId);

public record FriendRequestResponse(
    Guid Id, Guid RequesterUserId, Guid RecipientUserId, string Status, DateTime CreatedAt, DateTime? ResolvedAt);

// FriendUserId is always the *other* user relative to the caller — never
// UserAId/UserBId directly, since that normalized order has no meaning to
// a client (see Friendship's own doc comment).
public record FriendshipResponse(Guid Id, Guid FriendUserId, DateTime CreatedAt);

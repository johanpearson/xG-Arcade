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
                // The caller is always the requester here, so their own
                // DisplayName is already in hand — only the recipient's
                // needs a lookup (SCREEN-15 "Identity gap" fix, REQ-1401).
                SendFriendRequestOutcome.Sent => Results.Created(
                    $"/friends/requests/{result.FriendRequest!.Id}",
                    ToResponse(
                        result.FriendRequest,
                        requestingUser.DisplayName,
                        await ResolveDisplayNameAsync(userRepository, result.FriendRequest.RecipientUserId, cancellationToken))),
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

            return await ToResultAsync(result, requestingUser, userRepository, cancellationToken);
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

            return await ToResultAsync(result, requestingUser, userRepository, cancellationToken);
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

            // The caller is always the recipient of every row here (that's
            // this query's own filter), so only the varying requester ids
            // need a batch lookup — one query for the whole page, never one
            // per row (SCREEN-15 "Identity gap" fix, REQ-1401).
            var requesterDisplayNamesById = await ResolveDisplayNamesAsync(
                userRepository, pending.Select(r => r.RequesterUserId), cancellationToken);

            return Results.Ok(pending
                .Select(r => ToResponse(r, GetDisplayName(requesterDisplayNamesById, r.RequesterUserId), requestingUser.DisplayName))
                .ToList());
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

            // Every row's "other" user varies, so batch-resolve every
            // distinct friend id in one query rather than one round-trip
            // per row (SCREEN-15 "Identity gap" fix, REQ-1401).
            var friendIds = friendships.Select(f => f.UserAId == requestingUser.Id ? f.UserBId : f.UserAId);
            var friendDisplayNamesById = await ResolveDisplayNamesAsync(userRepository, friendIds, cancellationToken);

            return Results.Ok(friendships
                .Select(f => ToResponse(f, requestingUser.Id, friendDisplayNamesById))
                .ToList());
        }).RequireAuthorization();
    }

    private static async Task<IResult> ToResultAsync(
        ResolveFriendRequestResult result,
        User requestingUser,
        IUserRepository userRepository,
        CancellationToken cancellationToken)
    {
        if (result.Outcome != ResolveFriendRequestOutcome.Resolved)
        {
            return result.Outcome switch
            {
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
        }

        var friendRequest = result.FriendRequest!;
        // ResolveFriendRequestOutcome.NotYourRequest above already guarantees
        // requestingUser is the recipient here — only the requester's
        // DisplayName needs a lookup.
        var requesterDisplayName = await ResolveDisplayNameAsync(userRepository, friendRequest.RequesterUserId, cancellationToken);

        return Results.Ok(ToResponse(friendRequest, requesterDisplayName, requestingUser.DisplayName));
    }

    private static FriendRequestResponse ToResponse(FriendRequest friendRequest, string requesterDisplayName, string recipientDisplayName) =>
        new(
            friendRequest.Id,
            friendRequest.RequesterUserId,
            friendRequest.RecipientUserId,
            friendRequest.Status.ToString(),
            friendRequest.CreatedAt,
            friendRequest.ResolvedAt,
            requesterDisplayName,
            recipientDisplayName);

    private static FriendshipResponse ToResponse(Friendship friendship, Guid requestingUserId, IReadOnlyDictionary<Guid, string> friendDisplayNamesById)
    {
        var friendUserId = friendship.UserAId == requestingUserId ? friendship.UserBId : friendship.UserAId;
        return new(
            friendship.Id,
            friendUserId,
            friendship.CreatedAt,
            GetDisplayName(friendDisplayNamesById, friendUserId));
    }

    // SCREEN-15 "Identity gap" fix (REQ-1401/1402): resolves every distinct
    // user id's DisplayName in one IUserRepository.GetByIdsAsync call rather
    // than one round-trip per row — same batch-then-map shape
    // LeaderboardService already established around this same repository
    // method. Shared with ChallengeEndpoints via internal visibility.
    internal static async Task<IReadOnlyDictionary<Guid, string>> ResolveDisplayNamesAsync(
        IUserRepository userRepository, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var distinctIds = userIds.Distinct().ToList();
        if (distinctIds.Count == 0)
            return new Dictionary<Guid, string>();

        var users = await userRepository.GetByIdsAsync(distinctIds, cancellationToken);
        return users.ToDictionary(u => u.Id, u => u.DisplayName);
    }

    // Single-id convenience wrapper over ResolveDisplayNamesAsync, for
    // handlers that only ever need one other party's name (send/accept/
    // decline) rather than a whole page's worth.
    internal static async Task<string> ResolveDisplayNameAsync(
        IUserRepository userRepository, Guid userId, CancellationToken cancellationToken)
    {
        var displayNamesById = await ResolveDisplayNamesAsync(userRepository, new[] { userId }, cancellationToken);
        return GetDisplayName(displayNamesById, userId);
    }

    // Defensive fallback only — every FriendRequest/Friendship/Challenge row
    // is created between two users that existed at creation time, so this
    // should never actually be hit today (no code path hard-deletes a User
    // row referenced by one of these; see REQ-710's Guess-anonymization
    // carve-out for the analogous concern elsewhere).
    internal const string UnknownDisplayName = "Unknown Player";

    internal static string GetDisplayName(IReadOnlyDictionary<Guid, string> displayNamesById, Guid userId) =>
        displayNamesById.TryGetValue(userId, out var displayName) ? displayName : UnknownDisplayName;
}

public record SendFriendRequestRequest(Guid RecipientUserId);

public record FriendRequestResponse(
    Guid Id,
    Guid RequesterUserId,
    Guid RecipientUserId,
    string Status,
    DateTime CreatedAt,
    DateTime? ResolvedAt,
    string RequesterDisplayName,
    string RecipientDisplayName);

// FriendUserId is always the *other* user relative to the caller — never
// UserAId/UserBId directly, since that normalized order has no meaning to
// a client (see Friendship's own doc comment).
public record FriendshipResponse(Guid Id, Guid FriendUserId, DateTime CreatedAt, string FriendDisplayName);

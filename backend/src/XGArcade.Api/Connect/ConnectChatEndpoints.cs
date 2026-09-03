using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGConnect;

namespace XGArcade.Api.Connect;

// COMP-17 (Games.XGConnect)/ADR-0103, S-215: REQ-1410's in-match text chat
// send/read surface. Same thin-endpoint/owning-service pattern as
// ConnectMatchEndpoints.cs/ConnectChainStepEndpoints.cs — every precondition
// check (match exists, caller is a participant) lives in IConnectChatService
// (Games.XGConnect); this file only resolves the caller and shapes the
// response. Deliberately does NOT gate on match status — see
// IConnectChatService's own doc comment for why REQ-1410 has no such
// precondition, unlike REQ-1406/1407's MatchNotActive/AlreadyForfeited.
public static class ConnectChatEndpoints
{
    public static void MapConnectChatEndpoints(this WebApplication app)
    {
        app.MapPost("/matches/{matchId:guid}/chat-messages", async (
            Guid matchId,
            SendChatMessageRequest request,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IConnectChatService connectChatService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await connectChatService.SendMessageAsync(
                matchId, requestingUser.Id, request.MessageText, cancellationToken);

            return result.Outcome switch
            {
                ConnectChatOutcome.Success => Results.Ok(ToResponse(result.Message!)),
                ConnectChatOutcome.MatchNotFound => Results.NotFound(),
                ConnectChatOutcome.NotAParticipant => Results.Problem(
                    title: "Not a participant",
                    detail: "Only the two players in this match may send messages in its chat.",
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
            };
        }).RequireAuthorization();

        app.MapGet("/matches/{matchId:guid}/chat-messages", async (
            Guid matchId,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IConnectChatService connectChatService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await connectChatService.GetMessagesAsync(matchId, requestingUser.Id, cancellationToken);

            return result.Outcome switch
            {
                ConnectChatOutcome.Success => Results.Ok(result.Messages!.Select(ToResponse).ToList()),
                ConnectChatOutcome.MatchNotFound => Results.NotFound(),
                ConnectChatOutcome.NotAParticipant => Results.Problem(
                    title: "Not a participant",
                    detail: "Only the two players in this match may view its chat.",
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
            };
        }).RequireAuthorization();
    }

    private static ChatMessageResponse ToResponse(ConnectChatMessage message) =>
        new(message.Id, message.SenderUserId, message.MessageText, message.SentAt);
}

public record SendChatMessageRequest(string MessageText);

// SenderUserId is nullable in the response, mirroring the entity — it goes
// null only once REQ-710 anonymization has run for that sender, same
// nullable-in-place shape as ConnectMatch.PlayerAUserId/PlayerBUserId.
public record ChatMessageResponse(Guid Id, Guid? SenderUserId, string MessageText, DateTime SentAt);

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
    // Quality-gate finding on REQ-1410 (S-215): no other free-text
    // user-input endpoint in this codebase (GuessEndpoints.SubmittedName,
    // AdminAnnouncementBannerEndpoints.Message, LeagueEndpoints.name,
    // IncidentEndpoints' Title/Description/Screen) persists a request field
    // without a blank/max-length check — this one shouldn't be the
    // exception just because REQ-1410's own Given/When/Then text doesn't
    // spell it out. 1000 chars, not AdminAnnouncementBannerEndpoints'
    // 500 — a chat message is casual back-and-forth, not a single
    // site-wide notice, so a somewhat higher ceiling avoids truncating a
    // legitimate longer message while still ruling out unbounded/abusive
    // input. No product-specified number exists for this; picked by
    // judgment, same as AdminAnnouncementBannerEndpoints' own 500 was.
    private const int MaxMessageLength = 1000;

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
            // Validated here, before touching the user repository or
            // ConnectChatService, same "reject before any read/write"
            // ordering GuessEndpoints/AdminAnnouncementBannerEndpoints use
            // for their own free-text fields — a malformed message never
            // reaches IConnectChatService, so ConnectChatOutcome doesn't
            // need a new case for this.
            if (string.IsNullOrWhiteSpace(request.MessageText))
            {
                return Results.Problem(
                    title: "A message is required",
                    detail: "messageText must not be empty.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var trimmedMessageText = request.MessageText.Trim();
            if (trimmedMessageText.Length > MaxMessageLength)
            {
                return Results.Problem(
                    title: "Message is too long",
                    detail: $"messageText must be {MaxMessageLength} characters or fewer.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var result = await connectChatService.SendMessageAsync(
                matchId, requestingUser.Id, trimmedMessageText, cancellationToken);

            if (result.Outcome != ConnectChatOutcome.Success)
            {
                return result.Outcome switch
                {
                    ConnectChatOutcome.MatchNotFound => Results.NotFound(),
                    ConnectChatOutcome.NotAParticipant => Results.Problem(
                        title: "Not a participant",
                        detail: "Only the two players in this match may send messages in its chat.",
                        statusCode: StatusCodes.Status403Forbidden),
                    _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
                };
            }

            // SCREEN-15 "Identity gap" fix: the caller is always the sender
            // here, so their own DisplayName is already in hand — no lookup
            // needed for this one message's own response.
            return Results.Ok(ToResponse(result.Message!, requestingUser.DisplayName));
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

            if (result.Outcome != ConnectChatOutcome.Success)
            {
                return result.Outcome switch
                {
                    ConnectChatOutcome.MatchNotFound => Results.NotFound(),
                    ConnectChatOutcome.NotAParticipant => Results.Problem(
                        title: "Not a participant",
                        detail: "Only the two players in this match may view its chat.",
                        statusCode: StatusCodes.Status403Forbidden),
                    _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
                };
            }

            // SCREEN-15 "Identity gap" fix: every distinct sender id across
            // the whole page, resolved in one IUserRepository.GetByIdsAsync
            // call rather than one round-trip per row — same
            // batch-then-map shape FriendEndpoints/ChallengeEndpoints
            // (Core.Social) already established for this exact repository
            // method. Null sender ids (REQ-710 anonymization) are excluded
            // before the call — GetByIdsAsync only accepts non-null ids.
            var messages = result.Messages!;
            var senderIds = messages
                .Where(m => m.SenderUserId.HasValue)
                .Select(m => m.SenderUserId!.Value)
                .Distinct()
                .ToList();
            var senderDisplayNamesById = senderIds.Count == 0
                ? new Dictionary<Guid, string>()
                : (await userRepository.GetByIdsAsync(senderIds, cancellationToken)).ToDictionary(u => u.Id, u => u.DisplayName);

            return Results.Ok(messages.Select(m => ToResponse(m, ResolveSenderDisplayName(m.SenderUserId, senderDisplayNamesById))).ToList());
        }).RequireAuthorization();
    }

    // SenderDisplayName must mirror SenderUserId's own nullability exactly —
    // null whenever SenderUserId is null (REQ-710 anonymization), never a
    // placeholder for that case. A non-null SenderUserId with no matching row
    // is defensive-only — every ConnectChatMessage row is created by a user
    // that existed at send time, and REQ-710 anonymizes the SenderUserId
    // column itself rather than deleting the User row it once pointed to.
    private static string? ResolveSenderDisplayName(Guid? senderUserId, IReadOnlyDictionary<Guid, string> senderDisplayNamesById) =>
        senderUserId is null
            ? null
            : senderDisplayNamesById.TryGetValue(senderUserId.Value, out var displayName) ? displayName : "Unknown player";

    private static ChatMessageResponse ToResponse(ConnectChatMessage message, string? senderDisplayName) =>
        new(message.Id, message.SenderUserId, senderDisplayName, message.MessageText, message.SentAt);
}

public record SendChatMessageRequest(string MessageText);

// SenderUserId is nullable in the response, mirroring the entity — it goes
// null only once REQ-710 anonymization has run for that sender, same
// nullable-in-place shape as ConnectMatch.PlayerAUserId/PlayerBUserId.
// SenderDisplayName mirrors SenderUserId's own nullability exactly — null
// whenever SenderUserId is null, never a placeholder — resolved via a
// single batch IUserRepository.GetByIdsAsync call across every row rather
// than one lookup per row (SCREEN-15 "Identity gap" fix already applied to
// Core.Social's FriendshipResponse/ChallengeResponse; same batch-then-map
// shape LeaderboardService established for REQ-404).
public record ChatMessageResponse(Guid Id, Guid? SenderUserId, string? SenderDisplayName, string MessageText, DateTime SentAt);

using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect;

// S-215/REQ-1410: see IConnectChatService's own doc comment for why this
// deliberately does not gate on ConnectMatch.Status.
public class ConnectChatService(
    IConnectMatchRepository connectMatchRepository,
    IConnectChatMessageRepository connectChatMessageRepository,
    TimeProvider timeProvider) : IConnectChatService
{
    public async Task<SendChatMessageResult> SendMessageAsync(
        Guid matchId, Guid userId, string messageText, CancellationToken cancellationToken = default)
    {
        var access = await connectMatchRepository.ResolveParticipantMatchAsync(matchId, userId, cancellationToken);
        if (access.Outcome == ConnectMatchAccessOutcome.MatchNotFound)
            return new SendChatMessageResult(ConnectChatOutcome.MatchNotFound, null);
        if (access.Outcome == ConnectMatchAccessOutcome.NotAParticipant)
            return new SendChatMessageResult(ConnectChatOutcome.NotAParticipant, null);

        var message = new ConnectChatMessage
        {
            Id = Guid.NewGuid(),
            ConnectMatchId = matchId,
            SenderUserId = userId,
            MessageText = messageText,
            SentAt = timeProvider.GetUtcNow().UtcDateTime,
        };
        var persisted = await connectChatMessageRepository.AddMessageAsync(message, cancellationToken);

        return new SendChatMessageResult(ConnectChatOutcome.Success, persisted);
    }

    public async Task<GetChatMessagesResult> GetMessagesAsync(
        Guid matchId, Guid userId, CancellationToken cancellationToken = default)
    {
        var access = await connectMatchRepository.ResolveParticipantMatchAsync(matchId, userId, cancellationToken);
        if (access.Outcome == ConnectMatchAccessOutcome.MatchNotFound)
            return new GetChatMessagesResult(ConnectChatOutcome.MatchNotFound, null);
        if (access.Outcome == ConnectMatchAccessOutcome.NotAParticipant)
            return new GetChatMessagesResult(ConnectChatOutcome.NotAParticipant, null);

        var messages = await connectChatMessageRepository.GetMessagesForMatchAsync(matchId, cancellationToken);
        return new GetChatMessagesResult(ConnectChatOutcome.Success, messages);
    }
}

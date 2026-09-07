using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect;

// S-215/REQ-1410: see IConnectChatService's own doc comment for why this
// deliberately does not gate on ConnectMatch.Status, and REQ-1419's one
// narrow exception to that on the send path only.
public class ConnectChatService(
    IConnectMatchRepository connectMatchRepository,
    IConnectChatMessageRepository connectChatMessageRepository,
    TimeProvider timeProvider) : IConnectChatService
{
    // REQ-1419: how long after ConnectMatch.ResolvedAt a match's chat still
    // accepts new sends. Uses the same injected TimeProvider every other
    // xG Connect service already uses for "now" (e.g.
    // ConnectMatchLifecycleService's deadline/timeout checks,
    // ConnectChainStepDisputeService's own deadline check) rather than
    // calling DateTime.UtcNow directly.
    private static readonly TimeSpan ChatCloseWindow = TimeSpan.FromHours(1);

    public async Task<SendChatMessageResult> SendMessageAsync(
        Guid matchId, Guid userId, string messageText, CancellationToken cancellationToken = default)
    {
        var access = await connectMatchRepository.ResolveParticipantMatchAsync(matchId, userId, cancellationToken);
        if (access.Outcome == ConnectMatchAccessOutcome.MatchNotFound)
            return new SendChatMessageResult(ConnectChatOutcome.MatchNotFound, null);
        if (access.Outcome == ConnectMatchAccessOutcome.NotAParticipant)
            return new SendChatMessageResult(ConnectChatOutcome.NotAParticipant, null);
        var match = access.Match!;

        // REQ-1419: send-only cutoff, anchored to ResolvedAt — a match that
        // has never resolved (ResolvedAt null) is never subject to it, and
        // GetMessagesAsync below has no equivalent check (read path is
        // completely unaffected, per REQ-1410).
        if (match.Status == ConnectMatchStatus.Resolved && match.ResolvedAt is not null
            && timeProvider.GetUtcNow().UtcDateTime - match.ResolvedAt.Value > ChatCloseWindow)
        {
            return new SendChatMessageResult(ConnectChatOutcome.ChatClosed, null);
        }

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

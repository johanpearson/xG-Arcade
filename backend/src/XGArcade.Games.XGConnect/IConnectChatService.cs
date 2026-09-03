using XGArcade.Data.Entities;

namespace XGArcade.Games.XGConnect;

// S-215/REQ-1410 (docs/requirements-document.md §4.15): match-scoped chat
// send/read, layered on top of IConnectMatchRepository (participant check
// only) and IConnectChatMessageRepository (S-208 persistence) — same
// "outcome enum + result record for expected branches" shape as
// IConnectChainStepService/IConnectTargetPickService.
//
// Deliberately does NOT check ConnectMatch.Status, unlike
// ConnectChainStepService (which rejects MatchNotActive/AlreadyForfeited).
// REQ-1410's three Given/When/Then blocks never mention match status as a
// precondition for sending OR reading, and one of them is explicit that
// chat "remains visible/readable" once a match has reached a terminal
// state for both players — only match-exists and participant-only checks
// apply, for both SendMessageAsync and GetMessagesAsync.
public interface IConnectChatService
{
    Task<SendChatMessageResult> SendMessageAsync(
        Guid matchId, Guid userId, string messageText, CancellationToken cancellationToken = default);

    Task<GetChatMessagesResult> GetMessagesAsync(
        Guid matchId, Guid userId, CancellationToken cancellationToken = default);
}

public enum ConnectChatOutcome
{
    Success,

    MatchNotFound,

    // The caller is neither PlayerAUserId nor PlayerBUserId on this match —
    // same check/ordering as SubmitChainStepOutcome.NotAParticipant.
    NotAParticipant,
}

// Message is non-null only for Success.
public record SendChatMessageResult(ConnectChatOutcome Outcome, ConnectChatMessage? Message);

// Messages is non-null (though possibly empty) only for Success.
public record GetChatMessagesResult(ConnectChatOutcome Outcome, IReadOnlyList<ConnectChatMessage>? Messages);

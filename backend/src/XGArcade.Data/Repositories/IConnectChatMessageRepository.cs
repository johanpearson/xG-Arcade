using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// Games.XGConnect's (COMP-17) own persistence for REQ-1410 — the only path
// Games.XGConnect reaches ConnectChatMessage through. Kept separate from
// IConnectMatchRepository (rather than folded in) since chat is an
// independent read/write concern from the match/target-pick/chain-step
// family that repository owns — mirrors, e.g., IPlayerSuggestionRepository
// being its own repository rather than folded into a COMP-06 repository.
// See ADR-0103.
public interface IConnectChatMessageRepository
{
    Task<ConnectChatMessage> AddMessageAsync(ConnectChatMessage message, CancellationToken cancellationToken = default);

    // Ordered by SentAt — REQ-1410's only read shape, a chronological
    // per-match read.
    Task<IReadOnlyList<ConnectChatMessage>> GetMessagesForMatchAsync(Guid matchId, CancellationToken cancellationToken = default);
}

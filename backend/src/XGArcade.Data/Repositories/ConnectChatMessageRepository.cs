using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class ConnectChatMessageRepository(XGArcadeDbContext dbContext) : IConnectChatMessageRepository
{
    public async Task<ConnectChatMessage> AddMessageAsync(ConnectChatMessage message, CancellationToken cancellationToken = default)
    {
        dbContext.ConnectChatMessages.Add(message);
        await dbContext.SaveChangesAsync(cancellationToken);
        return message;
    }

    public async Task<IReadOnlyList<ConnectChatMessage>> GetMessagesForMatchAsync(
        Guid matchId, CancellationToken cancellationToken = default) =>
        await dbContext.ConnectChatMessages
            .AsNoTracking()
            .Where(m => m.ConnectMatchId == matchId)
            .OrderBy(m => m.SentAt)
            .ToListAsync(cancellationToken);

    // REQ-710/ADR-0101/S-215: load-then-save (coding-guidelines.md — never
    // ExecuteUpdateAsync, the InMemory test provider can't translate it),
    // tracked (not AsNoTracking) since every row here is mutated in place —
    // mirrors ConnectMatchRepository.AnonymizeUserDataAsync's own
    // per-entity-type loop shape.
    public async Task AnonymizeSenderAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var messages = await dbContext.ConnectChatMessages
            .Where(m => m.SenderUserId == userId)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
            message.SenderUserId = null;

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

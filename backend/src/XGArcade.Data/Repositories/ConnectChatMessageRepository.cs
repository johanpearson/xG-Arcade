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
}

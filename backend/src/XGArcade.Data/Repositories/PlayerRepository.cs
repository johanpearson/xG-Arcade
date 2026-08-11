using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerRepository(XGArcadeDbContext dbContext) : IPlayerRepository
{
    public async Task<Player?> GetPlayerByWikidataQidAsync(string wikidataQid, CancellationToken cancellationToken = default) =>
        await dbContext.Players.AsNoTracking().FirstOrDefaultAsync(p => p.WikidataQid == wikidataQid, cancellationToken);

    public async Task<Player?> GetPlayerByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, Player>> GetPlayersByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return new Dictionary<Guid, Player>();

        return await dbContext.Players
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
    }

    public async Task<Player> AddPlayerAsync(Player player, CancellationToken cancellationToken = default)
    {
        dbContext.Players.Add(player);
        await dbContext.SaveChangesAsync(cancellationToken);
        return player;
    }

    public async Task<IReadOnlyDictionary<string, Player>> GetOrCreatePlayersByWikidataQidAsync(
        IReadOnlyList<PlayerCreationRequest> requests, CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0)
            return new Dictionary<string, Player>();

        var qids = requests.Select(r => r.WikidataQid).ToList();
        var existingByQid = await dbContext.Players
            .AsNoTracking()
            .Where(p => p.WikidataQid != null && qids.Contains(p.WikidataQid))
            .ToDictionaryAsync(p => p.WikidataQid!, cancellationToken);

        var result = new Dictionary<string, Player>(existingByQid);

        foreach (var request in requests)
        {
            if (result.ContainsKey(request.WikidataQid))
                continue;

            var player = new Player
            {
                Id = Guid.NewGuid(),
                FullName = request.FullName,
                WikidataQid = request.WikidataQid,
                PhotoUrl = request.PhotoUrl,
                // REQ-1207/S-082: set only at creation, same as PhotoUrl —
                // this method never touches an existing Player row (the
                // `if (result.ContainsKey(...)) continue;` above skips it),
                // so "set once, never overwritten" is already this method's
                // behavior for free.
                Position = request.Position,
                BirthYear = request.BirthYear,
            };
            dbContext.Players.Add(player);
            result[request.WikidataQid] = player;
        }

        // One SaveChangesAsync call for the whole batch — load-then-
        // SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteUpdateAsync (the InMemory test provider can't translate it).
        await dbContext.SaveChangesAsync(cancellationToken);

        return result;
    }

    public async Task<IReadOnlyList<Player>> GetPlayersByNormalizedFullNameAsync(
        string normalizedFullName, CancellationToken cancellationToken = default) =>
        await dbContext.Players
            .AsNoTracking()
            .Where(p => p.NormalizedFullName == normalizedFullName)
            .ToListAsync(cancellationToken);
}

using Microsoft.EntityFrameworkCore;
using Npgsql;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerRepository(XGArcadeDbContext dbContext) : IPlayerRepository
{
    // Matches XGArcadeDbContext's EF-generated index name for the filtered
    // unique index on Player.WikidataQid ("IX_<Table>_<Column>") — same
    // naming convention UserRepository.DisplayNameUniqueIndexName/
    // LeagueRepository.InviteCodeUniqueIndexName rely on.
    private const string WikidataQidUniqueIndexName = "IX_Players_WikidataQid";

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

    public async Task<IReadOnlyDictionary<string, PlayerCreationResult>> GetOrCreatePlayersByWikidataQidAsync(
        IReadOnlyList<PlayerCreationRequest> requests, CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0)
            return new Dictionary<string, PlayerCreationResult>();

        var qids = requests.Select(r => r.WikidataQid).ToList();
        var existingByQid = await dbContext.Players
            .AsNoTracking()
            .Where(p => p.WikidataQid != null && qids.Contains(p.WikidataQid))
            .ToDictionaryAsync(p => p.WikidataQid!, cancellationToken);

        var result = new Dictionary<string, PlayerCreationResult>();
        foreach (var (qid, existingPlayer) in existingByQid)
            result[qid] = new PlayerCreationResult(existingPlayer, WasCreated: false);

        var newPlayers = new List<Player>();
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
            newPlayers.Add(player);
            result[request.WikidataQid] = new PlayerCreationResult(player, WasCreated: true);
        }

        if (newPlayers.Count == 0)
            return result;

        // One SaveChangesAsync call for the whole batch — load-then-
        // SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteUpdateAsync (the InMemory test provider can't translate it).
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: WikidataQidUniqueIndexName })
        {
            // S-129: a concurrent caller (REQ-211's guess-time fallback,
            // PlayerCareerPrefetchService's own sweep, or a second admin
            // commit) won the race for one or more WikidataQids in this
            // batch — same "detach loser, re-fetch winner" precedent as
            // LeagueRepository.GetOrCreateGlobalLeagueAsync/
            // PathInstanceRepository.GetOrCreateCycleStateAsync, extended to
            // a batch: SaveChangesAsync failed atomically (none of
            // newPlayers were persisted), so detach every pending insert,
            // re-query which of THIS BATCH's QIDs now exist, and retry only
            // the ones that are still genuinely missing.
            foreach (var loser in newPlayers)
                dbContext.Entry(loser).State = EntityState.Detached;

            var newQids = newPlayers.Select(p => p.WikidataQid!).ToList();
            var wonByOthersByQid = await dbContext.Players
                .AsNoTracking()
                .Where(p => p.WikidataQid != null && newQids.Contains(p.WikidataQid))
                .ToDictionaryAsync(p => p.WikidataQid!, cancellationToken);

            var stillMissing = new List<Player>();
            foreach (var loser in newPlayers)
            {
                if (wonByOthersByQid.TryGetValue(loser.WikidataQid!, out var winner))
                {
                    result[loser.WikidataQid!] = new PlayerCreationResult(winner, WasCreated: false);
                }
                else
                {
                    stillMissing.Add(loser);
                    dbContext.Players.Add(loser);
                }
            }

            // Only re-saves if at least one QID in this batch is still
            // genuinely missing after re-fetching every other caller's
            // winner above — if every collision in the batch was covered by
            // an already-committed concurrent insert, there's nothing left
            // to retry.
            //
            // Single retry, not a loop: this second SaveChangesAsync is
            // itself uncaught, so a THIRD concurrent writer colliding on the
            // same WikidataQid within this narrow retry window would still
            // throw a raw DbUpdateException. Judged acceptable — closing
            // that would need an arbitrarily-bounded retry loop for a
            // vanishingly rare double-collision — but don't assume this
            // catch closes the race under arbitrary concurrency.
            if (stillMissing.Count > 0)
                await dbContext.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    public async Task<IReadOnlyList<Player>> GetPlayersByNormalizedFullNameAsync(
        string normalizedFullName, CancellationToken cancellationToken = default) =>
        await dbContext.Players
            .AsNoTracking()
            .Where(p => p.NormalizedFullName == normalizedFullName)
            .ToListAsync(cancellationToken);
}

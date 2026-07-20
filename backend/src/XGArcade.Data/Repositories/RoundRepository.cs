using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class RoundRepository(XGArcadeDbContext dbContext) : IRoundRepository
{
    public async Task<Round?> GetLatestByGameKeyAsync(string gameKey, CancellationToken cancellationToken = default) =>
        await dbContext.Rounds
            .AsNoTracking()
            .Where(r => r.GameKey == gameKey)
            .OrderByDescending(r => r.EndTime)
            .FirstOrDefaultAsync(cancellationToken);

    // REQ-303: RoundStatusExtensions.GetStatus's own definition of "active"
    // (StartTime <= now <= EndTime), applied in the query rather than
    // fetched-then-filtered in memory. REQ-301's one-round-ahead scheduling
    // means there's normally only ever one Active round per GameKey, but
    // this orders by StartTime descending regardless — deterministic (not
    // whatever order Postgres happens to return) if leftover data ever
    // produces more than one, which is more likely in a test/dev database
    // than in steady-state production use.
    public async Task<Round?> GetActiveByGameKeyAsync(string gameKey, DateTime now, CancellationToken cancellationToken = default) =>
        await dbContext.Rounds
            .AsNoTracking()
            .Where(r => r.GameKey == gameKey && r.StartTime <= now && r.EndTime >= now)
            .OrderByDescending(r => r.StartTime)
            .FirstOrDefaultAsync(cancellationToken);

    // REQ-408: only ever ClosedAt != null rows, most recently closed first.
    public async Task<IReadOnlyList<Round>> GetClosedByGameKeyAsync(string gameKey, int skip, int take, CancellationToken cancellationToken = default) =>
        await dbContext.Rounds
            .AsNoTracking()
            .Where(r => r.GameKey == gameKey && r.ClosedAt != null)
            .OrderByDescending(r => r.ClosedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

    // REQ-405: locked-only (ClosedAt != null), half-open [windowStartUtc,
    // windowEndUtc) range on EndTime — the same (GameKey, EndTime) composite
    // index added for REQ-408's GetClosedByGameKeyAsync above already covers
    // this filter shape (a range on EndTime scoped to GameKey), so this
    // method deliberately does not add a new index/migration; see
    // XGArcadeDbContext.OnModelCreating's Round.HasIndex comment.
    public async Task<IReadOnlyList<Guid>> GetClosedIdsWithinWindowAsync(
        string gameKey, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken cancellationToken = default) =>
        await dbContext.Rounds
            .AsNoTracking()
            .Where(r => r.GameKey == gameKey && r.ClosedAt != null && r.EndTime >= windowStartUtc && r.EndTime < windowEndUtc)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

    public async Task<Round?> GetPreviousByGameKeyAsync(string gameKey, DateTime beforeStartTime, CancellationToken cancellationToken = default) =>
        await dbContext.Rounds
            .AsNoTracking()
            .Where(r => r.GameKey == gameKey && r.StartTime < beforeStartTime)
            .OrderByDescending(r => r.StartTime)
            .FirstOrDefaultAsync(cancellationToken);

    // Not AsNoTracking: RoundCloseService reads a Round, mutates it, and
    // saves it back via UpdateAsync in the same request/scope — matching
    // this repository's own read-then-write flow, unlike every other
    // repository in this codebase so far, which is add-only.
    public async Task<Round?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.Rounds.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<Round> AddAsync(Round round, CancellationToken cancellationToken = default)
    {
        dbContext.Rounds.Add(round);
        await dbContext.SaveChangesAsync(cancellationToken);
        return round;
    }

    public async Task UpdateAsync(Round round, CancellationToken cancellationToken = default)
    {
        dbContext.Rounds.Update(round);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

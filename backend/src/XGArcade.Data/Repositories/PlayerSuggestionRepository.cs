using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerSuggestionRepository(XGArcadeDbContext dbContext) : IPlayerSuggestionRepository
{
    // load-then-SaveChangesAsync isn't relevant here (this is a pure insert,
    // not a load-then-mutate write) — one SaveChangesAsync call persists the
    // PlayerSuggestion row and its owned AssertedClubs collection together
    // (EF Core's change tracker cascades the insert for a navigated
    // collection set before this call), matching the "one SaveChangesAsync
    // per logical write" discipline the rest of this project's repositories
    // already follow.
    public async Task<PlayerSuggestion> AddAsync(PlayerSuggestion suggestion, CancellationToken cancellationToken = default)
    {
        dbContext.PlayerSuggestions.Add(suggestion);
        await dbContext.SaveChangesAsync(cancellationToken);
        return suggestion;
    }

    public async Task<IReadOnlyList<PlayerSuggestion>> GetPendingAsync(CancellationToken cancellationToken = default) =>
        await dbContext.PlayerSuggestions
            .AsNoTracking()
            .Include(s => s.AssertedClubs)
            .Where(s => s.Status == PlayerSuggestionStatus.Pending)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<PlayerSuggestion?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.PlayerSuggestions
            .AsNoTracking()
            .Include(s => s.AssertedClubs)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<bool> ResolveAsync(
        Guid id, PlayerSuggestionStatus status, Guid adminId, DateTime resolvedAt, CancellationToken cancellationToken = default)
    {
        var suggestion = await dbContext.PlayerSuggestions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (suggestion is null || suggestion.Status != PlayerSuggestionStatus.Pending)
            return false;

        suggestion.Status = status;
        suggestion.ResolvedByAdminId = adminId;
        suggestion.ResolvedAt = resolvedAt;

        // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteUpdateAsync — the InMemory test provider can't translate it.
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}

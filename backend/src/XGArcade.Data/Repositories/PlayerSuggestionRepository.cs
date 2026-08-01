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
}

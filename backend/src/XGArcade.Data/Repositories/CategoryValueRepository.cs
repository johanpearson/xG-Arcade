using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class CategoryValueRepository(XGArcadeDbContext dbContext) : ICategoryValueRepository
{
    public async Task<IReadOnlyList<CountryDefinition>> GetCountriesAsync(CancellationToken cancellationToken = default) =>
        await dbContext.CountryDefinitions.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ClubDefinition>> GetClubsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.ClubDefinitions.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TrophyDefinition>> GetTrophiesAsync(CancellationToken cancellationToken = default) =>
        await dbContext.TrophyDefinitions.AsNoTracking().ToListAsync(cancellationToken);

    public async Task AddCountryAsync(CountryDefinition country, CancellationToken cancellationToken = default)
    {
        dbContext.CountryDefinitions.Add(country);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddClubAsync(ClubDefinition club, CancellationToken cancellationToken = default)
    {
        dbContext.ClubDefinitions.Add(club);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddTrophyAsync(TrophyDefinition trophy, CancellationToken cancellationToken = default)
    {
        dbContext.TrophyDefinitions.Add(trophy);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

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

    // REQ-110/ADR-0078/S-160: load-then-SaveChangesAsync, not
    // ExecuteUpdateAsync — see this method's own doc comment on
    // ICategoryValueRepository for why (InMemory-provider test safety,
    // docs/coding-guidelines.md).
    public async Task UpdateCountrySweptAtAsync(Guid countryId, DateTime sweptAt, CancellationToken cancellationToken = default)
    {
        var country = await dbContext.CountryDefinitions.FirstOrDefaultAsync(c => c.Id == countryId, cancellationToken);
        if (country is null)
            return;

        country.PlayerPoolSweptAt = sweptAt;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateClubSweptAtAsync(Guid clubId, DateTime sweptAt, CancellationToken cancellationToken = default)
    {
        var club = await dbContext.ClubDefinitions.FirstOrDefaultAsync(c => c.Id == clubId, cancellationToken);
        if (club is null)
            return;

        club.PlayerPoolSweptAt = sweptAt;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

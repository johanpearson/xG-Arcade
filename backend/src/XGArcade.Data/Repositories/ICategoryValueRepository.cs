using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore): the only path to category value reference
// data. Grid generation (COMP-05) must read candidate row/column values
// through this interface, never by deriving them from PlayerAttribute — see
// ADR-0012 and REQ-109.
public interface ICategoryValueRepository
{
    Task<IReadOnlyList<CountryDefinition>> GetCountriesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClubDefinition>> GetClubsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrophyDefinition>> GetTrophiesAsync(CancellationToken cancellationToken = default);

    Task AddCountryAsync(CountryDefinition country, CancellationToken cancellationToken = default);
    Task AddClubAsync(ClubDefinition club, CancellationToken cancellationToken = default);
    Task AddTrophyAsync(TrophyDefinition trophy, CancellationToken cancellationToken = default);
}

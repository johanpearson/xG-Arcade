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

    // REQ-110/ADR-0078/S-160: PlayerCareerPrefetchService's own write path
    // for CountryDefinition/ClubDefinition.PlayerPoolSweptAt — called only
    // from the countriesProcessed++/clubsProcessed++ success path (never a
    // null-QID skip or a caught WikidataQueryException). GetCountriesAsync/
    // GetClubsAsync return AsNoTracking rows, so the caller can't just
    // mutate the entity it already has and rely on SaveChangesAsync — these
    // load-then-save internally instead (docs/coding-guidelines.md: never
    // ExecuteUpdateAsync, the InMemory test provider can't translate it). A
    // no-op if the row no longer exists (defensive; not expected in
    // practice within a single PrefetchAsync run).
    Task UpdateCountrySweptAtAsync(Guid countryId, DateTime sweptAt, CancellationToken cancellationToken = default);
    Task UpdateClubSweptAtAsync(Guid clubId, DateTime sweptAt, CancellationToken cancellationToken = default);
}

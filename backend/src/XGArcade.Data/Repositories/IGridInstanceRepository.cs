using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// Games.XGGrid's (COMP-05) own persistence — the only path Games.XGGrid
// reaches GridTemplate/GridInstance/GridCell through, same repository-per-
// component pattern as ICategoryValueRepository/IPlayerStoreRepository.
public interface IGridInstanceRepository
{
    Task<GridTemplate?> GetTemplateByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<GridTemplate?> GetTemplateBySizeAsync(int size, CancellationToken cancellationToken = default);
    Task<GridTemplate> AddTemplateAsync(GridTemplate template, CancellationToken cancellationToken = default);

    Task<GridInstance> AddInstanceAsync(GridInstance instance, CancellationToken cancellationToken = default);
    Task<GridInstance?> GetInstanceByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

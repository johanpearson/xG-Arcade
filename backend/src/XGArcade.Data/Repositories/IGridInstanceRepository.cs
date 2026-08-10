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

    // REQ-215 (S-089, architecture-review fix): GridGameModule.
    // GetCellCategoryTypesAsync's own lookup — a GridCell.Id is globally
    // unique, so this resolves a specific cell (for its RowCategoryType/
    // ColCategoryType, recorded on the PlayerSuggestion authoritatively
    // rather than trusting client-supplied values) without needing to know
    // its owning GridInstance first. Originally called directly from
    // XGArcade.Api.Suggestions.SuggestionEndpoints — moved behind the
    // IGameModule boundary (ADR-0003) post-merge; this repository method
    // itself is unchanged, only its caller.
    Task<GridCell?> GetCellByIdAsync(Guid cellId, CancellationToken cancellationToken = default);
}

using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// Games.XGPath's (COMP-11) own persistence — the only path Games.XGPath
// reaches PathTemplate/PathInstance/PathPuzzle through, same
// repository-per-component pattern as IGridInstanceRepository (COMP-05).
public interface IPathInstanceRepository
{
    Task<PathTemplate?> GetTemplateByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // S-084/REQ-1202: mirrors IGridInstanceRepository.GetTemplateBySizeAsync/
    // AddTemplateAsync's exact find-or-create-by-config-value pattern —
    // PathTemplateResolver (XGArcade.Api.Path) is the caller, same role
    // GridTemplateResolver plays for GridTemplate.
    Task<PathTemplate?> GetTemplateByPuzzleCountAsync(int puzzleCount, CancellationToken cancellationToken = default);
    Task<PathTemplate> AddTemplateAsync(PathTemplate template, CancellationToken cancellationToken = default);

    Task<PathInstance> AddInstanceAsync(PathInstance instance, CancellationToken cancellationToken = default);
    Task<PathInstance?> GetInstanceByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

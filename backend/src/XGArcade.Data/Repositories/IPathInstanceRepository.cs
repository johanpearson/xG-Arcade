using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// Games.XGPath's (COMP-11) own persistence — the only path Games.XGPath
// reaches PathTemplate/PathInstance/PathPuzzle through, same
// repository-per-component pattern as IGridInstanceRepository (COMP-05).
public interface IPathInstanceRepository
{
    Task<PathTemplate?> GetTemplateByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PathInstance> AddInstanceAsync(PathInstance instance, CancellationToken cancellationToken = default);
    Task<PathInstance?> GetInstanceByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

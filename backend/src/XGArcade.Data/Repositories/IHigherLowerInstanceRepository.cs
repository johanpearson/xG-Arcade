using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// Games.XGHigherLower's (COMP-18) own persistence — the only path
// Games.XGHigherLower reaches HigherLowerInstance/HigherLowerComparator
// through, same repository-per-component pattern as IPredictInstanceRepository
// (COMP-15)/IPathInstanceRepository (COMP-11)/IGridInstanceRepository
// (COMP-05). ADR-0110.
//
// S-224 scope only: GenerateInstanceAsync's write path and GetCellIdsAsync's
// read path. REQ-1504's guess-submission/streak-progression read/write
// methods (S-225) are not added here yet — do not guess at that shape.
public interface IHigherLowerInstanceRepository
{
    // Persists instance + comparators together, mirroring
    // IPredictInstanceRepository.AddInstanceAsync's exact shape.
    Task<HigherLowerInstance> AddInstanceAsync(HigherLowerInstance instance, CancellationToken cancellationToken = default);

    Task<HigherLowerInstance?> GetInstanceByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

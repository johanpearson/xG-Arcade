using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-03 (Core.Rounds)'s own persistence — the only path Core.Rounds
// reaches Round through, same repository-per-component pattern as
// IGridInstanceRepository/IUserRepository.
public interface IRoundRepository
{
    // REQ-301's "one round ahead" check needs the most recently scheduled
    // round for a game, regardless of whether it has started yet.
    Task<Round?> GetLatestByGameKeyAsync(string gameKey, CancellationToken cancellationToken = default);

    Task<Round?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Round> AddAsync(Round round, CancellationToken cancellationToken = default);

    Task UpdateAsync(Round round, CancellationToken cancellationToken = default);
}

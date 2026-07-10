using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-04 (Core.Scoring)'s own persistence — the only path Core.Scoring
// reaches Guess through, same repository-per-component pattern as
// IRoundRepository/IGridInstanceRepository.
public interface IGuessRepository
{
    // REQ-201: a player has at most one Guess row per (round, user, cell) —
    // a resubmission overwrites it (subject to REQ-202/210), never inserts a
    // second row.
    Task<Guess?> GetAsync(Guid roundId, Guid userId, Guid cellId, CancellationToken cancellationToken = default);

    // REQ-303: every guess a player has made in one round, for rendering
    // their own progress across the whole grid in a single query rather than
    // one GetAsync call per cell.
    Task<IReadOnlyList<Guess>> GetByRoundAndUserAsync(Guid roundId, Guid userId, CancellationToken cancellationToken = default);

    Task<Guess> AddAsync(Guess guess, CancellationToken cancellationToken = default);

    Task UpdateAsync(Guess guess, CancellationToken cancellationToken = default);
}

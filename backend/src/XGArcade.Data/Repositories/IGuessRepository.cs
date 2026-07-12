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

    // REQ-204: every *correct* guess across a set of cells, across every
    // player who solved them — this is the live uniqueness denominator's
    // population. Never includes incorrect/burned-attempt guesses (see
    // UniquenessCalculator's own doc comment on why that regressed once).
    Task<IReadOnlyList<Guess>> GetCorrectByCellIdsAsync(IReadOnlyCollection<Guid> cellIds, CancellationToken cancellationToken = default);

    // REQ-205: every guess (correct and incorrect) recorded for a round,
    // so round-close can lock FinalUniquenessScore/FinalPoints for all of
    // them in one pass.
    Task<IReadOnlyList<Guess>> GetByRoundIdAsync(Guid roundId, CancellationToken cancellationToken = default);

    // REQ-401/404: each user's all-time total (SUM of FinalPoints across
    // every round they've played), for the leaderboard. A user with no
    // guesses at all — or none yet locked by a round close — is simply
    // absent from the returned dictionary; callers treat a missing key as 0.
    Task<IReadOnlyDictionary<Guid, int>> GetTotalFinalPointsByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    Task<Guess> AddAsync(Guess guess, CancellationToken cancellationToken = default);

    // REQ-206/ADR-0021: round-close materializes one synthetic Guess row per
    // (participant, unattempted cell) pair so an unanswered cell scores the
    // same penalty as an incorrect one, rather than silently contributing 0
    // (which would now be the *best* possible score under the lowest-wins
    // model). Batched since a round-close can synthesize many rows at once.
    Task AddRangeAsync(IReadOnlyCollection<Guess> guesses, CancellationToken cancellationToken = default);

    Task UpdateAsync(Guess guess, CancellationToken cancellationToken = default);
}

using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class GuessRepository(XGArcadeDbContext dbContext) : IGuessRepository
{
    // Not AsNoTracking: GuessSubmissionService reads a Guess, mutates it, and
    // saves it back via UpdateAsync in the same request/scope on a
    // resubmission — same read-then-write flow as RoundRepository.GetByIdAsync.
    public async Task<Guess?> GetAsync(Guid roundId, Guid userId, Guid cellId, CancellationToken cancellationToken = default) =>
        await dbContext.Guesses.FirstOrDefaultAsync(
            g => g.RoundId == roundId && g.UserId == userId && g.CellId == cellId, cancellationToken);

    public async Task<IReadOnlyList<Guess>> GetByRoundAndUserAsync(Guid roundId, Guid userId, CancellationToken cancellationToken = default) =>
        await dbContext.Guesses
            .AsNoTracking()
            .Where(g => g.RoundId == roundId && g.UserId == userId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guess>> GetCorrectByCellIdsAsync(IReadOnlyCollection<Guid> cellIds, CancellationToken cancellationToken = default) =>
        await dbContext.Guesses
            .AsNoTracking()
            .Where(g => g.IsCorrect && cellIds.Contains(g.CellId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guess>> GetByRoundIdAsync(Guid roundId, CancellationToken cancellationToken = default) =>
        await dbContext.Guesses
            .Where(g => g.RoundId == roundId)
            .ToListAsync(cancellationToken);

    // REQ-206/401: the same "sum FinalPoints, treating null as 0" formula
    // Core.Scoring.ScoreCalculator.CalculateTotalPoints implements, computed
    // database-side instead — the leaderboard's scope (every guess a member
    // has ever made) is too large to pull into memory just to re-sum it.
    // Keep both in sync if this formula ever changes.
    public async Task<IReadOnlyDictionary<Guid, int>> GetTotalFinalPointsByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default) =>
        await dbContext.Guesses
            .AsNoTracking()
            .Where(g => g.UserId != null && userIds.Contains(g.UserId.Value))
            .GroupBy(g => g.UserId!.Value)
            .Select(group => new { UserId = group.Key, Total = group.Sum(g => g.FinalPoints ?? 0) })
            .ToDictionaryAsync(x => x.UserId, x => x.Total, cancellationToken);

    public async Task<Guess> AddAsync(Guess guess, CancellationToken cancellationToken = default)
    {
        dbContext.Guesses.Add(guess);
        await dbContext.SaveChangesAsync(cancellationToken);
        return guess;
    }

    public async Task UpdateAsync(Guess guess, CancellationToken cancellationToken = default)
    {
        dbContext.Guesses.Update(guess);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

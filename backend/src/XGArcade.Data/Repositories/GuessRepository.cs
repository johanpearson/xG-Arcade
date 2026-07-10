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

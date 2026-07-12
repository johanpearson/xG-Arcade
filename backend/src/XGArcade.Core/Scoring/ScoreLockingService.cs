using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Scoring;

public class ScoreLockingService(IGuessRepository guessRepository) : IScoreLockingService
{
    public async Task LockRoundScoresAsync(Guid roundId, CancellationToken cancellationToken = default)
    {
        var guesses = await guessRepository.GetByRoundIdAsync(roundId, cancellationToken);
        var correctGuessesByCell = guesses
            .Where(g => g.IsCorrect)
            .GroupBy(g => g.CellId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<Guess>)group.ToList());

        foreach (var guess in guesses)
        {
            if (guess.IsCorrect)
            {
                // Safe: ScoreSubmissionAsync never returns IsCorrect = true
                // without also setting PlayerAnswerId (ScoreResult's own doc
                // comment), and this guess is necessarily a member of its own
                // cell's correct-guesses group.
                var uniqueScore = UniquenessCalculator.Calculate(correctGuessesByCell[guess.CellId], guess.PlayerAnswerId!.Value);
                guess.FinalUniquenessScore = uniqueScore;
                guess.FinalPoints = ScoringRules.PointsFromUniqueScore(uniqueScore);
            }
            else
            {
                // REQ-203: an incorrect guess yields 0 points regardless of
                // uniqueness — there's no real answer to measure rarity
                // against, so FinalUniquenessScore stays null rather than
                // computing a number that wouldn't mean anything.
                guess.FinalUniquenessScore = null;
                guess.FinalPoints = 0;
            }

            await guessRepository.UpdateAsync(guess, cancellationToken);
        }
    }
}

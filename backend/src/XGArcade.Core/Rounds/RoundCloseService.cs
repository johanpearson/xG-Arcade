using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Rounds;

public class RoundCloseService(IRoundRepository roundRepository, IGuessRepository guessRepository) : IRoundCloseService
{
    public async Task<Round?> CloseRoundAsync(Guid roundId, DateTime closedAt, CancellationToken cancellationToken = default)
    {
        var round = await roundRepository.GetByIdAsync(roundId, cancellationToken);
        if (round is null)
            return null;

        // Idempotent: only ever pulls EndTime earlier, never pushes it later
        // than whatever was already scheduled.
        if (round.EndTime > closedAt)
        {
            round.EndTime = closedAt;
            await roundRepository.UpdateAsync(round, cancellationToken);
        }

        // REQ-205: lock FinalUniquenessScore/FinalPoints for every guess in
        // this round. Safe to recompute even on a repeat call (e.g. a round
        // that's already closed) — no further guesses can be accepted once
        // GetStatus stops reporting Active (GuessSubmissionService checks
        // this before ever reaching here), so the correct-guess population
        // this depends on can't have changed since the last computation.
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
                guess.FinalPoints = (int)Math.Round(uniqueScore * ScoringRules.MaxPointsPerCell);
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

        return round;
    }
}

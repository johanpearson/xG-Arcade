using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Scoring;

public class ScoreLockingService(
    IGuessRepository guessRepository,
    IRoundRepository roundRepository,
    IGameModuleResolver gameModuleResolver) : IScoreLockingService
{
    public async Task LockRoundScoresAsync(Guid roundId, CancellationToken cancellationToken = default)
    {
        await MaterializeUnansweredCellsAsync(roundId, cancellationToken);

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
                // ADR-0021: an incorrect guess (including a synthesized
                // "never attempted" one, see MaterializeUnansweredCellsAsync
                // below) scores the worst-case penalty, not 0 — under the
                // lowest-wins model, 0 is the *best* possible score, so
                // leaving it there would make failing to answer at least as
                // good as the rarest possible correct guess. There's no real
                // answer to measure rarity against, so FinalUniquenessScore
                // stays null rather than computing a number that wouldn't
                // mean anything.
                guess.FinalUniquenessScore = null;
                guess.FinalPoints = ScoringRules.MaxPointsPerCell;
            }

            await guessRepository.UpdateAsync(guess, cancellationToken);
        }
    }

    // ADR-0021: "unanswered equals a wrong guess" — but only for a cell
    // belonging to a round a player actually participated in (submitted at
    // least one guess for), never for a round they never opened at all.
    // Materializing real Guess rows (rather than special-casing "missing"
    // in the read/aggregation paths) keeps ScoreCalculator/the leaderboard's
    // SUM query unchanged — they still just sum FinalPoints ?? 0 across
    // whatever Guess rows exist, same as before this ADR.
    //
    // Idempotent by construction for SEQUENTIAL calls: a second call
    // re-derives "which cells are still missing" from what's actually
    // persisted, so already-materialized rows are simply excluded the
    // second time, no separate guard needed. Not guarded against two
    // CONCURRENT calls for the same round (no transaction/lock) — both
    // could compute the same "missing" set and race on the (RoundId,
    // UserId, CellId) unique index. No current caller can trigger that
    // (only the non-Production force-close-round endpoint calls this path
    // today); revisit if a real concurrent scheduled round-closer is ever
    // added.
    private async Task MaterializeUnansweredCellsAsync(Guid roundId, CancellationToken cancellationToken)
    {
        var round = await roundRepository.GetByIdAsync(roundId, cancellationToken);
        if (round is null)
            return;

        var existingGuesses = await guessRepository.GetByRoundIdAsync(roundId, cancellationToken);
        var participantIds = existingGuesses
            .Where(g => g.UserId is not null)
            .Select(g => g.UserId!.Value)
            .Distinct()
            .ToList();
        if (participantIds.Count == 0)
            return;

        var gameModule = gameModuleResolver.Resolve(round.GameKey);
        var allCellIds = await gameModule.GetCellIdsAsync(round.GameInstanceId, cancellationToken);
        if (allCellIds.Count == 0)
            return;

        var attemptedCellIdsByUser = existingGuesses
            .Where(g => g.UserId is not null)
            .GroupBy(g => g.UserId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(g => g.CellId).ToHashSet());

        var now = DateTime.UtcNow;
        var missingGuesses = new List<Guess>();
        foreach (var userId in participantIds)
        {
            var attempted = attemptedCellIdsByUser[userId];
            foreach (var cellId in allCellIds)
            {
                if (attempted.Contains(cellId))
                    continue;

                // AttemptCount = 0 and an empty SubmittedName distinguish
                // "never attempted" from a real (wrong, AttemptCount >= 1)
                // guess, in case that distinction ever matters for future
                // review/debugging — both score identically per ADR-0021.
                missingGuesses.Add(new Guess
                {
                    Id = Guid.NewGuid(),
                    RoundId = roundId,
                    UserId = userId,
                    CellId = cellId,
                    SubmittedName = string.Empty,
                    PlayerAnswerId = null,
                    IsCorrect = false,
                    AttemptCount = 0,
                    CreatedAt = now,
                });
            }
        }

        await guessRepository.AddRangeAsync(missingGuesses, cancellationToken);
    }
}

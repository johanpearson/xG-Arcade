using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Scoring;

public class ScoreLockingService(
    IGuessRepository guessRepository,
    IRoundRepository roundRepository,
    IGameModuleResolver gameModuleResolver,
    IScoringStrategyResolver scoringStrategyResolver) : IScoreLockingService
{
    public async Task LockRoundScoresAsync(Guid roundId, CancellationToken cancellationToken = default)
    {
        // Fetched once here and reused below (both by
        // MaterializeUnansweredCellsAsync and the ADR-0040 strategy
        // resolution) rather than each fetching it independently — the null
        // check that used to live inside MaterializeUnansweredCellsAsync
        // moves here with the fetch; behavior for a null round (nothing
        // materialized) is unchanged.
        var round = await roundRepository.GetByIdAsync(roundId, cancellationToken);
        if (round is not null)
            await MaterializeUnansweredCellsAsync(round, roundId, cancellationToken);

        var guesses = await guessRepository.GetByRoundIdAsync(roundId, cancellationToken);
        var correctGuessesByCell = guesses
            .Where(g => g.IsCorrect)
            .GroupBy(g => g.CellId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<Guess>)group.ToList());

        // ADR-0040: only resolved when at least one correct guess exists in
        // this round — an empty round never needs a strategy (or, as of
        // S-083/ADR-0041, a per-cell max-attempts lookup) at all, matching
        // this method's pre-ADR-0040 behavior.
        IScoringStrategy? scoringStrategy = null;
        // S-083/ADR-0041: resolved once per cell present in
        // correctGuessesByCell (never per guess) — mirrors
        // LiveRoundContributionService's own maxAttemptsByCellId pass, and
        // avoids an avoidable N-per-guess cost. Only strategies that
        // actually use maxAttemptsForCell (ClueEfficiencyScoringStrategy)
        // depend on this being correct; UniquenessScoringStrategy ignores it.
        var maxAttemptsByCell = new Dictionary<Guid, int>();
        if (correctGuessesByCell.Count > 0)
        {
            scoringStrategy = scoringStrategyResolver.Resolve(round!.GameKey);

            var gameModule = gameModuleResolver.Resolve(round.GameKey);
            foreach (var cellId in correctGuessesByCell.Keys)
            {
                maxAttemptsByCell[cellId] = await gameModule.GetMaxAttemptsForCellAsync(round.GameInstanceId, cellId, cancellationToken);
            }
        }

        foreach (var guess in guesses)
        {
            if (guess.IsCorrect)
            {
                // Safe: ScoreSubmissionAsync never returns IsCorrect = true
                // without also setting PlayerAnswerId (ScoreResult's own doc
                // comment), and this guess is necessarily a member of its own
                // cell's correct-guesses group. scoringStrategy/
                // maxAttemptsByCell[guess.CellId] are necessarily populated
                // here too, since correctGuessesByCell is non-empty whenever
                // any guess.IsCorrect is true.
                var result = scoringStrategy!.ScoreCorrectGuess(guess, correctGuessesByCell[guess.CellId], maxAttemptsByCell[guess.CellId]);
                guess.FinalUniquenessScore = result.FinalUniquenessScore;
                guess.FinalPoints = result.FinalPoints;
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
    // UserId, CellId) unique index. `RoundGenerationService` now calls this
    // path for real (REQ-205: closing a round's predecessor as part of the
    // generate-round scheduled job), alongside the pre-existing non-
    // Production force-close-round endpoint — but both are only ever
    // triggered by generate-round.yml's low-cadence cron (twice a week) or a
    // manual test-data call, never expected to overlap in practice; still
    // not fixed, just an accepted, documented risk at Tier 0 scale.
    //
    // round: fetched once by LockRoundScoresAsync (and reused there for
    // ADR-0040's strategy resolution) rather than fetched again here —
    // the caller only invokes this method once it has already confirmed
    // round is non-null.
    private async Task MaterializeUnansweredCellsAsync(Round round, Guid roundId, CancellationToken cancellationToken)
    {
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

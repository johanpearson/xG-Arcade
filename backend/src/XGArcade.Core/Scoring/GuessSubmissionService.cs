using XGArcade.Core.Games;
using XGArcade.Core.Rounds;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Scoring;

// COMP-04 (Core.Scoring): REQ-201/202/210's guess-acceptance rules.
//
// REQ-210's lock-on-correct and attempt-cap checks are resolved here, using
// only the existing Guess row — *before* the owning IGameModule is ever
// called (architecture-document.md §6.2's flow: "reject immediately ...
// checked before any name resolution work, not after"). Name resolution
// itself (REQ-207/208/209/211) is entirely the owning game module's
// responsibility (GridGameModule.ScoreSubmissionAsync for xg-grid) — Core
// never inspects a candidate player or a cell's categories directly.
public class GuessSubmissionService(
    IRoundRepository roundRepository,
    IGuessRepository guessRepository,
    IGameModuleResolver gameModuleResolver,
    TimeProvider timeProvider) : IGuessSubmissionService
{
    public async Task<GuessSubmissionResult> SubmitGuessAsync(
        Guid roundId, Guid userId, Guid cellId, string submittedName, CancellationToken cancellationToken = default)
    {
        var round = await roundRepository.GetByIdAsync(roundId, cancellationToken);
        if (round is null)
            return GuessSubmissionResult.Rejected(GuessSubmissionOutcome.RoundNotFound);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        // REQ-201: guesses are only accepted for an active (not closed,
        // already-started) round.
        if (round.GetStatus(now) != RoundStatus.Active)
            return GuessSubmissionResult.Rejected(GuessSubmissionOutcome.RoundNotActive);

        var existingGuess = await guessRepository.GetAsync(roundId, userId, cellId, cancellationToken);

        // REQ-210: checked before any name resolution work, not after — no
        // call to IGameModule happens until we know an attempt is allowed.
        if (existingGuess is not null && existingGuess.IsCorrect)
            return GuessSubmissionResult.Rejected(GuessSubmissionOutcome.CellAlreadySolved);
        if (existingGuess is not null && existingGuess.AttemptCount >= GuessRules.MaxAttemptsPerCell)
            return GuessSubmissionResult.Rejected(GuessSubmissionOutcome.NoAttemptsRemaining);

        // REQ-202: guess-change policy — subordinate to REQ-210's lock/cap
        // above, which always take precedence regardless of this setting.
        if (existingGuess is not null && existingGuess.AttemptCount >= 1 && !round.AllowGuessChange)
            return GuessSubmissionResult.Rejected(GuessSubmissionOutcome.GuessChangeNotAllowed);

        var gameModule = gameModuleResolver.Resolve(round.GameKey);
        var scoreResult = await gameModule.ScoreSubmissionAsync(
            round.GameInstanceId, userId, new GuessSubmission(cellId, submittedName), cancellationToken);

        var attemptCount = (existingGuess?.AttemptCount ?? 0) + 1;
        if (existingGuess is null)
        {
            await guessRepository.AddAsync(new Guess
            {
                Id = Guid.NewGuid(),
                RoundId = roundId,
                UserId = userId,
                CellId = cellId,
                SubmittedName = submittedName,
                PlayerAnswerId = scoreResult.PlayerAnswerId,
                IsCorrect = scoreResult.IsCorrect,
                AttemptCount = attemptCount,
                CreatedAt = now,
            }, cancellationToken);
        }
        else
        {
            existingGuess.SubmittedName = submittedName;
            existingGuess.PlayerAnswerId = scoreResult.PlayerAnswerId;
            existingGuess.IsCorrect = scoreResult.IsCorrect;
            existingGuess.AttemptCount = attemptCount;
            await guessRepository.UpdateAsync(existingGuess, cancellationToken);
        }

        // REQ-210: locks immediately on a correct answer, even if only 1 of
        // the 2 attempts was used; otherwise locks once both are used.
        var locked = scoreResult.IsCorrect || attemptCount >= GuessRules.MaxAttemptsPerCell;
        return GuessSubmissionResult.Accepted(scoreResult.IsCorrect, attemptCount, locked);
    }
}

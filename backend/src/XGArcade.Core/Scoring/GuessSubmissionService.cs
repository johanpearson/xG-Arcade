using XGArcade.Core.Games;
using XGArcade.Core.Rounds;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Scoring;

// COMP-04 (Core.Scoring): REQ-201/202/210's guess-acceptance rules.
//
// REQ-210's lock-on-correct and attempt-cap checks are resolved here, using
// only the existing Guess row plus the cell's own max-attempts value
// (ADR-0041, IGameModule.GetMaxAttemptsForCellAsync) — *before* the owning
// IGameModule's ScoreSubmissionAsync (name resolution) is ever called
// (architecture-document.md §6.2's flow: "reject immediately ... checked
// before any name resolution work, not after"). Name resolution itself
// (REQ-207/208/209/211) is entirely the owning game module's responsibility
// (GridGameModule.ScoreSubmissionAsync for xg-grid) — Core never inspects a
// candidate player or a cell's categories directly.
// S-106 (pure refactor): IPlayerStoreRepository's own GetPlayerByIdAsync
// moved to IPlayerRepository — this class's only player-store call, so it
// takes the narrower interface directly rather than IPlayerStoreRepository.
//
// S-200/ADR-0098 Consequences: allowedGameKeys is an explicit allow-list of
// the GameKeys this Guess-based submission path is built for, supplied by
// the composition root (never hardcoded here — ADR-0003, same shape as
// IScoringStrategy.GameKey/GuessRoundScoreSource.GameKey) — an allow-list,
// not a deny-list naming "xg-predict", so Core.Scoring never references
// Games.XGPredict and any future non-Guess-based game is rejected the same
// way without a further change here. A dedicated type
// (GuessSubmissionAllowedGameKeys) rather than a raw
// IReadOnlyCollection<string>, so this DI registration can never collide
// with some other component's future need for a plain string collection.
public class GuessSubmissionService(
    IRoundRepository roundRepository,
    IGuessRepository guessRepository,
    IGameModuleResolver gameModuleResolver,
    IPlayerRepository playerRepository,
    TimeProvider timeProvider,
    GuessSubmissionAllowedGameKeys allowedGameKeys) : IGuessSubmissionService
{
    public async Task<GuessSubmissionResult> SubmitGuessAsync(
        Guid roundId, Guid userId, Guid cellId, string submittedName, Guid? chosenPlayerId = null, CancellationToken cancellationToken = default)
    {
        var round = await roundRepository.GetByIdAsync(roundId, cancellationToken);
        if (round is null)
            return GuessSubmissionResult.Rejected(GuessSubmissionOutcome.RoundNotFound);

        // S-200/ADR-0098 Consequences: checked before IGameModuleResolver is
        // ever consulted (and therefore before GetMaxAttemptsForCellAsync/
        // ScoreSubmissionAsync could ever be called) — this guard is
        // structural, not dependent on any particular IGameModule's
        // implementation state. See allowedGameKeys' own doc comment above
        // for why this is an allow-list, not a "xg-predict" deny-list.
        if (!allowedGameKeys.GameKeys.Contains(round.GameKey))
            return GuessSubmissionResult.Rejected(GuessSubmissionOutcome.GameNotSupported);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        // REQ-201: guesses are only accepted for an active (not closed,
        // already-started) round.
        if (round.GetStatus(now) != RoundStatus.Active)
            return GuessSubmissionResult.Rejected(GuessSubmissionOutcome.RoundNotActive);

        var existingGuess = await guessRepository.GetAsync(roundId, userId, cellId, cancellationToken);

        // ADR-0041: resolved before the REQ-210 checks below so the cap
        // itself can be read per-cell through the module — but this is only
        // module *resolution* plus a GetMaxAttemptsForCellAsync call, never
        // ScoreSubmissionAsync (name-resolution work), which still happens
        // only after every REQ-210/REQ-202 check below passes.
        var gameModule = gameModuleResolver.Resolve(round.GameKey);
        var maxAttemptsForCell = await gameModule.GetMaxAttemptsForCellAsync(round.GameInstanceId, cellId, cancellationToken);

        // REQ-210: checked before any name resolution work, not after — no
        // call to IGameModule.ScoreSubmissionAsync happens until we know an
        // attempt is allowed.
        if (existingGuess is not null && existingGuess.IsCorrect)
            return GuessSubmissionResult.Rejected(GuessSubmissionOutcome.CellAlreadySolved);
        if (existingGuess is not null && existingGuess.AttemptCount >= maxAttemptsForCell)
            return GuessSubmissionResult.Rejected(GuessSubmissionOutcome.NoAttemptsRemaining);

        // REQ-202: guess-change policy — subordinate to REQ-210's lock/cap
        // above, which always take precedence regardless of this setting.
        if (existingGuess is not null && existingGuess.AttemptCount >= 1 && !round.AllowGuessChange)
            return GuessSubmissionResult.Rejected(GuessSubmissionOutcome.GuessChangeNotAllowed);

        ScoreResult scoreResult;
        try
        {
            scoreResult = await gameModule.ScoreSubmissionAsync(
                round.GameInstanceId, userId, new GuessSubmission(cellId, submittedName, chosenPlayerId), cancellationToken);
        }
        catch (LiveLookupUnavailableException)
        {
            // REQ-211: a live-lookup timeout means "we don't know yet," not
            // "wrong" — return before ever touching guessRepository, same
            // shape as REQ-209's disambiguation branch below (no
            // AddAsync/UpdateAsync, no attempt-count increment, no lock
            // check). The player gets a genuine retry, not a consumed one.
            return GuessSubmissionResult.Rejected(GuessSubmissionOutcome.LiveLookupUnavailable);
        }

        // REQ-209/REQ-210: showing a disambiguation prompt is part of the
        // same attempt that triggered it, not a separate one — return
        // immediately, without ever touching guessRepository (no
        // AddAsync/UpdateAsync, no attempt-count increment, no lock check).
        // Only the player's eventual choice (valid or not) is a real scored
        // guess, handled by a later call to this method with chosenPlayerId
        // set.
        if (scoreResult.DisambiguationCandidates is { Count: > 0 })
            return GuessSubmissionResult.NeedsDisambiguation(scoreResult.DisambiguationCandidates);

        var attemptCount = (existingGuess?.AttemptCount ?? 0) + 1;

        // REQ-210: locks immediately on a correct answer, even if only 1 of
        // the 2 attempts was used; otherwise locks once the cell's own
        // max-attempts (ADR-0041) is used. Computed here, before the Guess
        // row is written below, so REQ-216's wrong-guess resolution (next)
        // can be persisted in the SAME write — never a second/batched
        // update.
        var locked = scoreResult.IsCorrect || attemptCount >= maxAttemptsForCell;

        // REQ-216/ADR-0057: fires exactly once, only on the submission that
        // just locked this cell with its final guess still incorrect — never
        // for state 2 (incorrect, attempts remaining), which this condition
        // excludes outright. A second call for the same cell can never
        // happen: once locked, the REQ-210 checks above (CellAlreadySolved/
        // NoAttemptsRemaining) reject any further guess before this point is
        // ever reached again. Wikidata-only, never API-Football, never
        // gated on any ExternalApiUsage threshold — see
        // IGameModule.ResolveWrongGuessPlayerAsync's own doc comment. Any
        // failure (timeout, error, no PlayerNameIndex match) surfaces here as
        // a plain null, never an exception — ADR-0057's silent, graceful
        // fallback; there is no correctness verdict left to compute for a
        // guess already known to be wrong.
        var wrongGuessPlayer = locked && !scoreResult.IsCorrect
            ? await gameModule.ResolveWrongGuessPlayerAsync(round.GameInstanceId, submittedName, cancellationToken)
            : null;

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
                MatchedPlayerName = wrongGuessPlayer?.PlayerName,
                MatchedPlayerPhotoUrl = wrongGuessPlayer?.PhotoUrl,
            }, cancellationToken);
        }
        else
        {
            existingGuess.SubmittedName = submittedName;
            existingGuess.PlayerAnswerId = scoreResult.PlayerAnswerId;
            existingGuess.IsCorrect = scoreResult.IsCorrect;
            existingGuess.AttemptCount = attemptCount;
            existingGuess.MatchedPlayerName = wrongGuessPlayer?.PlayerName;
            existingGuess.MatchedPlayerPhotoUrl = wrongGuessPlayer?.PhotoUrl;
            await guessRepository.UpdateAsync(existingGuess, cancellationToken);
        }

        // Frontend name-display fix: a correct guess's canonical, properly-
        // cased name — never the raw as-typed submittedName, which stays
        // exactly as the player entered it on the Guess row itself. Safe:
        // scoreResult.IsCorrect is never true without PlayerAnswerId also
        // being set (ScoreResult's own doc comment).
        // REQ-214: resolvedPlayer.PhotoUrl travels alongside FullName from
        // the same lookup — never a second query, and null exactly when
        // Wikidata had no P18 for this player (no error either way).
        var resolvedPlayer = scoreResult.IsCorrect
            ? await playerRepository.GetPlayerByIdAsync(scoreResult.PlayerAnswerId!.Value, cancellationToken)
            : null;

        return GuessSubmissionResult.Accepted(
            scoreResult.IsCorrect, attemptCount, locked, resolvedPlayer?.FullName, resolvedPlayer?.PhotoUrl,
            wrongGuessPlayer?.PlayerName, wrongGuessPlayer?.PhotoUrl);
    }
}

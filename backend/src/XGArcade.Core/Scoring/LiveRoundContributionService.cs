using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Scoring;

public class LiveRoundContributionService(
    IGuessRepository guessRepository,
    IGameModuleResolver gameModuleResolver) : ILiveRoundContributionService
{
    public async Task<IReadOnlyDictionary<Guid, int>> GetContributionsByUserIdAsync(
        Round round, CancellationToken cancellationToken = default)
    {
        var guesses = await guessRepository.GetByRoundIdAsync(round.Id, cancellationToken);

        // ADR-0003: cells are resolved generically through IGameModule, never
        // by reaching into GridInstance/GridCell directly — same pattern
        // ScoreLockingService.MaterializeUnansweredCellsAsync already uses.
        // Guarding correctGuessesByCell/participant guesses against this set
        // is defensive (every Guess for this RoundId should already belong to
        // its current GameInstanceId's cells) rather than load-bearing today.
        var gameModule = gameModuleResolver.Resolve(round.GameKey);
        var cellIds = (await gameModule.GetCellIdsAsync(round.GameInstanceId, cancellationToken)).ToHashSet();
        var guessesInThisInstance = cellIds.Count == 0
            ? []
            : guesses.Where(g => cellIds.Contains(g.CellId)).ToList();

        // Same "correct guesses, grouped by cell" population UniquenessCalculator
        // needs — built from every guess for the cell (not just this round's
        // participants) so an anonymized (UserId == null) correct guess still
        // counts toward rarity, exactly as RoundEndpoints/ScoreLockingService
        // already do.
        var correctGuessesByCell = guessesInThisInstance
            .Where(g => g.IsCorrect)
            .GroupBy(g => g.CellId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<Guess>)group.ToList());

        // Participant definition (ADR-0021's MaterializeUnansweredCellsAsync):
        // any user with UserId != null on >=1 Guess row in this round.
        var participantGuesses = guessesInThisInstance.Where(g => g.UserId is not null).ToList();
        if (participantGuesses.Count == 0)
            return new Dictionary<Guid, int>();

        // Every participant starts at 0 (not absent) so a participant who has
        // attempted something but has nothing locked/correct yet is still
        // distinguishable from a true non-participant (who is never a key in
        // this dictionary at all) — see this interface's own doc comment.
        var contributionsByUserId = participantGuesses
            .Select(g => g.UserId!.Value)
            .Distinct()
            .ToDictionary(userId => userId, _ => 0);

        // Per-participant "which cells have they made at least one guess on
        // at all" — needed below (2026-07-20) to find each participant's
        // zero-guess cells, distinct from a cell with one of two attempts
        // used and still unresolved (which stays in this set and therefore
        // correctly contributes nothing here).
        var attemptedCellIdsByUserId = participantGuesses
            .GroupBy(g => g.UserId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(g => g.CellId).ToHashSet());

        foreach (var guess in participantGuesses)
        {
            int? cellContribution = null;
            if (guess.IsCorrect)
            {
                // S-018/REQ-204: the exact same UniquenessCalculator +
                // ScoringRules.PointsFromUniqueScore call RoundEndpoints and
                // ScoreLockingService already make — never a third formula.
                var uniqueScore = UniquenessCalculator.Calculate(correctGuessesByCell[guess.CellId], guess.PlayerAnswerId!.Value);
                cellContribution = ScoringRules.PointsFromUniqueScore(uniqueScore);
            }
            else if (guess.AttemptCount >= GuessRules.MaxAttemptsPerCell)
            {
                // Locked-incorrect: both attempts used, never correct.
                cellContribution = ScoringRules.MaxPointsPerCell;
            }
            // else: incorrect with an attempt still remaining — not yet
            // resolved either way, contributes nothing (same as never having
            // attempted the cell at all).

            if (cellContribution is not null)
            {
                contributionsByUserId[guess.UserId!.Value] += cellContribution.Value;
            }
        }

        // REQ-406/407 (2026-07-20): for a participant, every cell in this
        // round's grid they have made ZERO guesses on at all (no Guess row
        // for that cell, distinct from a cell with one of two attempts used
        // and still unresolved, which stays out of this loop entirely)
        // contributes MaxPointsPerCell — same as a locked-incorrect cell.
        // Mirrors ScoreLockingService.MaterializeUnansweredCellsAsync's own
        // per-participant "every cell not in their attempted set" pass,
        // just computed on the fly here instead of materialized as rows.
        foreach (var userId in contributionsByUserId.Keys.ToList())
        {
            var attemptedCellIds = attemptedCellIdsByUserId[userId];
            foreach (var cellId in cellIds)
            {
                if (!attemptedCellIds.Contains(cellId))
                {
                    contributionsByUserId[userId] += ScoringRules.MaxPointsPerCell;
                }
            }
        }

        return contributionsByUserId;
    }
}

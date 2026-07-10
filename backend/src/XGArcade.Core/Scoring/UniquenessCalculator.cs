using XGArcade.Data.Entities;

namespace XGArcade.Core.Scoring;

// REQ-204's uniqueness formula, and the one place it's allowed to be
// written — shared by the live read path (RoundEndpoints) and round-close's
// final-score computation (RoundCloseService) so both always agree.
//
// A real bug already regressed once here during design (review-2026-07-07-
// design.md, finding 2): an earlier draft's denominator was every Guess for
// the cell, including incorrect ones and burned attempts — that let how
// much *failing* happened on a cell distort everyone's score, which has
// nothing to do with answer rarity. The fix, preserved here: both the
// numerator and denominator only ever count correct guesses, one per
// player (enforced upstream by Guess's (RoundId, UserId, CellId) unique
// index — a player can have at most one Guess row per cell).
public static class UniquenessCalculator
{
    // correctGuessesForCell: every Guess for one cell where IsCorrect is
    // true — callers must never pass incorrect/burned-attempt guesses here.
    // myAnswerPlayerId: the PlayerAnswerId of the guess being scored (must
    // itself be one of correctGuessesForCell).
    public static double Calculate(IReadOnlyCollection<Guess> correctGuessesForCell, Guid myAnswerPlayerId)
    {
        if (correctGuessesForCell.Count == 0)
        {
            throw new InvalidOperationException(
                "Cannot calculate uniqueness with zero correct guesses — REQ-204 only applies once at least one correct guess exists for the cell.");
        }

        var sameAnswerCount = correctGuessesForCell.Count(g => g.PlayerAnswerId == myAnswerPlayerId);
        return 1.0 - (double)sameAnswerCount / correctGuessesForCell.Count;
    }
}

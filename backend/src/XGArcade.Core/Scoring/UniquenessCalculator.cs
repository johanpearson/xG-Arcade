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
//
// ADR-0020: the comparison excludes the guesser's own guess from both sides
// of the ratio. An earlier version compared each guesser against the whole
// correct-guess population including themselves, which meant a lone correct
// guesser was trivially "100% of the population sharing their answer" (i.e.
// the same guess counted against itself) and scored 0% unique/0 points —
// backwards from the intent that being the only (or first) correct answer
// for a cell should score maximally unique, not minimally. The formula now
// asks "of the *other* correct guessers, what share picked the same answer
// as me" — with no other correct guessers yet, that's vacuously 0%, so this
// guess is 100% unique.
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

        var otherCorrectGuessCount = correctGuessesForCell.Count - 1;
        if (otherCorrectGuessCount == 0)
        {
            // No other correct guesser to compare against yet — the first/
            // only correct answer for a cell is maximally unique.
            return 1.0;
        }

        var othersWithSameAnswer = correctGuessesForCell.Count(g => g.PlayerAnswerId == myAnswerPlayerId) - 1;
        return 1.0 - (double)othersWithSameAnswer / otherCorrectGuessCount;
    }
}

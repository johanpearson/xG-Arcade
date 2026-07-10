using XGArcade.Data.Entities;

namespace XGArcade.Core.Scoring;

// REQ-206: the sum of FinalPoints across a set of guesses. The caller
// decides the scope — one round's cells for a per-round total, or every
// guess a player has ever made for a leaderboard's all-time total — the
// math is identical either way.
public static class ScoreCalculator
{
    // Unanswered cells never have a Guess row at all, so "unanswered cells
    // count as 0 points" falls out of summing only what's actually in the
    // collection — no placeholder zero-rows needed. A guess whose
    // FinalPoints hasn't been locked yet (round still active) also
    // contributes 0, since it isn't a *final* score yet.
    public static int CalculateTotalPoints(IEnumerable<Guess> guesses) =>
        guesses.Sum(g => g.FinalPoints ?? 0);
}

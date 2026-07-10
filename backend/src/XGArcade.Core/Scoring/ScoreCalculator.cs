using XGArcade.Data.Entities;

namespace XGArcade.Core.Scoring;

// REQ-206: the sum of FinalPoints across a set of guesses, when the caller
// already has the Guess rows in memory (e.g. one round's cells for a
// per-round total). This is REQ-206's canonical formula, unit-tested in
// isolation (ScoreCalculatorTests) — deliberately NOT called by the
// leaderboard's all-time total (GuessRepository
// .GetTotalFinalPointsByUserIdsAsync), which reimplements the same SUM as a
// database-side GROUP BY instead: that path can cover many users' entire
// guess history, and pulling every Guess row into memory just to re-sum
// them here would be the wrong tradeoff at that scale (REQ-607). Both must
// keep computing the same thing (FinalPoints ?? 0, summed); if this
// formula ever changes, check GetTotalFinalPointsByUserIdsAsync too.
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

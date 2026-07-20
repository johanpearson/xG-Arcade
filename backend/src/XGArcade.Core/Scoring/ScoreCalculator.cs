using XGArcade.Data.Entities;

namespace XGArcade.Core.Scoring;

// REQ-206: the sum of FinalPoints across a set of guesses, when the caller
// already has the Guess rows in memory (e.g. one round's cells for a
// per-round total). This is REQ-206's canonical formula, unit-tested in
// isolation (ScoreCalculatorTests) — deliberately NOT called by the
// leaderboard's per-round/per-user totals (GuessRepository
// .GetTotalFinalPointsByRoundIdsAsync/.GetPerRoundFinalPointsByUserIdsAsync),
// which reimplement the same SUM as a database-side GROUP BY instead: those
// paths can cover many users'/rounds' entire guess history, and pulling
// every Guess row into memory just to re-sum them here would be the wrong
// tradeoff at that scale (REQ-607). All must keep computing the same thing
// (FinalPoints ?? 0, summed); if this formula ever changes, check those too.
public static class ScoreCalculator
{
    // ADR-0021: a cell a round's participant never attempted no longer
    // relies on "no Guess row = 0" falling out for free — 0 is now the
    // *best* possible score (lowest-wins), so ScoreLockingService
    // materializes a real, penalized Guess row for it at round close
    // (MaterializeUnansweredCellsAsync) before this ever sums anything. This
    // formula itself is unchanged: still just SUM(FinalPoints ?? 0). A
    // guess whose FinalPoints hasn't been locked yet (round still active)
    // contributes 0 here, since it isn't a *final* score yet — not to be
    // confused with 0 as a genuinely-locked best-case score.
    public static int CalculateTotalPoints(IEnumerable<Guess> guesses) =>
        guesses.Sum(g => g.FinalPoints ?? 0);
}

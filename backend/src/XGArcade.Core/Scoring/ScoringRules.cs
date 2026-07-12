namespace XGArcade.Core.Scoring;

// REQ-205/206's points scale. No document specifies an exact value for "how
// many points is the worst-case (fully common) correct guess worth" — this
// is the Tier 0 default, same non-appsettings-bound, plain-constant pattern
// as GuessRules.MaxAttemptsPerCell.
//
// ADR-0021: xG Arcade is scored like golf — LOWER is better, and a player's
// (or the leaderboard's) goal is to MINIMIZE total points, not maximize
// them. MaxPointsPerCell is therefore the WORST possible per-cell outcome
// (a fully common correct answer, an incorrect guess, or an unanswered
// cell all score this), and 0 is the BEST (a correct answer nobody else
// shares).
public static class ScoringRules
{
    public const int MaxPointsPerCell = 100;

    // REQ-205's locked-score formula, and the one place it's allowed to be
    // written — shared by ScoreLockingService's FinalPoints and
    // RoundEndpoints' live LivePoints (S-018) so the two can never drift
    // into two different roundings/scalings of the same uniqueScore.
    //
    // ADR-0021: inverted from an earlier direct `uniqueScore * MaxPointsPerCell`
    // mapping (higher uniqueScore -> higher points -> "more points is
    // better"). uniqueScore itself is unchanged (still 1.0 = nobody else
    // shares this answer, ADR-0020) — only its mapping to points is
    // inverted, so the rarest possible correct answer scores 0 (best, under
    // lowest-wins) and the most commonly-shared one scores MaxPointsPerCell
    // (worst).
    public static int PointsFromUniqueScore(double uniqueScore) =>
        (int)Math.Round((1.0 - uniqueScore) * MaxPointsPerCell);
}

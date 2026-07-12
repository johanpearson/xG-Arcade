namespace XGArcade.Core.Scoring;

// REQ-205/206's points scale. No document specifies an exact value for "how
// many points is a 100%-unique correct guess worth" — this is the Tier 0
// default, same non-appsettings-bound, plain-constant pattern as
// GuessRules.MaxAttemptsPerCell.
public static class ScoringRules
{
    public const int MaxPointsPerCell = 100;

    // REQ-205's locked-score formula, and the one place it's allowed to be
    // written — shared by ScoreLockingService's FinalPoints and
    // RoundEndpoints' live LivePoints (S-018) so the two can never drift
    // into two different roundings/scalings of the same uniqueScore.
    public static int PointsFromUniqueScore(double uniqueScore) =>
        (int)Math.Round(uniqueScore * MaxPointsPerCell);
}

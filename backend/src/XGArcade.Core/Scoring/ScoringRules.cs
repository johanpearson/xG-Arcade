namespace XGArcade.Core.Scoring;

// REQ-205/206's points scale. No document specifies an exact value for "how
// many points is a 100%-unique correct guess worth" — this is the Tier 0
// default, same non-appsettings-bound, plain-constant pattern as
// GuessRules.MaxAttemptsPerCell.
public static class ScoringRules
{
    public const int MaxPointsPerCell = 100;
}

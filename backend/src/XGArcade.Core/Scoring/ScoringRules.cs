namespace XGArcade.Core.Scoring;

// REQ-205/206's points scale. No document specifies an exact value for "how
// many points is the worst-case (fully common) correct guess worth" — this
// is the Tier 0 default, a plain constant not bound to appsettings. Unlike
// REQ-210's attempt cap (ADR-0041: resolved per-cell through IGameModule
// since it isn't the same value for every game), this points scale is a
// genuine platform-wide constant.
//
// ADR-0021: xG Arcade is scored like golf — LOWER is better, and a player's
// (or the leaderboard's) goal is to MINIMIZE total points, not maximize
// them. MaxPointsPerCell is therefore the WORST possible per-cell outcome
// (a fully common correct answer, an incorrect guess, or an unanswered
// cell all score this), and 0 is the BEST (a correct answer nobody else
// shares).
//
// Exception: PredictPointsPerComponent below is xG Predict's award value
// (ADR-0095) — that one GameKey is conventional higher-is-better, not
// golf-style, so read its own comment rather than assuming this class-level
// "lower is better" framing applies to it too.
public static class ScoringRules
{
    public const int MaxPointsPerCell = 100;

    // REQ-1304's per-component award value for xG Predict (outcome/
    // home-goals/away-goals, each independently 0-or-this). Not specified
    // by REQ-1304's own text — same "exact point values are an
    // implementation detail" precedent as MaxPointsPerCell above; only that
    // naming/ownership convention carries over, not its golf-style
    // direction (ADR-0095: xG Predict is higher-is-better, the named
    // exception to ADR-0021 — see XGPredictScoringStrategy).
    public const int PredictPointsPerComponent = 10;

    // REQ-205's locked-score formula, and the one place it's allowed to be
    // written — shared by RoundEndpoints' live LivePoints (S-018) and, as of
    // S-076/ADR-0040, ScoreLockingService's FinalPoints only indirectly, via
    // UniquenessScoringStrategy (ScoreLockingService no longer calls this
    // directly; it resolves an IScoringStrategy per GameKey, and xG Grid's
    // implementation calls this method on its behalf) — so the two can
    // never drift into two different roundings/scalings of the same
    // uniqueScore.
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

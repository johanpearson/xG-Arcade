using XGArcade.Core.Scoring;

namespace XGArcade.Core.Tests.Scoring;

// REQ-204/REQ-205 (docs/requirements-document.md §4.4/§4.5, ADR-0021):
// ScoringRules.PointsFromUniqueScore is the one place the uniqueScore ->
// points mapping is written, shared by REQ-204's live LivePoints estimate
// and REQ-205's locked FinalPoints (RoundCloseServiceScoringTests/
// CurrentRoundEndpointTests exercise it only indirectly, through DB-backed
// scenarios that happen to land on exact 0.0/0.5/1.0 uniqueScores). Exercised
// directly here so its rounding behavior at non-exact-multiple fractions is
// pinned down on its own — in particular Math.Round's default
// MidpointRounding.ToEven ("banker's rounding") at exact .5 boundaries,
// which none of the existing DB-backed scenarios happen to exercise.
public class ScoringRulesTests
{
    [Test]
    public void REQ204_PointsFromUniqueScore_FullyUnique_ReturnsZero()
    {
        // ADR-0021: uniqueScore = 1.0 (nobody else shares this answer) is
        // the BEST possible outcome under lowest-wins scoring, so it must
        // map to 0, not MaxPointsPerCell.
        var points = ScoringRules.PointsFromUniqueScore(1.0);

        Assert.That(points, Is.EqualTo(0));
    }

    [Test]
    public void REQ204_PointsFromUniqueScore_NotUniqueAtAll_ReturnsMaxPointsPerCell()
    {
        // ADR-0021: uniqueScore = 0.0 (every other correct guesser shares
        // this exact answer) is the WORST possible correct-guess outcome —
        // it must map to MaxPointsPerCell, the same worst-case value an
        // incorrect guess or a materialized unanswered cell locks at
        // (ScoreLockingService).
        var points = ScoringRules.PointsFromUniqueScore(0.0);

        Assert.That(points, Is.EqualTo(ScoringRules.MaxPointsPerCell));
    }

    [Test]
    public void REQ204_PointsFromUniqueScore_Midpoint_ReturnsHalfOfMaxPointsPerCell()
    {
        var points = ScoringRules.PointsFromUniqueScore(0.5);

        Assert.That(points, Is.EqualTo(ScoringRules.MaxPointsPerCell / 2));
    }

    // Math.Round's default MidpointRounding is ToEven ("banker's rounding"),
    // not "round half up" or "round half down" — these two cases land
    // exactly on a .5 boundary in opposite directions:
    //   uniqueScore=0.625 -> (1 - 0.625) * 100 = 37.5 -> rounds UP to the
    //     nearest even integer, 38 (not 37)
    //   uniqueScore=0.375 -> (1 - 0.375) * 100 = 62.5 -> rounds DOWN to the
    //     nearest even integer, 62 (not 63)
    // Both operands are exact dyadic fractions (eighths), so this isn't a
    // floating-point-precision artifact — it's Math.Round's real, specified
    // rounding mode, pinned down explicitly so a future refactor (e.g.
    // switching to a different rounding call) can't silently change it.
    [TestCase(0.625, 38)]
    [TestCase(0.375, 62)]
    public void REQ204_PointsFromUniqueScore_ExactMidpointFraction_RoundsToNearestEven(double uniqueScore, int expectedPoints)
    {
        var points = ScoringRules.PointsFromUniqueScore(uniqueScore);

        Assert.That(points, Is.EqualTo(expectedPoints));
    }

    [Test]
    public void REQ204_PointsFromUniqueScore_IsMonotonicallyNonIncreasing_AsUniqueScoreIncreases()
    {
        // The whole point of ADR-0021's inversion: a MORE unique (rarer)
        // correct answer must never score MORE points than a less unique
        // one. Regression guard against the mapping accidentally flipping
        // back (or partially flipping) in a future edit.
        var uniqueScoresAscending = new[] { 0.0, 0.1, 0.25, 0.4, 0.5, 0.6, 0.75, 0.9, 1.0 };

        var points = uniqueScoresAscending.Select(ScoringRules.PointsFromUniqueScore).ToList();

        for (var i = 1; i < points.Count; i++)
        {
            Assert.That(points[i], Is.LessThanOrEqualTo(points[i - 1]),
                $"points at uniqueScore={uniqueScoresAscending[i]} ({points[i]}) must not exceed " +
                $"points at uniqueScore={uniqueScoresAscending[i - 1]} ({points[i - 1]})");
        }
    }
}

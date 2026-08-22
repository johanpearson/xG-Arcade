using XGArcade.Data.Entities;

namespace XGArcade.Games.XGPath.Tests;

// REQ-1203/S-082: PathClueSequenceBuilder.BuildSequence/GetRevealedTurnCount
// — docs/requirements-document.md's own "Test level" note for this REQ
// calls out exactly what's covered below: the 3-way club-count split at N=3
// (minimum), a non-multiple-of-3 value below 10, and a value at/above 10;
// appearance count present vs. unknown within a multi-club turn;
// chronological order preserved both across and within turns; the bundled
// year-range clue's content; the fixed position/nationality/age order; and
// the sequence halting immediately on a correct guess at every possible
// point. Deliberately pure — no DbContext/repository setup, unlike
// XGPathGameModuleTests — see PathClueSequenceBuilder's own doc comment for
// why this class is DB-free by design.
public class PathClueSequenceBuilderTests
{
    // SequenceOrder/PlayerId are irrelevant to BuildSequence (it trusts the
    // caller to have already supplied stints in chronological order, per its
    // own doc comment) — left at defaults.
    private static PlayerCareerStint Stint(string clubName, int startYear, int? endYear, int? appearanceCount = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            PlayerId = Guid.NewGuid(),
            ClubName = clubName,
            StartYear = startYear,
            EndYear = endYear,
            AppearanceCount = appearanceCount,
        };

    private static IReadOnlyList<int> ClubTurnSizes(IReadOnlyList<PathClueTurn> turns) =>
        turns.Where(t => t.Kind == PathClueKind.ClubReveal).Select(t => t.Clubs!.Count).ToList();

    // ---- REQ-1203: the fixed 3-way club-reveal split, worked examples -----

    [Test]
    public void REQ1203_BuildSequence_MinimumThreeStints_SplitsOneOneOne()
    {
        var stints = new[] { Stint("Club A", 2010, 2013), Stint("Club B", 2013, 2016), Stint("Club C", 2016, null) };

        var turns = PathClueSequenceBuilder.BuildSequence(stints, null, null, null);

        Assert.That(ClubTurnSizes(turns), Is.EqualTo(new[] { 1, 1, 1 }));
    }

    [Test]
    public void REQ1203_BuildSequence_FourStints_NonMultipleOfThreeBelowTen_SplitsOneOneTwo()
    {
        var stints = Enumerable.Range(0, 4).Select(i => Stint($"Club {i}", 2000 + i, 2001 + i)).ToArray();

        var turns = PathClueSequenceBuilder.BuildSequence(stints, null, null, null);

        Assert.That(ClubTurnSizes(turns), Is.EqualTo(new[] { 1, 1, 2 }));
    }

    [Test]
    public void REQ1203_BuildSequence_FiveStints_NonMultipleOfThreeBelowTen_SplitsOneTwoTwo()
    {
        var stints = Enumerable.Range(0, 5).Select(i => Stint($"Club {i}", 2000 + i, 2001 + i)).ToArray();

        var turns = PathClueSequenceBuilder.BuildSequence(stints, null, null, null);

        Assert.That(ClubTurnSizes(turns), Is.EqualTo(new[] { 1, 2, 2 }));
    }

    [Test]
    public void REQ1203_BuildSequence_TenStints_AtTenThreshold_SplitsThreeThreeFour()
    {
        var stints = Enumerable.Range(0, 10).Select(i => Stint($"Club {i}", 2000 + i, 2001 + i)).ToArray();

        var turns = PathClueSequenceBuilder.BuildSequence(stints, null, null, null);

        Assert.That(ClubTurnSizes(turns), Is.EqualTo(new[] { 3, 3, 4 }));
    }

    [Test]
    public void REQ1203_BuildSequence_ElevenStints_AboveTenThreshold_SplitsThreeFourFour()
    {
        var stints = Enumerable.Range(0, 11).Select(i => Stint($"Club {i}", 2000 + i, 2001 + i)).ToArray();

        var turns = PathClueSequenceBuilder.BuildSequence(stints, null, null, null);

        Assert.That(ClubTurnSizes(turns), Is.EqualTo(new[] { 3, 4, 4 }));
    }

    [TestCase(3, TestName = "REQ1203_BuildSequence_TotalTurnCountIsAlwaysFixedAtSeven_N3")]
    [TestCase(4, TestName = "REQ1203_BuildSequence_TotalTurnCountIsAlwaysFixedAtSeven_N4")]
    [TestCase(5, TestName = "REQ1203_BuildSequence_TotalTurnCountIsAlwaysFixedAtSeven_N5")]
    [TestCase(10, TestName = "REQ1203_BuildSequence_TotalTurnCountIsAlwaysFixedAtSeven_N10")]
    [TestCase(11, TestName = "REQ1203_BuildSequence_TotalTurnCountIsAlwaysFixedAtSeven_N11")]
    [TestCase(23, TestName = "REQ1203_BuildSequence_TotalTurnCountIsAlwaysFixedAtSeven_N23")]
    public void REQ1203_BuildSequence_TotalTurnCountIsAlwaysFixedAtSeven(int stintCount)
    {
        var stints = Enumerable.Range(0, stintCount).Select(i => Stint($"Club {i}", 2000 + i, 2001 + i)).ToArray();

        var turns = PathClueSequenceBuilder.BuildSequence(stints, null, null, null);

        Assert.That(turns, Has.Count.EqualTo(PathClueSequenceBuilder.TotalTurns));
        Assert.That(turns, Has.Count.EqualTo(7));
    }

    [TestCase(3)]
    [TestCase(4)]
    [TestCase(11)]
    public void REQ1203_BuildSequence_EveryStintIsRevealed_NoneOmittedForHavingTooManyClubs(int stintCount)
    {
        var stints = Enumerable.Range(0, stintCount).Select(i => Stint($"Club {i}", 2000 + i, 2001 + i)).ToArray();

        var turns = PathClueSequenceBuilder.BuildSequence(stints, null, null, null);

        var totalClubsRevealed = turns.Where(t => t.Kind == PathClueKind.ClubReveal).Sum(t => t.Clubs!.Count);
        Assert.That(totalClubsRevealed, Is.EqualTo(stintCount));
    }

    // ---- REQ-1203: turn sizes are non-decreasing (first turn never larger -
    // than the last) ----------------------------------------------------

    [Test]
    public void REQ1203_BuildSequence_ClubTurnSizes_AreNonDecreasing()
    {
        var stints = Enumerable.Range(0, 11).Select(i => Stint($"Club {i}", 2000 + i, 2001 + i)).ToArray();

        var sizes = ClubTurnSizes(PathClueSequenceBuilder.BuildSequence(stints, null, null, null));

        Assert.That(sizes[0], Is.LessThanOrEqualTo(sizes[1]));
        Assert.That(sizes[1], Is.LessThanOrEqualTo(sizes[2]));
    }

    // ---- REQ-1203: appearance count present vs. unknown within a --------
    // multi-club turn -------------------------------------------------------

    [Test]
    public void REQ1203_BuildSequence_MultiClubTurn_AppearanceCountPresentForOneClub_UnknownForAnother_BothStillRevealed()
    {
        // N=4 -> 1-1-2 split: the third (final) club-reveal turn carries two
        // clubs, one with a known appearance count and one without.
        var stints = new[]
        {
            Stint("Club A", 2000, 2005),
            Stint("Club B", 2005, 2008),
            Stint("Club C", 2008, 2012, appearanceCount: 150),
            Stint("Club D", 2012, null, appearanceCount: null),
        };

        var turns = PathClueSequenceBuilder.BuildSequence(stints, null, null, null);

        var lastClubTurn = turns.Where(t => t.Kind == PathClueKind.ClubReveal).Last();
        Assert.That(lastClubTurn.Clubs, Has.Count.EqualTo(2));
        Assert.That(lastClubTurn.Clubs![0], Is.EqualTo(new PathClubClue("Club C", 150)));
        Assert.That(lastClubTurn.Clubs![1], Is.EqualTo(new PathClubClue("Club D", null)),
            "a club with no recorded appearance count is still revealed, without a count, never delayed or omitted");
    }

    // ---- REQ-1203: chronological order preserved across and within turns -

    [Test]
    public void REQ1203_BuildSequence_ClubOrder_IsChronologicalAcrossAndWithinTurns()
    {
        var stints = new[]
        {
            Stint("Earliest Club", 1999, 2003),
            Stint("Second Club", 2003, 2007),
            Stint("Third Club", 2007, 2011),
            Stint("Fourth Club", 2011, 2015),
            Stint("Latest Club", 2015, null),
        };

        var turns = PathClueSequenceBuilder.BuildSequence(stints, null, null, null);

        var clubNamesInOrder = turns
            .Where(t => t.Kind == PathClueKind.ClubReveal)
            .SelectMany(t => t.Clubs!)
            .Select(c => c.ClubName)
            .ToList();

        Assert.That(clubNamesInOrder, Is.EqualTo(new[] { "Earliest Club", "Second Club", "Third Club", "Fourth Club", "Latest Club" }));
    }

    // ---- REQ-1203: the bundled year-range clue -----------------------------

    [Test]
    public void REQ1203_BuildSequence_YearRangeTurn_IsOneBundledTurnCoveringEveryClub_InTheSameChronologicalOrder()
    {
        // REQ-1203's own worked example.
        var stints = new[]
        {
            Stint("Club A", 2012, 2015),
            Stint("Club B", 2015, 2019),
            Stint("Club C", 2019, null),
        };

        var turns = PathClueSequenceBuilder.BuildSequence(stints, null, null, null);

        var yearRangeTurns = turns.Where(t => t.Kind == PathClueKind.YearRange).ToList();
        Assert.That(yearRangeTurns, Has.Count.EqualTo(1), "exactly one bundled clue, never one clue per club");
        Assert.That(yearRangeTurns[0].TurnNumber, Is.EqualTo(4), "turn 4 — immediately after the 3 club-reveal turns");
        Assert.That(yearRangeTurns[0].YearRanges, Is.EqualTo(new[] { "2012-15", "2015-19", "2019-present" }));
    }

    [Test]
    public void REQ1203_BuildSequence_YearRangeTurn_CoversEveryStint_EvenWithMoreThanThreeClubs()
    {
        var stints = Enumerable.Range(0, 11).Select(i => Stint($"Club {i}", 2000 + i, 2001 + i)).ToArray();

        var turns = PathClueSequenceBuilder.BuildSequence(stints, null, null, null);

        var yearRangeTurn = turns.Single(t => t.Kind == PathClueKind.YearRange);
        Assert.That(yearRangeTurn.YearRanges, Has.Count.EqualTo(11));
    }

    // ---- REQ-1203: fixed position/nationality/age order --------------------

    [Test]
    public void REQ1203_BuildSequence_FinalThreeTurns_AreFixedOrderPositionThenNationalityThenAge()
    {
        var stints = new[] { Stint("Club A", 2010, 2013), Stint("Club B", 2013, 2016), Stint("Club C", 2016, null) };

        var turns = PathClueSequenceBuilder.BuildSequence(stints, "Forward", "France", 1990);

        Assert.That(turns[4].Kind, Is.EqualTo(PathClueKind.Position));
        Assert.That(turns[4].TurnNumber, Is.EqualTo(5));
        Assert.That(turns[4].TextValue, Is.EqualTo("Forward"));

        Assert.That(turns[5].Kind, Is.EqualTo(PathClueKind.Nationality));
        Assert.That(turns[5].TurnNumber, Is.EqualTo(6));
        Assert.That(turns[5].TextValue, Is.EqualTo("France"));

        Assert.That(turns[6].Kind, Is.EqualTo(PathClueKind.Age));
        Assert.That(turns[6].TurnNumber, Is.EqualTo(7));
        Assert.That(turns[6].TextValue, Is.EqualTo("1990"));
    }

    [Test]
    public void REQ1203_BuildSequence_EveryTurnKind_MatchesTheFixedSevenTurnShape_NoNationalTeamCapsClue()
    {
        // No PathClueKind value exists for national-team caps/appearances at
        // all (REQ-1203: "this clue type does not exist for xG Path") — this
        // asserts the full, exact Kind sequence the builder emits, which
        // structurally rules out any such clue ever appearing.
        var stints = new[] { Stint("Club A", 2010, 2013), Stint("Club B", 2013, 2016), Stint("Club C", 2016, null) };

        var turns = PathClueSequenceBuilder.BuildSequence(stints, "Forward", "France", 1990);

        Assert.That(turns.Select(t => t.Kind), Is.EqualTo(new[]
        {
            PathClueKind.ClubReveal, PathClueKind.ClubReveal, PathClueKind.ClubReveal,
            PathClueKind.YearRange, PathClueKind.Position, PathClueKind.Nationality, PathClueKind.Age,
        }));
    }

    // ---- REQ-1207: null position/nationality/age render as "not ----------
    // available," never a skipped turn ---------------------------------------

    [Test]
    public void REQ1207_BuildSequence_NullPositionNationalityBirthYear_RenderAsNotAvailable_NeverSkipTurns()
    {
        var stints = new[] { Stint("Club A", 2010, 2013), Stint("Club B", 2013, 2016), Stint("Club C", 2016, null) };

        var turns = PathClueSequenceBuilder.BuildSequence(stints, null, null, null);

        Assert.That(turns, Has.Count.EqualTo(7), "a data gap must never shrink the fixed 7-turn sequence");
        Assert.That(turns[4].TextValue, Is.EqualTo("not available"));
        Assert.That(turns[5].TextValue, Is.EqualTo("not available"));
        Assert.That(turns[6].TextValue, Is.EqualTo("not available"));
    }

    // ---- REQ-1203: the sequence halts immediately on a correct guess at ---
    // every possible point (GetRevealedTurnCount) ----------------------------
    // Flagged by the implementation's own comment as an inference beyond
    // literal REQ text (PathClueSequenceBuilder.GetRevealedTurnCount) — the
    // more literal "min(attemptsMade + 1, 7)" formula over-reveals by one
    // turn for a solved puzzle, so this is tested thoroughly at every
    // attempt count.

    [TestCase(0, 1)]
    [TestCase(1, 2)]
    [TestCase(2, 3)]
    [TestCase(3, 4)]
    [TestCase(4, 5)]
    [TestCase(5, 6)]
    [TestCase(6, 7)]
    public void REQ1203_GetRevealedTurnCount_NotYetCorrect_RevealsOneMoreTurnThanAttemptsMade(int attemptsMade, int expectedRevealed)
    {
        Assert.That(PathClueSequenceBuilder.GetRevealedTurnCount(attemptsMade, isCorrect: false), Is.EqualTo(expectedRevealed));
    }

    [Test]
    public void REQ1203_GetRevealedTurnCount_NotYetCorrect_AttemptsMadeEqualsCap_RevealedCountStaysCappedAtSeven()
    {
        // REQ-1205: 7 is the puzzle's own attempt cap — this must never
        // compute an 8th "revealed" turn once every attempt is exhausted.
        Assert.That(PathClueSequenceBuilder.GetRevealedTurnCount(7, isCorrect: false), Is.EqualTo(7));
    }

    [Test]
    public void REQ1203_GetRevealedTurnCount_NotYetCorrect_AttemptsMadeBeyondCap_DefensivelyStaysCappedAtSeven()
    {
        // Defensive bound only (a malformed/legacy Guess row) — attemptsMade
        // should never itself exceed 7 in normal play (REQ-1205's cap).
        Assert.That(PathClueSequenceBuilder.GetRevealedTurnCount(10, isCorrect: false), Is.EqualTo(7));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    public void REQ1203_GetRevealedTurnCount_SolvedAtThisAttempt_RevealedCountFreezesAtAttemptsMade_NeverRevealsTheNextTurn(int attemptsMade)
    {
        // Solved on attempt `attemptsMade`: the winning guess was submitted
        // while exactly `attemptsMade` turns were visible — no further turn
        // is ever revealed once solved.
        Assert.That(PathClueSequenceBuilder.GetRevealedTurnCount(attemptsMade, isCorrect: true), Is.EqualTo(attemptsMade));
    }

    // ---- S-163/ADR-0080: PathClubClue.IsLoan is wired through from --------
    // PathCareerStintFilter.IsInferredLoan — see that method's own doc ------
    // comment (PathCareerStintFilter.cs) for the inference rule itself; -----
    // this only confirms BuildSequence actually calls it per-stint with the -
    // full stintsChronological list, not that the heuristic itself is -------
    // correct (covered by PathCareerStintFilterTests instead) ---------------

    [Test]
    public void REQ1203_BuildSequence_LoanShapedFixture_WiresIsLoanThroughForContainedStintOnly()
    {
        // Man-Utd/Preston-shaped: one long-range stint, one short-range
        // stint fully inside it, different clubs — plus a third, unrelated,
        // non-overlapping stint that must NOT be flagged.
        var stints = new[]
        {
            Stint("Manchester United", 1992, 2003),
            Stint("Preston North End", 1994, 1995),
            Stint("LA Galaxy", 2003, 2007),
        };

        var turns = PathClueSequenceBuilder.BuildSequence(stints, null, null, null);

        var allClubClues = turns
            .Where(t => t.Kind == PathClueKind.ClubReveal)
            .SelectMany(t => t.Clubs!)
            .ToList();

        Assert.That(allClubClues.Single(c => c.ClubName == "Manchester United").IsLoan, Is.False);
        Assert.That(allClubClues.Single(c => c.ClubName == "Preston North End").IsLoan, Is.True);
        Assert.That(allClubClues.Single(c => c.ClubName == "LA Galaxy").IsLoan, Is.False);
    }

    // ---- S-162/ADR-0081: PathCareerStintFilter.CollapseAdjacentSameClub ----
    // BuildSequence itself does NOT call CollapseAdjacentSameClub — like
    // ExcludeNationalTeams/ExcludeBTeams, collapse is applied by the two real
    // callers (XGPathGameModule.GetEligiblePlayerIdsAsync, PathEndpoints.cs)
    // BEFORE BuildSequence ever sees a stint list, matching this class's own
    // header comment ("the caller ... is responsible for fetching the
    // PlayerCareerStint list" — BuildSequence stays a pure turn-splitter/
    // formatter with no filter-chain knowledge of its own). None of this
    // file's own fixtures above happen to contain an adjacent-same-club pair
    // (every stint list above uses distinct club names — "Club A"/"Club
    // B"/"Club {i}"/etc. — with no repeats), so no existing BuildSequence
    // test needed a collapse-specific case added to it.
    //
    // This one test is the exception: a small, still DB-free (both
    // CollapseAdjacentSameClub and BuildSequence are pure functions —
    // neither needs a repository or DbContext) wiring check that the two
    // functions compose correctly end-to-end, the same shape the real call
    // sites use (Collapse's output feeding directly into BuildSequence's
    // input) — living here rather than in XGPathGameModuleTests.cs because
    // it needs no DB/eligibility machinery at all, only the two pure
    // functions themselves.
    [Test]
    public void REQ1203_CollapseThenBuildSequence_AdjacentSameClubPair_RendersAsOneClubRevealEntry()
    {
        // Origi/Lille-shaped: two adjacent same-club rows with different,
        // both-known AppearanceCounts, plus one more stint elsewhere.
        var rawStints = new[]
        {
            Stint("Lille", 2015, 2017, appearanceCount: 40),
            Stint("Lille", 2017, 2020, appearanceCount: 33),
            Stint("Liverpool", 2020, null),
        };

        var collapsed = PathCareerStintFilter.CollapseAdjacentSameClub(rawStints);
        var turns = PathClueSequenceBuilder.BuildSequence(collapsed, null, null, null);

        var allClubClues = turns
            .Where(t => t.Kind == PathClueKind.ClubReveal)
            .SelectMany(t => t.Clubs!)
            .ToList();

        // One "Lille" entry, not two — the merged AppearanceCount is the sum
        // (73), and the total club count across all 3 club-reveal turns is 2
        // (Lille, Liverpool), not the raw 3 rows.
        Assert.That(allClubClues.Select(c => c.ClubName), Is.EqualTo(new[] { "Lille", "Liverpool" }));
        Assert.That(allClubClues.Single(c => c.ClubName == "Lille").AppearanceCount, Is.EqualTo(73));
    }
}

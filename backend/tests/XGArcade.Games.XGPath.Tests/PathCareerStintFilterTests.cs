using XGArcade.Data.Entities;

namespace XGArcade.Games.XGPath.Tests;

// REQ-1203, bug fix (2026-08-08): PathCareerStintFilter.ExcludeYouthNationalTeams
// / IsYouthNationalTeam — the read-time defensive filter for leftover
// pre-2026-08-02 youth/age-grade national-team PlayerCareerStint rows (see
// PathCareerStintFilter's own doc comment for the full "why a read-time
// filter, not a cleanup script" reasoning). Deliberately pure/DB-free, same
// precedent as PathClueSequenceBuilderTests.
public class PathCareerStintFilterTests
{
    private static PlayerCareerStint Stint(string clubName) =>
        new()
        {
            Id = Guid.NewGuid(),
            PlayerId = Guid.NewGuid(),
            ClubName = clubName,
            StartYear = 2010,
            EndYear = 2013,
        };

    // ---- REQ-1203: real, reported youth/age-grade national-team labels ----
    // are excluded ----------------------------------------------------------

    [TestCase("Spain national under-16 association football team")]
    [TestCase("Italy national under-20 football team")]
    [TestCase("Italy national under-21 football team")]
    [TestCase("France national under-19 football team")]
    [TestCase("England national under-17 football team")]
    public void REQ1203_IsYouthNationalTeam_ReportedAgeGradeNationalTeamLabels_ReturnsTrue(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsYouthNationalTeam(clubName), Is.True);
    }

    [Test]
    public void REQ1203_IsYouthNationalTeam_IsCaseInsensitive()
    {
        Assert.That(PathCareerStintFilter.IsYouthNationalTeam("SPAIN NATIONAL UNDER-16 ASSOCIATION FOOTBALL TEAM"), Is.True);
    }

    // ---- REQ-1203: the senior national team must NOT be excluded — it's a --
    // real, meaningful career milestone and showed correctly in the same -----
    // reported puzzle timeline the youth teams leaked into ------------------

    [TestCase("Italy men's national association football team")]
    [TestCase("Switzerland men's national football team")]
    [TestCase("Spain national football team")]
    public void REQ1203_IsYouthNationalTeam_SeniorNationalTeamLabels_ReturnsFalse(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsYouthNationalTeam(clubName), Is.False);
    }

    // ---- Scope: only what was actually reported — a non-FIFA regional side -
    // (seen in the same screenshots, not flagged as a problem) is left alone -

    [Test]
    public void REQ1203_IsYouthNationalTeam_NonFifaRegionalTeam_ReturnsFalse()
    {
        Assert.That(PathCareerStintFilter.IsYouthNationalTeam("Basque Country regional football team"), Is.False);
    }

    // ---- Real clubs, including ones with "United"/similar in the name, ----
    // must never be excluded ------------------------------------------------

    [TestCase("Manchester United")]
    [TestCase("AS Monaco")]
    [TestCase("Paris Saint-Germain")]
    [TestCase("Real Madrid")]
    [TestCase("Sporting Clube de Portugal")]
    public void REQ1203_IsYouthNationalTeam_RealClubNames_ReturnsFalse(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsYouthNationalTeam(clubName), Is.False);
    }

    // ---- Regression: without a leading \b before "national", the pattern --
    // would match "national" as a bare substring inside a longer word ------
    // (e.g. "Inter" + "national", "Multi" + "national"), wrongly excluding --
    // these even though none of them is an actual national team ------------

    [TestCase("International Under-20 Select XI")]
    [TestCase("FC International Milan Under-20")]
    [TestCase("Multinational Development Squad Under-19")]
    public void REQ1203_IsYouthNationalTeam_ClubNamesContainingNationalAsSubstring_ReturnsFalse(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsYouthNationalTeam(clubName), Is.False);
    }

    // ---- ExcludeYouthNationalTeams: filters a mixed stint list -------------

    [Test]
    public void REQ1203_ExcludeYouthNationalTeams_MixOfRealClubsAndYouthNationalTeams_KeepsOnlyRealClubs()
    {
        var stints = new[]
        {
            Stint("AS Monaco"),
            Stint("Spain national under-16 association football team"),
            Stint("Paris Saint-Germain"),
            Stint("Italy national under-21 football team"),
            Stint("Real Madrid"),
        };

        var filtered = PathCareerStintFilter.ExcludeYouthNationalTeams(stints);

        Assert.That(filtered.Select(s => s.ClubName), Is.EqualTo(new[] { "AS Monaco", "Paris Saint-Germain", "Real Madrid" }));
    }

    [Test]
    public void REQ1203_ExcludeYouthNationalTeams_KeepsSeniorNationalTeamAlongsideRealClubs()
    {
        var stints = new[]
        {
            Stint("AS Monaco"),
            Stint("Italy men's national association football team"),
            Stint("Italy national under-20 football team"),
        };

        var filtered = PathCareerStintFilter.ExcludeYouthNationalTeams(stints);

        Assert.That(filtered.Select(s => s.ClubName), Is.EqualTo(new[] { "AS Monaco", "Italy men's national association football team" }));
    }

    [Test]
    public void REQ1203_ExcludeYouthNationalTeams_OnlyYouthNationalTeams_ReturnsEmpty()
    {
        var stints = new[]
        {
            Stint("Spain national under-16 association football team"),
            Stint("Spain national under-17 association football team"),
        };

        var filtered = PathCareerStintFilter.ExcludeYouthNationalTeams(stints);

        Assert.That(filtered, Is.Empty);
    }

    [Test]
    public void REQ1203_ExcludeYouthNationalTeams_EmptyInput_ReturnsEmpty()
    {
        var filtered = PathCareerStintFilter.ExcludeYouthNationalTeams([]);

        Assert.That(filtered, Is.Empty);
    }

    [Test]
    public void REQ1203_ExcludeYouthNationalTeams_NoJunkRows_ReturnsAllUnchanged()
    {
        var stints = new[] { Stint("AS Monaco"), Stint("Paris Saint-Germain"), Stint("Real Madrid") };

        var filtered = PathCareerStintFilter.ExcludeYouthNationalTeams(stints);

        Assert.That(filtered, Is.EqualTo(stints));
    }
}

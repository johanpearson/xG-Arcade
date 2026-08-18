using XGArcade.Data.Entities;

namespace XGArcade.Games.XGPath.Tests;

// REQ-1203, bug fix (2026-08-08; broadened 2026-08-10, bug-bundle):
// PathCareerStintFilter.ExcludeNationalTeams / IsNationalTeam — the
// read-time defensive filter for leftover pre-2026-08-02 national-team
// PlayerCareerStint rows (see PathCareerStintFilter's own doc comment for
// the full "why a read-time filter, not a cleanup script" reasoning, and
// its 2026-08-10 scope-correction comment for why this now covers senior
// national teams too, not just youth/age-grade). Deliberately pure/DB-free,
// same precedent as PathClueSequenceBuilderTests.
//
// S-137/ADR-0073 note: the new BirthYear >= 1975 eligibility floor has no
// case here. Despite docs/backlog.md's S-137 entry naming this file, the
// actual implementation lives in XGPathGameModule.GetEligiblePlayerIdsAsync
// as a Player-level check (Player.BirthYear is a fact about the PLAYER, not
// about any individual PlayerCareerStint row), not inside
// PathCareerStintFilter — there is no stint-level concept for this rule to
// test here. See XGPathGameModuleTests' REQ1201_GenerateInstanceAsync_
// CandidateWithBirthYearAtFloor_IsEligible and its siblings.
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
    public void REQ1203_IsNationalTeam_ReportedAgeGradeNationalTeamLabels_ReturnsTrue(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsNationalTeam(clubName), Is.True);
    }

    [Test]
    public void REQ1203_IsNationalTeam_IsCaseInsensitive()
    {
        Assert.That(PathCareerStintFilter.IsNationalTeam("SPAIN NATIONAL UNDER-16 ASSOCIATION FOOTBALL TEAM"), Is.True);
    }

    // ---- Bug fix (2026-08-10, bug-bundle): the senior national team MUST --
    // now be excluded too — REQ-1203's own unqualified acceptance criterion -
    // makes no senior/youth distinction, and a 2026-08-10 bug report ---------
    // (screenshot: "Italy men's national association football team" with ---
    // "30 apps" leaking into a club-reveal clue) directly contradicted the --
    // 2026-08-08 fix's narrower judgment call. This flips the previous ------
    // "SeniorNationalTeamLabels_ReturnsFalse" assertion. -----------------

    [TestCase("Italy men's national association football team")]
    [TestCase("Switzerland men's national football team")]
    [TestCase("Spain national football team")]
    public void REQ1203_IsNationalTeam_SeniorNationalTeamLabels_ReturnsTrue(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsNationalTeam(clubName), Is.True);
    }

    // ---- Scope: a non-FIFA regional side (seen in the same screenshots, ---
    // not flagged as a problem) is still left alone — regional --------------
    // representative teams are NOT national teams -----------------------

    [Test]
    public void REQ1203_IsNationalTeam_NonFifaRegionalTeam_ReturnsFalse()
    {
        Assert.That(PathCareerStintFilter.IsNationalTeam("Basque Country regional football team"), Is.False);
    }

    // ---- Boundary fix (2026-08-10 follow-up, quality-gate finding): the ---
    // class doc comment previously overclaimed this filter "leaves non-FIFA -
    // regional representative sides alone" as if that were a general -------
    // FIFA-affiliation carve-out. It isn't — this filter has no way to know -
    // FIFA affiliation at all and matches purely on label wording. A -------
    // non-FIFA side whose Wikidata label nonetheless uses "national team" ---
    // phrasing (unlike the "regional" wording of the Basque Country case ----
    // above) IS excluded, same as any FIFA member. NOT verified against a --
    // live Wikidata query from this sandbox — "Catalonia national football --
    // team" is used as a plausible real label but is flagged for manual -----
    // confirmation rather than presented as a verified Wikidata fact --------

    [Test]
    public void REQ1203_IsNationalTeam_NonFifaButLabeledAsNationalTeam_ReturnsTrue()
    {
        Assert.That(PathCareerStintFilter.IsNationalTeam("Catalonia national football team"), Is.True);
    }

    // ---- Real clubs, including ones with "United"/similar in the name, ----
    // must never be excluded ------------------------------------------------

    [TestCase("Manchester United")]
    [TestCase("AS Monaco")]
    [TestCase("Paris Saint-Germain")]
    [TestCase("Real Madrid")]
    [TestCase("Sporting Clube de Portugal")]
    public void REQ1203_IsNationalTeam_RealClubNames_ReturnsFalse(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsNationalTeam(clubName), Is.False);
    }

    // ---- Regression: without a leading \b before "national", the pattern --
    // would match "national" as a bare substring inside a longer word ------
    // (e.g. "Inter" + "national", "Multi" + "national"), wrongly excluding --
    // these even though none of them is an actual national team ------------

    [TestCase("International Under-20 Select XI")]
    [TestCase("FC International Milan Under-20")]
    [TestCase("Multinational Development Squad Under-19")]
    public void REQ1203_IsNationalTeam_ClubNamesContainingNationalAsSubstring_ReturnsFalse(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsNationalTeam(clubName), Is.False);
    }

    // ---- Regression: a club literally named "National" (no accompanying --
    // "team" word) must not be excluded — the trailing \bteam\b requirement -
    // is what keeps this pattern from over-matching on the word "national" --
    // alone -------------------------------------------------------------

    [TestCase("National")]
    [TestCase("CD National")]
    public void REQ1203_IsNationalTeam_ClubNamedNationalWithoutTeamWord_ReturnsFalse(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsNationalTeam(clubName), Is.False);
    }

    // ---- ExcludeNationalTeams: filters a mixed stint list ------------------

    [Test]
    public void REQ1203_ExcludeNationalTeams_MixOfRealClubsAndYouthNationalTeams_KeepsOnlyRealClubs()
    {
        var stints = new[]
        {
            Stint("AS Monaco"),
            Stint("Spain national under-16 association football team"),
            Stint("Paris Saint-Germain"),
            Stint("Italy national under-21 football team"),
            Stint("Real Madrid"),
        };

        var filtered = PathCareerStintFilter.ExcludeNationalTeams(stints);

        Assert.That(filtered.Select(s => s.ClubName), Is.EqualTo(new[] { "AS Monaco", "Paris Saint-Germain", "Real Madrid" }));
    }

    [Test]
    public void REQ1203_ExcludeNationalTeams_ExcludesSeniorNationalTeamAlongsideYouthNationalTeams()
    {
        // Bug fix (2026-08-10, bug-bundle): this test replaces the previous
        // "KeepsSeniorNationalTeamAlongsideRealClubs" assertion — the senior
        // national team must now be excluded, same as the youth one.
        var stints = new[]
        {
            Stint("AS Monaco"),
            Stint("Italy men's national association football team"),
            Stint("Italy national under-20 football team"),
        };

        var filtered = PathCareerStintFilter.ExcludeNationalTeams(stints);

        Assert.That(filtered.Select(s => s.ClubName), Is.EqualTo(new[] { "AS Monaco" }));
    }

    [Test]
    public void REQ1203_ExcludeNationalTeams_OnlyNationalTeams_ReturnsEmpty()
    {
        var stints = new[]
        {
            Stint("Spain national under-16 association football team"),
            Stint("Spain national under-17 association football team"),
            Stint("Spain national football team"),
        };

        var filtered = PathCareerStintFilter.ExcludeNationalTeams(stints);

        Assert.That(filtered, Is.Empty);
    }

    [Test]
    public void REQ1203_ExcludeNationalTeams_EmptyInput_ReturnsEmpty()
    {
        var filtered = PathCareerStintFilter.ExcludeNationalTeams([]);

        Assert.That(filtered, Is.Empty);
    }

    [Test]
    public void REQ1203_ExcludeNationalTeams_NoJunkRows_ReturnsAllUnchanged()
    {
        var stints = new[] { Stint("AS Monaco"), Stint("Paris Saint-Germain"), Stint("Real Madrid") };

        var filtered = PathCareerStintFilter.ExcludeNationalTeams(stints);

        Assert.That(filtered, Is.EqualTo(stints));
    }

    // ==== S-139/ADR-0075: PathCareerStintFilter.IsBTeam/ExcludeBTeams — ====
    // ==== the B-team/reserve-team read-time filter, same shape/reasoning ===
    // ==== as IsNationalTeam/ExcludeNationalTeams above (see BTeamPattern's =
    // ==== own doc comment in PathCareerStintFilter.cs for the full ========
    // ==== false-positive analysis this section pins down as real tests) ===

    // ---- REQ-1203: each known reserve/B-team label shape is excluded ------

    [TestCase("Everton Reserves")] // explicit plural "Reserves" suffix
    [TestCase("Everton Reserve")] // singular "Reserve" suffix — "reserves?" makes the trailing s optional
    [TestCase("Barcelona B")] // bare "B" tier suffix, standalone word
    [TestCase("Bayern Munich II")] // bare "II" tier suffix, standalone word
    [TestCase("Manchester United U17")] // age-grade marker, low boundary of U1[7-9]
    [TestCase("Manchester United U20")] // age-grade marker, mid-range
    [TestCase("Manchester United U23")] // age-grade marker, high boundary of U2[0-3]
    [TestCase("Real Madrid Castilla")] // Real Madrid's Spanish-named reserve side
    [TestCase("Barcelona Atlètic")] // Catalan/Spanish reserve-side qualifier, accented spelling
    [TestCase("Barcelona Atletic")] // same qualifier, unaccented spelling — atl[eè]tic covers both
    public void REQ1203_IsBTeam_KnownReserveTeamLabelShapes_ReturnsTrue(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsBTeam(clubName), Is.True);
    }

    [Test]
    public void REQ1203_IsBTeam_IsCaseInsensitive()
    {
        Assert.That(PathCareerStintFilter.IsBTeam("REAL MADRID CASTILLA"), Is.True);
    }

    // ---- ADR-0075's false-positive check, as a real test rather than only -
    // a hand-traced code/ADR comment: every club name currently in --------
    // ReferenceDataSeeder.cs's Clubs array must never match. Read directly --
    // from that file (as of S-139/2026-08-18), not trusted secondhand. -----
    // This is 33 club names, matching PathCareerStintFilter.cs's and --------
    // ADR-0075's own "33-club" headline count (an earlier draft of both -----
    // undercounted this as "30-club" — since corrected, quality-gate finding).

    [TestCase("Real Madrid")]
    [TestCase("Barcelona")]
    [TestCase("Manchester United")]
    [TestCase("Manchester City")]
    [TestCase("Liverpool")]
    [TestCase("Arsenal")]
    [TestCase("Chelsea")]
    [TestCase("Bayern Munich")]
    [TestCase("Borussia Dortmund")]
    [TestCase("Juventus")]
    [TestCase("AC Milan")]
    [TestCase("Inter Milan")]
    [TestCase("Paris Saint-Germain")]
    [TestCase("Ajax")]
    [TestCase("Benfica")]
    [TestCase("Tottenham Hotspur")]
    [TestCase("Atletico Madrid")]
    [TestCase("Napoli")]
    [TestCase("AS Roma")]
    [TestCase("Sevilla")]
    [TestCase("Porto")]
    [TestCase("RB Leipzig")]
    [TestCase("Bayer Leverkusen")]
    [TestCase("Marseille")]
    [TestCase("Lyon")]
    [TestCase("Monaco")]
    [TestCase("Lille")]
    [TestCase("Lazio")]
    [TestCase("Valencia")]
    [TestCase("Real Sociedad")]
    [TestCase("Newcastle United")]
    [TestCase("West Ham United")]
    [TestCase("Celtic")]
    public void REQ1203_IsBTeam_CurrentSeededClubNames_ReturnsFalse(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsBTeam(clubName), Is.False);
    }

    // ---- ADR-0075 calls these two seeded clubs out by name as the closest -
    // near-misses — dedicated individual tests, not just buried in the ------
    // full-list parametrized test above, so a regression in either one -----
    // fails clearly and specifically -----------------------------------------

    [Test]
    public void REQ1203_IsBTeam_RBLeipzig_ReturnsFalse()
    {
        // "R" and "B" are adjacent word characters with no boundary between
        // them in "RB" — \bB\b never matches the "B" inside "RB". Only a
        // label with "B" as its own space-separated word (e.g. "Barcelona
        // B") matches.
        Assert.That(PathCareerStintFilter.IsBTeam("RB Leipzig"), Is.False);
    }

    [Test]
    public void REQ1203_IsBTeam_AtleticoMadrid_ReturnsFalse()
    {
        // The trailing \b fails inside "Atletico" — "c" and "o" are both
        // word characters with no boundary between them, so atl[eè]tic's
        // trailing \b never matches. Only a label with "atlètic"/"atletic"
        // as its own standalone final word (e.g. "Barcelona Atlètic")
        // matches.
        Assert.That(PathCareerStintFilter.IsBTeam("Atletico Madrid"), Is.False);
    }

    // ---- Boundary/negative regression: a real club whose name contains ----
    // one of BTeamPattern's tokens as a bare substring, not as its own -------
    // word, must never match -------------------------------------------------

    [TestCase("Athletic Bilbao")] // "B" is inside "Bilbao", not its own word; "Athletic" has an extra "h" so it never matches atl[eè]tic either ("Athletic" vs "atletic")
    [TestCase("Real Betis")] // "B" is inside "Betis", not its own word
    [TestCase("B36 Tórshavn")] // ADR-0075's own named theoretical risk case — "B" is immediately followed by the digit "3" with no word boundary between them, so bare B does not match this exact "B36" formatting
    public void REQ1203_IsBTeam_ClubNamesContainingBTeamTokensAsSubstring_ReturnsFalse(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsBTeam(clubName), Is.False);
    }

    // ---- ExcludeBTeams: filters a mixed stint list -------------------------

    [Test]
    public void REQ1203_ExcludeBTeams_MixOfRealClubsAndBTeams_KeepsOnlyRealClubs()
    {
        var stints = new[]
        {
            Stint("AS Monaco"),
            Stint("Real Madrid Castilla"),
            Stint("Paris Saint-Germain"),
            Stint("Barcelona B"),
            Stint("Real Madrid"),
        };

        var filtered = PathCareerStintFilter.ExcludeBTeams(stints);

        Assert.That(filtered.Select(s => s.ClubName), Is.EqualTo(new[] { "AS Monaco", "Paris Saint-Germain", "Real Madrid" }));
    }

    [Test]
    public void REQ1203_ExcludeBTeams_OnlyBTeams_ReturnsEmpty()
    {
        var stints = new[]
        {
            Stint("Everton Reserves"),
            Stint("Bayern Munich II"),
            Stint("Barcelona Atlètic"),
        };

        var filtered = PathCareerStintFilter.ExcludeBTeams(stints);

        Assert.That(filtered, Is.Empty);
    }

    [Test]
    public void REQ1203_ExcludeBTeams_EmptyInput_ReturnsEmpty()
    {
        var filtered = PathCareerStintFilter.ExcludeBTeams([]);

        Assert.That(filtered, Is.Empty);
    }

    [Test]
    public void REQ1203_ExcludeBTeams_NoJunkRows_ReturnsAllUnchanged()
    {
        var stints = new[] { Stint("AS Monaco"), Stint("Paris Saint-Germain"), Stint("Real Madrid") };

        var filtered = PathCareerStintFilter.ExcludeBTeams(stints);

        Assert.That(filtered, Is.EqualTo(stints));
    }

    // ---- Combined filtering: both call sites chain -------------------------
    // ExcludeBTeams(ExcludeNationalTeams(stints)) — both must run, together, -
    // excluding both a national-team row AND a B-team row from the same ------
    // mixed list ---------------------------------------------------------

    [Test]
    public void REQ1203_ExcludeBTeamsChainedWithExcludeNationalTeams_ExcludesBothNationalAndBTeamRows()
    {
        var stints = new[]
        {
            Stint("AS Monaco"),
            Stint("Spain national under-16 association football team"),
            Stint("Real Madrid Castilla"),
            Stint("Paris Saint-Germain"),
            Stint("Barcelona B"),
            Stint("Real Madrid"),
        };

        var filtered = PathCareerStintFilter.ExcludeBTeams(PathCareerStintFilter.ExcludeNationalTeams(stints));

        Assert.That(filtered.Select(s => s.ClubName), Is.EqualTo(new[] { "AS Monaco", "Paris Saint-Germain", "Real Madrid" }));
    }
}

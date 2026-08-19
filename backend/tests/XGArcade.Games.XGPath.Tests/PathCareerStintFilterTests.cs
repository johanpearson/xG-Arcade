using XGArcade.Data.Entities;

namespace XGArcade.Games.XGPath.Tests;

// REQ-1203, bug fix (2026-08-08; broadened 2026-08-10, bug-bundle;
// broadened again 2026-08-18, S-140):
// PathCareerStintFilter.ExcludeNationalTeams / IsNationalTeam — the
// read-time defensive filter for leftover pre-2026-08-02 national-team
// PlayerCareerStint rows (see PathCareerStintFilter's own doc comment for
// the full "why a read-time filter, not a cleanup script" reasoning, its
// 2026-08-10 scope-correction comment for why this now covers senior
// national teams too, not just youth/age-grade, and its 2026-08-18
// correction for why "regional" + "team"/"representative" phrasing is now
// excluded on the same principle as "national" + "team" phrasing).
// Deliberately pure/DB-free, same precedent as PathClueSequenceBuilderTests.
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

    // ---- Bug fix (2026-08-18, S-140): a non-FIFA REGIONAL representative --
    // side must be excluded on the same principle as a national one — the --
    // original regex excluded "Catalonia national football team" but not ---
    // "Basque Country regional football team" purely because the two ------
    // labels use different words ("national" vs. "regional"), not because -
    // of any deliberate distinction. This flips the previous ---------------
    // "NonFifaRegionalTeam_ReturnsFalse" assertion, which pinned the bug ---
    // as correct behavior. See PathCareerStintFilter.cs's own 2026-08-18 ---
    // doc-comment correction for the full reasoning. ------------------------

    [TestCase("Basque Country regional football team")]
    [TestCase("Basque Country regional representative team")]
    public void REQ1203_IsNationalTeam_NonFifaRegionalRepresentativeTeam_ReturnsTrue(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsNationalTeam(clubName), Is.True);
    }

    // ---- Boundary fix (2026-08-10 follow-up, quality-gate finding): the ---
    // class doc comment previously overclaimed this filter "leaves non-FIFA -
    // regional representative sides alone" as if that were a general -------
    // FIFA-affiliation carve-out. It isn't — this filter has no way to know -
    // FIFA affiliation at all and matches purely on label wording. A -------
    // non-FIFA side whose Wikidata label uses "national team" phrasing IS ---
    // excluded, same as any FIFA member (and, since S-140/2026-08-18, so is -
    // a non-FIFA side whose label instead uses "regional" + "team"/---------
    // "representative" phrasing — see the test above). NOT verified against -
    // a live Wikidata query from this sandbox — "Catalonia national --------
    // football team" is used as a plausible real label but is flagged for --
    // manual confirmation rather than presented as a verified Wikidata fact -

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

    // ---- Regression (2026-08-18, S-140): without a leading \b before ------
    // "regional", the broadened pattern would match "regional" as a bare ----
    // substring inside a longer word (e.g. "Inter" + "regional") — the ------
    // same false-positive risk NationalTeamPattern's own leading \b before --
    // "national" already guards against, above ------------------------------

    [TestCase("Interregional Development Squad Team")]
    public void REQ1203_IsNationalTeam_ClubNamesContainingRegionalAsSubstring_ReturnsFalse(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsNationalTeam(clubName), Is.False);
    }

    // ---- Regression (2026-08-18, S-140): a club literally named "Regional" -
    // (no accompanying "team"/"representative" word) must not be excluded — -
    // the trailing \b(?:team|representative)\b requirement is what keeps ---
    // this pattern from over-matching on the word "regional" alone, the -----
    // same discipline the "National"/"CD National" test above applies to ---
    // the "national" alternative -------------------------------------------

    [TestCase("Regional")]
    [TestCase("CD Regional")]
    public void REQ1203_IsNationalTeam_ClubNamedRegionalWithoutTeamOrRepresentativeWord_ReturnsFalse(string clubName)
    {
        Assert.That(PathCareerStintFilter.IsNationalTeam(clubName), Is.False);
    }

    // ---- ADR-0075's false-positive check, run against IsNationalTeam too --
    // (not just IsBTeam below), now that NationalTeamPattern also matches ---
    // "regional" + "team"/"representative": every club name currently in ---
    // ReferenceDataSeeder.cs's Clubs array must never match. Read directly --
    // from that file (as of S-140/2026-08-18, unchanged from S-139's own ----
    // 33-club count) — none contain "national," "regional," "team," or ------
    // "representative" as their own word. ------------------------------------

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
    public void REQ1203_IsNationalTeam_CurrentSeededClubNames_ReturnsFalse(string clubName)
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

    // ==== S-163/ADR-0080: PathCareerStintFilter.IsInferredLoan — the ======
    // ==== date-range-containment loan heuristic (see that method's own ====
    // ==== doc comment in PathCareerStintFilter.cs for the exact rule, its =
    // ==== two ongoing-stint edge cases, and the identical-range-different- =
    // ==== club decision this section pins down as real tests) =============

    private static PlayerCareerStint StintWithRange(string clubName, int startYear, int? endYear) =>
        new()
        {
            Id = Guid.NewGuid(),
            PlayerId = Guid.NewGuid(),
            ClubName = clubName,
            StartYear = startYear,
            EndYear = endYear,
        };

    [Test]
    public void REQ1203_IsInferredLoan_FullyContainedDifferentClubStint_ReturnsTrue()
    {
        // Beckham-shaped: Man Utd 1992-2003 fully contains Preston 1994-95.
        var manUtd = StintWithRange("Manchester United", 1992, 2003);
        var preston = StintWithRange("Preston North End", 1994, 1995);
        var allStints = new[] { manUtd, preston };

        Assert.That(PathCareerStintFilter.IsInferredLoan(preston, allStints), Is.True);
    }

    [Test]
    public void REQ1203_IsInferredLoan_PartialOverlapOnly_ReturnsFalse()
    {
        // Overlaps 2011-2013 but neither stint's range fully contains the
        // other's — a real overlapping-transfer-window shape, not a loan.
        var clubA = StintWithRange("Club A", 2010, 2013);
        var clubB = StintWithRange("Club B", 2011, 2015);
        var allStints = new[] { clubA, clubB };

        Assert.That(PathCareerStintFilter.IsInferredLoan(clubA, allStints), Is.False);
        Assert.That(PathCareerStintFilter.IsInferredLoan(clubB, allStints), Is.False);
    }

    [Test]
    public void REQ1203_IsInferredLoan_NoOverlapAtAll_ReturnsFalse()
    {
        var clubA = StintWithRange("Club A", 2010, 2013);
        var clubB = StintWithRange("Club B", 2013, 2016);
        var allStints = new[] { clubA, clubB };

        Assert.That(PathCareerStintFilter.IsInferredLoan(clubA, allStints), Is.False);
        Assert.That(PathCareerStintFilter.IsInferredLoan(clubB, allStints), Is.False);
    }

    [Test]
    public void REQ1203_IsInferredLoan_IdenticalRangeDifferentClub_ReturnsTrue()
    {
        // Documented decision (PathCareerStintFilter.IsInferredLoan's own
        // doc comment, edge case 3): the contract's non-strict <=/>=
        // comparisons mean an identical date range on a different club DOES
        // satisfy containment, symmetrically for both stints. This is a
        // deliberate, documented trade-off, not an oversight — presentation-
        // only, no eligibility impact.
        var clubA = StintWithRange("Club A", 2010, 2013);
        var clubB = StintWithRange("Club B", 2010, 2013);
        var allStints = new[] { clubA, clubB };

        Assert.That(PathCareerStintFilter.IsInferredLoan(clubA, allStints), Is.True);
        Assert.That(PathCareerStintFilter.IsInferredLoan(clubB, allStints), Is.True);
    }

    [Test]
    public void REQ1203_IsInferredLoan_CandidateStintItselfOngoing_ReturnsFalse()
    {
        // Conservative rule: an ongoing stint (EndYear: null) is never
        // itself flagged as contained, even though a different, wider,
        // also-ongoing stint started earlier — it might yet outlast it.
        var wideOngoing = StintWithRange("Club A", 2000, null);
        var candidateOngoing = StintWithRange("Club B", 2010, null);
        var allStints = new[] { wideOngoing, candidateOngoing };

        Assert.That(PathCareerStintFilter.IsInferredLoan(candidateOngoing, allStints), Is.False);
    }

    [Test]
    public void REQ1203_IsInferredLoan_ContainingStintIsOngoing_EarlierEndedCandidate_ReturnsTrue()
    {
        // The CONTAINING stint being ongoing (EndYear: null) CAN still mark
        // an earlier-ended stint at a different club as a loan — an
        // open-ended stint that started before the candidate necessarily
        // still "covers" it today.
        var ongoingParentClub = StintWithRange("Parent Club", 2005, null);
        var earlierEndedLoan = StintWithRange("Loan Club", 2010, 2011);
        var allStints = new[] { ongoingParentClub, earlierEndedLoan };

        Assert.That(PathCareerStintFilter.IsInferredLoan(earlierEndedLoan, allStints), Is.True);
    }

    [Test]
    public void REQ1203_IsInferredLoan_NoOtherStints_ReturnsFalse()
    {
        var onlyStint = StintWithRange("Club A", 2010, 2013);

        Assert.That(PathCareerStintFilter.IsInferredLoan(onlyStint, new[] { onlyStint }), Is.False);
    }

    [Test]
    public void REQ1203_IsInferredLoan_SameClubDifferentStintRecords_NeverSelfFlagged()
    {
        // A same-named club appearing twice (e.g. two separate spells) must
        // never count as "a different club" containing the other, even if
        // their ranges would otherwise satisfy containment.
        var firstSpell = StintWithRange("Club A", 2000, 2020);
        var secondSpellSameClub = StintWithRange("Club A", 2005, 2010);
        var allStints = new[] { firstSpell, secondSpellSameClub };

        Assert.That(PathCareerStintFilter.IsInferredLoan(secondSpellSameClub, allStints), Is.False);
    }
}

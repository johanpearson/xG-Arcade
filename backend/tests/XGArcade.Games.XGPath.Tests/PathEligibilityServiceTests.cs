using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGPath.Tests;

// REQ-1201 (target-player eligibility) — docs/requirements-document.md
// §4.12. S-154 (pure refactor, no behavior change, docs/backlog.md Epic 17):
// split out of XGPathGameModuleTests.cs alongside PathEligibilityService
// itself, following the same precedent
// docs/decisions/0068-grid-game-module-responsibility-split.md set for
// GridGenerationServiceTests.cs. Every test here exercises
// IPathEligibilityService directly against a freshly-constructed
// PathEligibilityService (composed with its own real IPlayerFamiliarityService
// fake — a hand-rolled fake, this repo's no-mocking-framework convention),
// rather than going through XGPathGameModule.GenerateInstanceAsync — the
// "fakes/mocks only construct the one class under test" convention S-106/
// S-107 established for the IPlayerStoreRepository split (ADR-0067).
//
// Reshaping note: the moved tests below assert directly on
// GetEligiblePlayerIdsAsync's own returned id list (Does.Contain/
// Does.Not.Contain) rather than on XGPathGameModule.GenerateInstanceAsync's
// "insufficient pool -> PathGenerationException" side effect the original,
// pre-split tests used as an indirect proxy for eligibility — a legitimate
// narrowing to the unit actually under test, same "reshaped to assert
// directly on the narrower unit's own contract" allowance ADR-0068's own
// Decision section describes for a handful of its own REQ-211 tests.
// Fixture setup (SeedClub/SeedPlayer/SeedEligiblePlayer/SeedStints/etc.) is
// otherwise unchanged from XGPathGameModuleTests.cs.
public class PathEligibilityServiceTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    // S-106/S-107 (pure refactor): the sibling repositories carrying the
    // methods split out of the original, now-deleted IPlayerStoreRepository
    // — see ADR-0067. _playerCareerStintRepository carries
    // GetCareerStintCandidatePlayerIdsAsync/GetCareerStintsByPlayerIdsAsync.
    private IPlayerCareerStintRepository _playerCareerStintRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private ICategoryValueRepository _categoryValueRepository = null!;
    private FakePlayerFamiliarityService _playerFamiliarityService = null!;
    private PathEligibilityService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerCareerStintRepository = new PlayerCareerStintRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _playerFamiliarityService = new FakePlayerFamiliarityService();
        _service = new PathEligibilityService(
            _playerCareerStintRepository, _playerRepository, _categoryValueRepository, _playerFamiliarityService);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private void SeedClub(string name)
    {
        if (_dbContext.ClubDefinitions.Any(c => c.Name == name))
            return;

        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = $"Qclub-{name}" });
        _dbContext.SaveChanges();
    }

    // REQ-1201/ADR-0073/S-137: birthYear defaults to 1990 (safely >=
    // PathEligibilityService.MinBirthYear's 1975 floor), not left at Player.
    // BirthYear's own null default — every pre-existing "this candidate
    // should be eligible" fixture in this file was written before the
    // BirthYear>=1975 filter existed and relies on SeedPlayer/
    // SeedEligiblePlayer producing an eligible player by default.
    // Overridable per test for the BirthYear-specific cases (1975 boundary,
    // 1974, null) this default is designed to keep untouched.
    //
    // REQ-1201/ADR-0079/S-161: position defaults to "Forward" for the exact
    // same reason birthYear defaults to 1990 above — every pre-existing
    // "this candidate should be eligible" fixture predates the
    // Position != null/empty floor and relies on this helper producing an
    // eligible player by default. Overridable per test for the
    // Position-specific cases (non-null control, null) this default is
    // designed to keep untouched.
    private Player SeedPlayer(string name, int? birthYear = 1990, string? position = "Forward")
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = name, WikidataQid = $"Qplayer-{name}", BirthYear = birthYear, Position = position };
        _dbContext.Players.Add(player);
        _dbContext.SaveChanges();
        return player;
    }

    // Seeds `stints` PlayerCareerStint rows for playerId. SequenceOrder is
    // irrelevant to eligibility (IsEligible reads only StartYear/EndYear/
    // ClubName/AppearanceCount), so every fixture row is left at 0 rather
    // than replicating AddCareerStintsAsync's own re-sequencing logic here.
    // AppearanceCount defaults to null ("unknown"), which ADR-0047 treats
    // as passing the appearance-count check — most fixtures don't need to
    // set it explicitly.
    private void SeedStints(Guid playerId, params (int StartYear, int? EndYear, string ClubName)[] stints)
    {
        SeedStints(playerId, stints.Select(s => (s.StartYear, s.EndYear, s.ClubName, (int?)null)).ToArray());
    }

    private void SeedStints(Guid playerId, params (int StartYear, int? EndYear, string ClubName, int? AppearanceCount)[] stints)
    {
        foreach (var (startYear, endYear, clubName, appearanceCount) in stints)
        {
            _dbContext.PlayerCareerStints.Add(new PlayerCareerStint
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                ClubName = clubName,
                StartYear = startYear,
                EndYear = endYear,
                SequenceOrder = 0,
                AppearanceCount = appearanceCount,
            });
        }
        _dbContext.SaveChanges();
    }

    // S-162/ADR-0081: same shape as SeedStints above, but with explicit,
    // caller-controlled SequenceOrder values (0, 1, 2, ... matching each
    // tuple's position in the params array) rather than SeedStints' fixed
    // SequenceOrder=0 for every row. PathCareerStintFilter.CollapseAdjacentSameClub
    // defines "adjacent" purely as "next to each other after sorting by
    // SequenceOrder" (its own doc comment's precondition) — the plain
    // SeedStints helper's "SequenceOrder is irrelevant to eligibility" claim
    // stopped being true the moment collapse joined
    // GetEligiblePlayerIdsAsync's filter chain, so collapse-specific
    // fixtures need real, distinct SequenceOrder values.
    private void SeedStintsOrdered(Guid playerId, params (int StartYear, int? EndYear, string ClubName, int? AppearanceCount)[] stints)
    {
        for (var i = 0; i < stints.Length; i++)
        {
            var (startYear, endYear, clubName, appearanceCount) = stints[i];
            _dbContext.PlayerCareerStints.Add(new PlayerCareerStint
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                ClubName = clubName,
                StartYear = startYear,
                EndYear = endYear,
                SequenceOrder = i,
                AppearanceCount = appearanceCount,
            });
        }
        _dbContext.SaveChanges();
    }

    // Baseline "definitely eligible" fixture (REQ-1201/ADR-0074/S-138): 3
    // well-ordered stints, at 2 DISTINCT seeded clubs (seededClubName and a
    // second club derived from it, "{seededClubName} 2") plus 1 unseeded
    // club — satisfies the "≥2 distinct qualifying seeded clubs" rule. The
    // second club is registered here, not by the caller, via the now-
    // idempotent SeedClub above. birthYear/position forward to SeedPlayer's
    // own default/override.
    private Player SeedEligiblePlayer(string name, string seededClubName, int? birthYear = 1990, string? position = "Forward")
    {
        var secondSeededClubName = $"{seededClubName} 2";
        SeedClub(secondSeededClubName);

        var player = SeedPlayer(name, birthYear, position);
        SeedStints(player.Id,
            (2010, 2013, seededClubName),
            (2013, 2016, secondSeededClubName),
            (2016, null, "Another Unseeded Club"));
        return player;
    }

    // ---- REQ-1201/ADR-0074/S-138: the "≥2 distinct qualifying seeded --------
    // clubs" structural rule -------------------------------------------------

    // REQ-1201/ADR-0074/S-138: the old "≥3 documented stint rows" rule is
    // gone, replaced by "≥2 DISTINCT qualifying seeded clubs" — this fixture
    // (1 seeded-club stint, 1 non-seeded) isolates that rule: exactly 1
    // qualifying seeded club is not enough.
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithOnlyOneQualifyingSeededClub_NeverSelected()
    {
        SeedClub("Seeded FC");
        var oneSeededClub = SeedPlayer("OneSeededClub");
        SeedStints(oneSeededClub.Id, (2010, 2013, "Seeded FC"), (2013, null, "Other FC")); // only 1 qualifying seeded club

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(oneSeededClub.Id));
    }

    // REQ-1203/ADR-0074/S-138 (architecture-review finding, not the
    // original backlog text): 2 distinct qualifying seeded clubs alone is
    // NOT enough — a candidate whose ONLY documented stints are exactly
    // those 2 qualifying clubs (2 total rows, no third stint of any kind)
    // must still be rejected. Dropping the old total-stint-row floor
    // entirely (as the original S-138 backlog text assumed was safe once
    // the 2-club rule existed) would let this candidate through, and
    // PathClueSequenceBuilder.SplitIntoTurns(2) produces club-reveal turn
    // sizes [0, 1, 1] — an empty first clue turn, a real player-facing bug.
    // MinDocumentedStintCount (3) exists specifically to keep this
    // candidate excluded.
    [Test]
    public async Task REQ1203_GetEligiblePlayerIdsAsync_CandidateWithTwoQualifyingSeededClubsButOnlyTwoTotalStints_NeverSelected()
    {
        SeedClub("Seeded FC");
        SeedClub("Seeded FC 2");
        var onlyTwoStints = SeedPlayer("OnlyTwoStints");
        SeedStints(onlyTwoStints.Id, (2010, 2013, "Seeded FC"), (2013, null, "Seeded FC 2")); // 2 qualifying seeded clubs, but only 2 total rows

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(onlyTwoStints.Id));
    }

    // Bug fix (2026-08-08, REQ-1203; rule updated REQ-1201/ADR-0074/S-138):
    // a player whose only REAL documented club stint is at one seeded club
    // must never become eligible purely because leftover pre-2026-08-02
    // youth-national-team junk rows are also present — see
    // PathCareerStintFilter's own doc comment. 2 real stints (only 1 at the
    // seeded club) + 2 youth-national junk rows, none of which are seeded
    // clubs either, so the qualifying-seeded-club count is 1 regardless of
    // whether the junk rows are filtered.
    [Test]
    public async Task REQ1203_GetEligiblePlayerIdsAsync_CandidateWithTwoRealStintsPaddedByYouthNationalTeamJunkRows_NeverSelected()
    {
        SeedClub("Seeded FC");
        var paddedByJunk = SeedPlayer("PaddedByJunk");
        SeedStints(paddedByJunk.Id,
            (2010, 2013, "Seeded FC"),
            (2013, null, "Other FC"),
            (2005, 2007, "Spain national under-16 association football team"),
            (2007, 2009, "Italy national under-21 football team"));

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(paddedByJunk.Id));
    }

    // Positive control for the fix above (fixture updated REQ-1201/
    // ADR-0074/S-138 to carry 2 distinct qualifying seeded clubs, the
    // current eligibility rule): a candidate with a genuinely eligible
    // career must still be selected even when leftover youth-national-team
    // junk rows are ALSO present — the filter must not accidentally reject
    // a real candidate just because junk rows exist alongside their real
    // career data.
    //
    // Bug fix (S-139/ADR-0075, test-only): the third real stint below is
    // named "Unseeded Club Two", not "...Club B" — ExcludeBTeams is chained
    // alongside ExcludeNationalTeams at this exact call site
    // (PathEligibilityService.GetEligiblePlayerIdsAsync), and BTeamPattern's
    // own bare "B" alternative would match a trailing standalone "B" word
    // (ADR-0075's own flagged "bare B/II token... real false-positive risk"
    // concern), silently filtering out this test's own "real" stint.
    [Test]
    public async Task REQ1203_GetEligiblePlayerIdsAsync_CandidateWithTwoQualifyingSeededClubStints_StillEligible_DespiteYouthNationalTeamJunkRows()
    {
        SeedClub("Seeded FC");
        SeedClub("Seeded FC 2");
        var withJunk = SeedPlayer("RealCareerPlusJunk");
        SeedStints(withJunk.Id,
            (2010, 2013, "Seeded FC"),
            (2013, 2016, "Seeded FC 2"),
            (2016, null, "Unseeded Club Two"),
            (2005, 2007, "Spain national under-17 association football team"));

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Contain(withJunk.Id));
    }

    // S-139/ADR-0075: same "must never count toward eligibility" bug class as
    // the youth-national-team junk-row tests above, now for B-team/
    // reserve-team junk rows (PathCareerStintFilter.ExcludeBTeams, chained
    // alongside ExcludeNationalTeams). 2 real stints (only 1 at the seeded
    // club) + 2 B-team junk rows, neither a seeded club, so the
    // qualifying-seeded-club count is 1 regardless of whether the junk rows
    // are filtered.
    [Test]
    public async Task REQ1203_GetEligiblePlayerIdsAsync_CandidateWithTwoRealStintsPaddedByBTeamJunkRows_NeverSelected()
    {
        SeedClub("Seeded FC");
        var paddedByJunk = SeedPlayer("PaddedByBTeamJunk");
        SeedStints(paddedByJunk.Id,
            (2010, 2013, "Seeded FC"),
            (2013, null, "Other FC"),
            (2005, 2007, "Real Madrid Castilla"),
            (2007, 2009, "Barcelona B"));

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(paddedByJunk.Id));
    }

    // Positive control for the fix above: a candidate with a genuinely
    // eligible career must still be selected even when leftover B-team/
    // reserve-team junk rows are ALSO present. The third real stint is
    // deliberately named "Unseeded Club Two", NOT "...Club B" like the
    // sibling youth-national-team version of this test uses — BTeamPattern's
    // own bare-"B" alternative would now match a trailing standalone "B"
    // word (see "Barcelona B" in PathCareerStintFilterTests.cs), so reusing
    // that name here would wrongly filter out this test's own "real"
    // fixture row.
    [Test]
    public async Task REQ1203_GetEligiblePlayerIdsAsync_CandidateWithTwoQualifyingSeededClubStints_StillEligible_DespiteBTeamJunkRows()
    {
        SeedClub("Seeded FC");
        SeedClub("Seeded FC 2");
        var withJunk = SeedPlayer("RealCareerPlusBTeamJunk");
        SeedStints(withJunk.Id,
            (2010, 2013, "Seeded FC"),
            (2013, 2016, "Seeded FC 2"),
            (2016, null, "Unseeded Club Two"),
            (2005, 2007, "Bayern Munich II"));

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Contain(withJunk.Id));
    }

    // ==== S-162/ADR-0081: PathCareerStintFilter.CollapseAdjacentSameClub ===
    // ==== joins the same GetEligiblePlayerIdsAsync filter chain as ========
    // ==== ExcludeNationalTeams/ExcludeBTeams above, applied AFTER both =====

    // Same bug class as PaddedByYouthNationalTeamJunkRows/PaddedByBTeamJunkRows
    // above, but the OPPOSITE direction of risk: here the candidate's RAW row
    // count (3) meets MinDocumentedStintCount on its own, but two of those
    // three rows are an ADJACENT same-club run ("Seeded FC" then "Seeded FC"
    // again, nothing else between them) that collapses to ONE displayed
    // entry — so the POST-COLLAPSE distinct-chapter count is only 2, below
    // MinDocumentedStintCount. If collapse were applied only at the display
    // call site (PathEndpoints.cs) and not here, this candidate would be
    // wrongly accepted as eligible with too few real post-collapse stints for
    // PathClueSequenceBuilder.SplitIntoTurns to split across its 3 fixed
    // club-reveal turns — exactly the "empty clue" bug class ADR-0074 already
    // fixed once for a different cause (see GetEligiblePlayerIdsAsync's own
    // INVARIANT comment).
    [Test]
    public async Task REQ1203_GetEligiblePlayerIdsAsync_CandidateWithThreeRawStintsButTwoPostCollapse_NeverSelected()
    {
        SeedClub("Seeded FC");
        SeedClub("Seeded FC 2");
        var collapsesTooFar = SeedPlayer("CollapsesTooFar");
        SeedStintsOrdered(collapsesTooFar.Id,
            (2010, 2012, "Seeded FC", (int?)null),
            (2012, 2015, "Seeded FC", (int?)null), // adjacent, same club as the row above -> collapses into ONE row with it
            (2015, null, "Seeded FC 2", (int?)null));
        // Raw row count: 3 (meets MinDocumentedStintCount on its own).
        // Post-collapse row count: 2 (the two "Seeded FC" rows merge) -> below
        // MinDocumentedStintCount, so this candidate must still be rejected.

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(collapsesTooFar.Id));
    }

    // Positive control for the fix above: a genuinely eligible candidate
    // whose real career happens to include an adjacent same-club pair (e.g.
    // Origi's real "Lille" shape) must still be selected — collapsing must
    // not shrink an otherwise-eligible candidate's pool membership below what
    // it should be. Post-collapse this candidate has exactly 3 distinct
    // chapters (Seeded FC merged, Seeded FC 2, Unseeded Club Two) at 2
    // distinct qualifying seeded clubs — genuinely eligible.
    [Test]
    public async Task REQ1203_GetEligiblePlayerIdsAsync_CandidateWithAdjacentSameClubPair_StillEligible_PoolDoesNotShrinkBelowPuzzleCount()
    {
        SeedClub("Seeded FC");
        SeedClub("Seeded FC 2");
        var adjacentPair = SeedPlayer("AdjacentSameClubPair");
        SeedStintsOrdered(adjacentPair.Id,
            (2005, 2008, "Seeded FC", (int?)null),
            (2008, 2010, "Seeded FC", (int?)null), // adjacent, same club -> collapses with the row above into one "Seeded FC" chapter
            (2010, 2013, "Seeded FC 2", (int?)null),
            (2013, null, "Unseeded Club Two", (int?)null));

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Contain(adjacentPair.Id));
    }

    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithUndeterminableStintOrder_NeverSelected()
    {
        SeedClub("Seeded FC");
        SeedClub("Seeded FC 2");
        // 2 distinct qualifying seeded clubs (REQ-1201/ADR-0074/S-138) so
        // this fixture isolates the order-check failure specifically —
        // without the duplicate dates below, this candidate would otherwise
        // be eligible on club count alone. Two stints share the identical
        // (StartYear=2010, EndYear=2013) pair — their relative chronological
        // order can't be derived from the dates themselves, only from
        // write-order SequenceOrder.
        var undeterminable = SeedPlayer("DuplicateDates");
        SeedStints(undeterminable.Id,
            (2010, 2013, "Seeded FC"),
            (2010, 2013, "Seeded FC 2"),
            (2016, null, "Yet Another Club"));

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(undeterminable.Id));
    }

    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithTwoSimultaneouslyOngoingStints_NeverSelected()
    {
        SeedClub("Seeded FC");
        SeedClub("Seeded FC 2");
        // 2 distinct qualifying seeded clubs (REQ-1201/ADR-0074/S-138) so
        // this fixture isolates the order-check failure specifically — see
        // the comment above on the sibling "undeterminable stint order"
        // test. Both stints start in 2010 and are still "ongoing"
        // (EndYear null) — an identical (StartYear, EndYear) pair even
        // though EndYear is null on both sides (design decision: null must
        // compare equal to null here, not be treated as "never a
        // duplicate").
        var twoOngoing = SeedPlayer("TwoOngoingStints");
        SeedStints(twoOngoing.Id,
            (2010, null, "Seeded FC"),
            (2010, null, "Seeded FC 2"),
            (2016, 2018, "Yet Another Club"));

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(twoOngoing.Id));
    }

    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithNoStintAtSeededClub_NeverSelected()
    {
        SeedClub("Seeded FC");
        var noSeededClub = SeedPlayer("NoSeededClub");
        SeedStints(noSeededClub.Id,
            (2010, 2013, "Unseeded Club A"),
            (2013, 2016, "Unseeded Club B"),
            (2016, null, "Unseeded Club C"));

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(noSeededClub.Id));
    }

    // ADR-0047/S-138: a seeded-club stint with a known, sub-threshold
    // appearance count doesn't count as a QUALIFYING seeded club — a
    // one-off loan/fringe appearance shouldn't be enough to make an
    // otherwise-obscure player a valid target. This candidate has 2 seeded
    // clubs, but only ONE of them (the 25-appearance one) individually
    // qualifies, so the qualifying count is 1 — still below
    // MinQualifyingSeededClubs (2).
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithSeededClubStintBelowAppearanceThreshold_NeverSelected()
    {
        SeedClub("Seeded FC");
        SeedClub("Seeded FC 2");
        var fringeAppearance = SeedPlayer("FringeAppearance");
        SeedStints(fringeAppearance.Id,
            (2010, 2011, "Seeded FC", 19), // one below the 20-appearance threshold — doesn't qualify
            (2011, 2014, "Seeded FC 2", 25), // qualifies — only 1 of the 2 seeded clubs does
            (2014, null, "Unseeded Club B", (int?)null));

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(fringeAppearance.Id));
    }

    // ADR-0047: a known appearance count meeting the threshold exactly is
    // still eligible — the check is ">=", not ">". Fixture carries a SECOND
    // qualifying seeded club (unknown appearance count) alongside the
    // at-threshold one, since a single qualifying club is no longer enough
    // on its own.
    //
    // Bug fix (S-139/ADR-0075, test-only): the extra stint below is named
    // "Unseeded Club Two", not "...Club B" — see the identical rename/reason
    // on the sibling YouthNationalTeamJunkRows test above.
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithSeededClubStintAtAppearanceThreshold_IsEligible()
    {
        SeedClub("Seeded FC");
        SeedClub("Seeded FC 2");
        var atThreshold = SeedPlayer("AtThreshold");
        SeedStints(atThreshold.Id,
            (2010, 2011, "Seeded FC", 20), // exactly the threshold — qualifies
            (2011, 2014, "Seeded FC 2", (int?)null), // unknown count — also qualifies
            (2014, null, "Unseeded Club Two", (int?)null));

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Contain(atThreshold.Id));
    }

    // ADR-0047: an unknown appearance count (Wikidata's P1350 qualifier
    // absent) is not evidence of a fringe appearance, so it still passes —
    // only a known, sub-threshold count disqualifies a stint. Fixture
    // carries a SECOND qualifying seeded club alongside the unknown-count
    // one, plus an extra unseeded stint required to reach
    // MinDocumentedStintCount (3), since only 2 stints qualify as seeded
    // clubs.
    //
    // Bug fix (S-139/ADR-0075, test-only): the extra stint below is named
    // "Unseeded Club Two", not "...Club B" — same rename/reason as the two
    // sibling tests above.
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithSeededClubStintUnknownAppearanceCount_IsEligible()
    {
        SeedClub("Seeded FC");
        SeedClub("Seeded FC 2");
        var unknownCount = SeedPlayer("UnknownAppearanceCount");
        SeedStints(unknownCount.Id,
            (2010, 2011, "Seeded FC", (int?)null), // unknown count — qualifies
            (2011, 2014, "Seeded FC 2", 25), // qualifies
            (2014, null, "Unseeded Club Two", (int?)null)); // extra, required to reach MinDocumentedStintCount

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Contain(unknownCount.Id));
    }

    // REQ-1201/ADR-0074/S-138: repeat stints at the SAME seeded club (e.g.
    // a loan, then a later permanent return — PlayerCareerStint's own doc
    // comment explicitly allows this as multiple distinct, valid stint
    // ROWS) still only count as ONE qualifying club, not two — the current
    // rule counts distinct qualifying club NAMES, not stint rows. A
    // candidate whose 3 stints are all at the SAME seeded club therefore
    // has only 1 distinct qualifying club and must be rejected, even though
    // it would have passed the old "≥3 stint rows, ≥1 at a seeded club"
    // rule this replaces.
    //
    // Perf fix (2026-08-03) regression coverage: this also doubles as the
    // narrowing-superset regression test for GetCareerStintCandidatePlayerIdsAsync,
    // which now narrows on ">= minSeededClubCount DISTINCT seeded club
    // names" (not stint rows) — a narrowing bug that counted raw stint ROWS
    // instead of distinct club names would wrongly let this candidate (1
    // distinct seeded club, 3 rows) through to IsEligible, which would then
    // itself correctly reject it — so this test only pins IsEligible's own
    // behavior; PlayerCareerStintRepositoryTests carries the narrowing
    // pass's own dedicated regression coverage for this same distinction.
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithThreeStintsAtSameSeededClub_NeverSelected()
    {
        SeedClub("Seeded FC");
        var sameClubThreeTimes = SeedPlayer("SameClubThrice");
        SeedStints(sameClubThreeTimes.Id,
            (2010, 2012, "Seeded FC"),
            (2013, 2015, "Seeded FC"),
            (2016, null, "Seeded FC"));

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(sameClubThreeTimes.Id));
    }

    // Perf fix (2026-08-03): the narrowing pass
    // (GetCareerStintCandidatePlayerIdsAsync) must use the same exact,
    // ordinal/case-sensitive club-name comparison IsEligible itself uses —
    // deliberately NOT GetUnseededClubCandidatesAsync's OrdinalIgnoreCase
    // precedent, which is a different, diagnostic-only choice for a
    // different method. A candidate whose only near-seeded-club stint
    // differs from the seeded name purely by case must still be rejected.
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithOnlyCaseDifferingSeededClubStint_NeverSelected()
    {
        SeedClub("Seeded FC");
        var caseMismatch = SeedPlayer("CaseMismatch");
        SeedStints(caseMismatch.Id,
            (2010, 2013, "SEEDED FC"),
            (2013, 2016, "Unseeded Club A"),
            (2016, null, "Unseeded Club B"));

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(caseMismatch.Id));
    }

    // ---- REQ-1201/ADR-0073/S-137: Player.BirthYear >= 1975 floor -----------
    // Additive to (not a re-check of) REQ-112's own shared 1939 pool floor —
    // see GetEligiblePlayerIdsAsync's own doc comment.

    // Boundary-inclusive positive control: BirthYear == MinBirthYear (1975)
    // itself must be eligible, not excluded — the check is ">=", not ">".
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithBirthYearAtFloor_IsEligible()
    {
        SeedClub("Seeded FC");
        var atFloor = SeedEligiblePlayer("BornExactly1975", "Seeded FC", birthYear: 1975);

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Contain(atFloor.Id));
    }

    // One year below the boundary: BirthYear == 1974 must be excluded.
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithBirthYearOneYearBelowFloor_NeverSelected()
    {
        SeedClub("Seeded FC");
        var tooOld = SeedEligiblePlayer("BornExactly1974", "Seeded FC", birthYear: 1974);

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(tooOld.Id));
    }

    // Fail-closed (ADR-0073): a candidate with no recorded BirthYear at all
    // must be excluded, not silently admitted — the codebase's established
    // "can't verify it, so don't admit it" convention (ADR-0070), the
    // opposite treatment from the seeded-club-stint "unknown appearance
    // count passes" rule elsewhere in this same eligibility check.
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithNullBirthYear_NeverSelected()
    {
        SeedClub("Seeded FC");
        var unknownBirthYear = SeedEligiblePlayer("UnknownBirthYear", "Seeded FC", birthYear: null);

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(unknownBirthYear.Id));
    }

    // Ordering regression, mirroring ADR0056_GetEligiblePlayerIdsAsync_
    // FamiliarityFilterOnlySeesStructurallyEligibleCandidates below: the
    // BirthYear floor is applied to the structurally-eligible set BEFORE
    // the familiarity filter runs (GetEligiblePlayerIdsAsync's own doc
    // comment on why — no point spending a familiarity-check call on a
    // candidate this check would already exclude) — a candidate excluded by
    // the BirthYear floor must never even be offered to
    // IPlayerFamiliarityService, let alone counted against it.
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_BirthYearFilterOnlySeesStructurallyEligibleCandidates()
    {
        SeedClub("Seeded FC");
        SeedEligiblePlayer("Eligible1", "Seeded FC");
        SeedEligiblePlayer("Eligible2", "Seeded FC");

        // Structurally eligible (3 well-ordered stints, one at the seeded
        // club) but excluded solely by the BirthYear floor.
        var tooOld = SeedEligiblePlayer("BornExactly1974", "Seeded FC", birthYear: 1974);

        await _service.GetEligiblePlayerIdsAsync();

        Assert.That(_playerFamiliarityService.Calls, Has.Count.EqualTo(1));
        Assert.That(_playerFamiliarityService.Calls[0], Does.Not.Contain(tooOld.Id));
    }

    // ---- REQ-1201/ADR-0079/S-161: Player.Position != null/empty floor ------
    // Additive to (not a fold into) the BirthYear floor above — see
    // GetEligiblePlayerIdsAsync's own doc comment. Fixes a real 2026-08-18
    // QA report: a puzzle rendered "Position: not available" for a target
    // whose Nationality/BirthYear WERE populated — Player.Position staying
    // null forever for a subset of rows is deliberate, documented REQ-1207
    // behavior (a data gap, not a bug), but nothing previously stopped such
    // a candidate from being SELECTED as a target in the first place.

    // Positive control, mirroring REQ1201_GetEligiblePlayerIdsAsync_
    // CandidateWithBirthYearAtFloor_IsEligible's shape: a candidate with a
    // non-null Position (SeedEligiblePlayer's own default, "Forward") is
    // still eligible — this is the "the check doesn't over-exclude" half of
    // the pair, made explicit rather than left as an implicit assumption
    // baked into every other test in this file's default fixture.
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithNonNullPosition_IsEligible()
    {
        SeedClub("Seeded FC");
        var withPosition = SeedEligiblePlayer("HasPosition", "Seeded FC", position: "Midfielder");

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Contain(withPosition.Id));
    }

    // Fail-closed (ADR-0079, matching ADR-0073/ADR-0070's precedent): a
    // candidate with no recorded Position at all must be excluded, not
    // silently admitted — this is the exact bug the 2026-08-18 QA report
    // found: a structurally-eligible, BirthYear-eligible candidate whose
    // Position happened to be null was still selected as a puzzle target
    // before this story's fix.
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithNullPosition_NeverSelected()
    {
        SeedClub("Seeded FC");
        var unknownPosition = SeedEligiblePlayer("UnknownPosition", "Seeded FC", position: null);

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(unknownPosition.Id));
    }

    // Same fail-closed rule as the null-Position case immediately above, but
    // for the broader IsNullOrWhiteSpace branch specifically — a whitespace-
    // only Position ("" would work identically, since both fail
    // IsNullOrWhiteSpace the same way; see MinBirthYear's neighboring
    // comment and ADR-0079 for why IsNullOrWhiteSpace was deliberately
    // chosen over a bare null check) must also be excluded, not admitted.
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_CandidateWithWhitespaceOnlyPosition_NeverSelected()
    {
        SeedClub("Seeded FC");
        var whitespaceOnlyPosition = SeedEligiblePlayer("WhitespaceOnlyPosition", "Seeded FC", position: " ");

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(whitespaceOnlyPosition.Id));
    }

    // Ordering regression, mirroring REQ1201_GetEligiblePlayerIdsAsync_
    // BirthYearFilterOnlySeesStructurallyEligibleCandidates above: the
    // Position floor is applied to the structurally-eligible set BEFORE
    // the familiarity filter runs — a candidate excluded by the Position
    // floor must never even be offered to IPlayerFamiliarityService, let
    // alone counted against it.
    [Test]
    public async Task REQ1201_GetEligiblePlayerIdsAsync_PositionFilterOnlySeesStructurallyEligibleCandidates()
    {
        SeedClub("Seeded FC");
        SeedEligiblePlayer("Eligible1", "Seeded FC");
        SeedEligiblePlayer("Eligible2", "Seeded FC");

        // Structurally eligible (3 well-ordered stints, one at the seeded
        // club) and BirthYear-eligible, but excluded solely by the Position
        // floor.
        var noPosition = SeedEligiblePlayer("NoPosition", "Seeded FC", position: null);

        await _service.GetEligiblePlayerIdsAsync();

        Assert.That(_playerFamiliarityService.Calls, Has.Count.EqualTo(1));
        Assert.That(_playerFamiliarityService.Calls[0], Does.Not.Contain(noPosition.Id));
    }

    // ---- ADR-0056: familiarity filter ---------------------------------------

    // ADR-0056: a candidate that passes every REQ-1201 structural check is
    // still never selected if IPlayerFamiliarityService judges it
    // unfamiliar.
    [Test]
    public async Task ADR0056_GetEligiblePlayerIdsAsync_CandidateFailingFamiliarityFilter_NeverSelected()
    {
        SeedClub("Seeded FC");
        var unfamiliar = SeedEligiblePlayer("Unfamiliar", "Seeded FC");
        _playerFamiliarityService.MarkUnfamiliar(unfamiliar.Id);

        var eligibleIds = await _service.GetEligiblePlayerIdsAsync();

        Assert.That(eligibleIds, Does.Not.Contain(unfamiliar.Id));
    }

    // ADR-0056: the familiarity filter must run against exactly the
    // structurally-eligible pool (REQ-1201's checks already applied) — a
    // candidate that fails a structural check (too few stints, here) must
    // never even be offered to the familiarity filter, let alone counted
    // against it.
    [Test]
    public async Task ADR0056_GetEligiblePlayerIdsAsync_FamiliarityFilterOnlySeesStructurallyEligibleCandidates()
    {
        SeedClub("Seeded FC");
        SeedEligiblePlayer("Eligible1", "Seeded FC");
        SeedEligiblePlayer("Eligible2", "Seeded FC");
        SeedEligiblePlayer("Eligible3", "Seeded FC");

        var tooFewStints = SeedPlayer("TwoStints");
        SeedStints(tooFewStints.Id, (2010, 2013, "Seeded FC"), (2013, null, "Other FC"));

        await _service.GetEligiblePlayerIdsAsync();

        Assert.That(_playerFamiliarityService.Calls, Has.Count.EqualTo(1));
        Assert.That(_playerFamiliarityService.Calls[0], Does.Not.Contain(tooFewStints.Id));
    }
}

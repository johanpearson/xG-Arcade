using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203 follow-up,
// ADR-0059): DuplicateCareerStintCleaner's own narrow, provable-only
// cleanup — see that class's own doc comment for the full reasoning on why
// it's scoped this way rather than a full purge-and-reseed.
public class DuplicateCareerStintCleanerTests
{
    private XGArcadeDbContext _dbContext = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private async Task SeedClubAsync(string name, string wikidataQid = "Q1")
    {
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid });
        await _dbContext.SaveChangesAsync();
    }

    private async Task<PlayerCareerStint> SeedStintAsync(
        Guid playerId, string clubName, int startYear, int? endYear, int? appearanceCount)
    {
        var stint = new PlayerCareerStint
        {
            Id = Guid.NewGuid(), PlayerId = playerId, ClubName = clubName,
            StartYear = startYear, EndYear = endYear, AppearanceCount = appearanceCount,
        };
        _dbContext.PlayerCareerStints.Add(stint);
        await _dbContext.SaveChangesAsync();
        return stint;
    }

    [Test]
    public async Task REQ1203_CleanAsync_NonCanonicalRowWithMatchingCanonicalRow_IsRemoved()
    {
        await SeedClubAsync("Lyon");
        var playerId = Guid.NewGuid();
        await SeedStintAsync(playerId, "Lyon", 2000, 2003, 90); // Canonical (xG Grid byproduct writer).
        await SeedStintAsync(playerId, "Olympique Lyonnais", 2000, 2003, 90); // Pre-fix full-career-fetch writer.

        var removedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(removedCount, Is.EqualTo(1));
        var remaining = await _dbContext.PlayerCareerStints.Where(s => s.PlayerId == playerId).ToListAsync();
        Assert.That(remaining, Has.Count.EqualTo(1));
        Assert.That(remaining[0].ClubName, Is.EqualTo("Lyon"));
    }

    [Test]
    public async Task REQ1203_CleanAsync_NonCanonicalRowWithNoMatchingCanonicalRow_IsLeftAlone()
    {
        await SeedClubAsync("Lyon");
        var playerId = Guid.NewGuid();
        // No canonical "Lyon" row for this player at all — nothing to prove this row against.
        await SeedStintAsync(playerId, "Olympique Lyonnais", 2000, 2003, 90);

        var removedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(removedCount, Is.EqualTo(0));
        Assert.That(await _dbContext.PlayerCareerStints.CountAsync(s => s.PlayerId == playerId), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ1203_CleanAsync_GenuinelyUnseededClubRow_IsNeverTouched()
    {
        await SeedClubAsync("Lyon");
        var playerId = Guid.NewGuid();
        await SeedStintAsync(playerId, "Some Genuinely Unseeded Club", 2005, 2008, 40);

        var removedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(removedCount, Is.EqualTo(0));
        Assert.That(await _dbContext.PlayerCareerStints.CountAsync(s => s.PlayerId == playerId), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ1203_CleanAsync_DifferentDates_IsNotTreatedAsADuplicate()
    {
        await SeedClubAsync("Lyon");
        var playerId = Guid.NewGuid();
        await SeedStintAsync(playerId, "Lyon", 2000, 2003, 90);
        // Same club, but genuinely different stint window (e.g. a loan, then a later return)
        // — must never be collapsed just because the club matches.
        await SeedStintAsync(playerId, "Olympique Lyonnais", 2008, 2010, 30);

        var removedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(removedCount, Is.EqualTo(0));
        Assert.That(await _dbContext.PlayerCareerStints.CountAsync(s => s.PlayerId == playerId), Is.EqualTo(2));
    }

    [Test]
    public async Task REQ1203_CleanAsync_NullVsPopulatedAppearanceCount_MergesIntoCanonicalRowKeepingPopulatedCount()
    {
        // Bug fix (2026-08-10, bug-bundle): supersedes the previous
        // "DifferentAppearanceCount_IsNotTreatedAsADuplicate" test, whose
        // name and assertions are now only accurate for the
        // both-populated-and-different case (see the next test below). A
        // null AppearanceCount means "unknown," and a populated value seen
        // on the matching non-canonical row is strictly more informative,
        // not a conflict — the two rows must now merge, with the surviving
        // canonical row's AppearanceCount updated to the populated value
        // rather than that value being silently dropped.
        await SeedClubAsync("Lyon");
        var playerId = Guid.NewGuid();
        await SeedStintAsync(playerId, "Lyon", 2000, 2003, null);
        await SeedStintAsync(playerId, "Olympique Lyonnais", 2000, 2003, 90);

        var removedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(removedCount, Is.EqualTo(1));
        var remaining = await _dbContext.PlayerCareerStints.Where(s => s.PlayerId == playerId).ToListAsync();
        Assert.That(remaining, Has.Count.EqualTo(1));
        Assert.That(remaining[0].ClubName, Is.EqualTo("Lyon"));
        Assert.That(remaining[0].AppearanceCount, Is.EqualTo(90));
    }

    [Test]
    public async Task REQ1203_CleanAsync_BothAppearanceCountsPopulatedButDiffer_IsNotTreatedAsADuplicate()
    {
        // The narrower, INTENTIONAL non-fix carve-out this class's own doc
        // comment describes: two rows for the same player/dates but with
        // DIFFERENT, both-populated AppearanceCount values could plausibly
        // be two genuinely different stints, so they must not be merged —
        // same "known, ACCEPTED limitation" as
        // WikidataClient.MergeCareerStintEntries' identical rule.
        await SeedClubAsync("Lyon");
        var playerId = Guid.NewGuid();
        await SeedStintAsync(playerId, "Lyon", 2000, 2003, 25);
        await SeedStintAsync(playerId, "Olympique Lyonnais", 2000, 2003, 90);

        var removedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(removedCount, Is.EqualTo(0));
        Assert.That(await _dbContext.PlayerCareerStints.CountAsync(s => s.PlayerId == playerId), Is.EqualTo(2));
    }

    // ---- Step 2: same-ClubName duplicates (2026-08-10 bug-bundle) --------
    // Regression coverage for the exact bug reported with screenshots: "AC
    // Milan 25 apps" / "AC Milan 95 apps" and "Real Sociedad 2 apps" / bare
    // "Real Sociedad," all under one already-identical ClubName that Step 1
    // above never compares against itself.

    [Test]
    public async Task REQ1203_CleanAsync_SameClubNameNullVsPopulatedAppearanceCount_Merges()
    {
        await SeedClubAsync("Real Sociedad");
        var playerId = Guid.NewGuid();
        var populated = await SeedStintAsync(playerId, "Real Sociedad", 2018, 2020, 2);
        await SeedStintAsync(playerId, "Real Sociedad", 2018, 2020, null);

        var removedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(removedCount, Is.EqualTo(1));
        var remaining = await _dbContext.PlayerCareerStints.Where(s => s.PlayerId == playerId).ToListAsync();
        Assert.That(remaining, Has.Count.EqualTo(1));
        Assert.That(remaining[0].Id, Is.EqualTo(populated.Id));
        Assert.That(remaining[0].AppearanceCount, Is.EqualTo(2));
    }

    [Test]
    public async Task REQ1203_CleanAsync_SameClubNameBothAppearanceCountsPopulatedButDiffer_IsNotTreatedAsADuplicate()
    {
        await SeedClubAsync("AC Milan");
        var playerId = Guid.NewGuid();
        await SeedStintAsync(playerId, "AC Milan", 2019, 2021, 25);
        await SeedStintAsync(playerId, "AC Milan", 2019, 2021, 95);

        var removedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(removedCount, Is.EqualTo(0));
        Assert.That(await _dbContext.PlayerCareerStints.CountAsync(s => s.PlayerId == playerId), Is.EqualTo(2));
    }

    [Test]
    public async Task REQ1203_CleanAsync_SameClubNameIdenticalPopulatedAppearanceCounts_CollapsesToOneRow()
    {
        // Bug fix (2026-08-10 follow-up, quality-gate finding): two rows for
        // the same club/dates with the SAME populated AppearanceCount (no
        // null row at all) used to slip past this step untouched — the
        // distinctPopulatedCounts.Count == 1 check passed, but the old
        // "remove null rows" loop found nothing to remove since neither row
        // was null. Both duplicate rows must now collapse to one.
        await SeedClubAsync("AC Milan");
        var playerId = Guid.NewGuid();
        await SeedStintAsync(playerId, "AC Milan", 2019, 2021, 25);
        await SeedStintAsync(playerId, "AC Milan", 2019, 2021, 25);

        var removedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(removedCount, Is.EqualTo(1));
        var remaining = await _dbContext.PlayerCareerStints.Where(s => s.PlayerId == playerId).ToListAsync();
        Assert.That(remaining, Has.Count.EqualTo(1));
        Assert.That(remaining[0].AppearanceCount, Is.EqualTo(25));
    }

    // ---- Step 1: 3+-row groups sharing a (PlayerId, StartYear, EndYear) ---
    // key (2026-08-10 follow-up, quality-gate finding) ----------------------
    // Regression coverage for the order-dependent-merge risk: the previous
    // per-stint loop mutated a canonical row's AppearanceCount in place
    // while iterating, so which non-canonical row "won" for a 3+-row group
    // depended on allStints' enumeration order. Now deterministic: an
    // ambiguous group (more than one distinct populated AppearanceCount
    // across the whole group) is left entirely alone; an unambiguous group
    // (at most one distinct populated value) still merges correctly.

    [Test]
    public async Task REQ1203_CleanAsync_ThreeRowGroupWithTwoDistinctPopulatedAppearanceCounts_IsLeftEntirelyAlone()
    {
        await SeedClubAsync("Lyon");
        var playerId = Guid.NewGuid();
        await SeedStintAsync(playerId, "Lyon", 2000, 2003, null); // Canonical row, unknown count.
        await SeedStintAsync(playerId, "Olympique Lyonnais", 2000, 2003, 25); // Non-canonical, 25 apps.
        await SeedStintAsync(playerId, "OL", 2000, 2003, 95); // Non-canonical, 95 apps — disagrees with the row above.

        var removedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(removedCount, Is.EqualTo(0));
        var remaining = await _dbContext.PlayerCareerStints.Where(s => s.PlayerId == playerId).ToListAsync();
        Assert.That(remaining, Has.Count.EqualTo(3));
        Assert.That(remaining.Single(s => s.ClubName == "Lyon").AppearanceCount, Is.Null);
        Assert.That(remaining.Single(s => s.ClubName == "Olympique Lyonnais").AppearanceCount, Is.EqualTo(25));
        Assert.That(remaining.Single(s => s.ClubName == "OL").AppearanceCount, Is.EqualTo(95));
    }

    [Test]
    public async Task REQ1203_CleanAsync_ThreeRowGroupWithOnePopulatedAppearanceCountAndOneNull_StillMergesCorrectly()
    {
        await SeedClubAsync("Lyon");
        var playerId = Guid.NewGuid();
        await SeedStintAsync(playerId, "Lyon", 2000, 2003, null); // Canonical row, unknown count.
        await SeedStintAsync(playerId, "Olympique Lyonnais", 2000, 2003, 90); // Non-canonical, the one populated value.
        await SeedStintAsync(playerId, "OL", 2000, 2003, null); // Non-canonical, also unknown.

        var removedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(removedCount, Is.EqualTo(2));
        var remaining = await _dbContext.PlayerCareerStints.Where(s => s.PlayerId == playerId).ToListAsync();
        Assert.That(remaining, Has.Count.EqualTo(1));
        Assert.That(remaining[0].ClubName, Is.EqualTo("Lyon"));
        Assert.That(remaining[0].AppearanceCount, Is.EqualTo(90));
    }

    [Test]
    public async Task REQ1203_CleanAsync_DifferentPlayers_NeverCrossMatched()
    {
        await SeedClubAsync("Lyon");
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        await SeedStintAsync(playerA, "Lyon", 2000, 2003, 90);
        // Same dates/club label/appearance count, but a DIFFERENT player —
        // must never be matched against playerA's canonical row.
        await SeedStintAsync(playerB, "Olympique Lyonnais", 2000, 2003, 90);

        var removedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(removedCount, Is.EqualTo(0));
        Assert.That(await _dbContext.PlayerCareerStints.CountAsync(s => s.PlayerId == playerB), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ1203_CleanAsync_NoSeededClubs_ThrowsRatherThanSilentlyRemovingNothing()
    {
        var playerId = Guid.NewGuid();
        await SeedStintAsync(playerId, "Lyon", 2000, 2003, 90);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await DuplicateCareerStintCleaner.CleanAsync(_dbContext));
    }

    [Test]
    public async Task REQ1203_CleanAsync_IsSafeToRunAgain_WhenNothingIsLeftToClean()
    {
        await SeedClubAsync("Lyon");
        var playerId = Guid.NewGuid();
        await SeedStintAsync(playerId, "Lyon", 2000, 2003, 90);
        await SeedStintAsync(playerId, "Olympique Lyonnais", 2000, 2003, 90);
        await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        var secondRunRemovedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(secondRunRemovedCount, Is.EqualTo(0));
    }
}

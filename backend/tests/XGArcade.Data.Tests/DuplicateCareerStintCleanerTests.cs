using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203 follow-up,
// ADR-0058): DuplicateCareerStintCleaner's own narrow, provable-only
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
    public async Task REQ1203_CleanAsync_DifferentAppearanceCount_IsNotTreatedAsADuplicate()
    {
        // Same "known, ACCEPTED limitation" as WikidataClient.ParseCareerStintBindings'
        // own dedup: a null vs. known AppearanceCount must not be treated as
        // "matches anything," or two genuinely different stints could be
        // silently merged.
        await SeedClubAsync("Lyon");
        var playerId = Guid.NewGuid();
        await SeedStintAsync(playerId, "Lyon", 2000, 2003, null);
        await SeedStintAsync(playerId, "Olympique Lyonnais", 2000, 2003, 90);

        var removedCount = await DuplicateCareerStintCleaner.CleanAsync(_dbContext);

        Assert.That(removedCount, Is.EqualTo(0));
        Assert.That(await _dbContext.PlayerCareerStints.CountAsync(s => s.PlayerId == playerId), Is.EqualTo(2));
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

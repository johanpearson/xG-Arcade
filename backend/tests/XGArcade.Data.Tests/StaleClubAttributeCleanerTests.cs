using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// S-037: StaleClubAttributeCleaner recovers from a wrong Wikidata QID
// discovered after real PlayerAttribute/PlayerData rows were already
// fetched under it — see that class's own doc comment and NOTES.md's
// 2026-07-13 entry for the real incident this responds to (4 of S-036's
// club QIDs were wrong; each happened to be some *other* real Wikidata
// entity, so queries against them didn't error or return empty, they
// silently returned real-but-wrong player data under the intended club's
// name).
public class StaleClubAttributeCleanerTests
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

    private async Task<Player> SeedPlayerWithClubAttributeAsync(string clubName, string playerNameSuffix = "")
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = $"Player {clubName}{playerNameSuffix}", WikidataQid = $"Q{Guid.NewGuid():N}" };
        _dbContext.Players.Add(player);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = clubName });
        _dbContext.PlayerData.Add(new PlayerData
        {
            Id = Guid.NewGuid(),
            PlayerId = player.Id,
            Field = "club",
            Value = clubName,
            Source = "wikidata",
            Confidence = "unverified",
            SyncedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
        return player;
    }

    // The regression case this whole class exists for: a club seeded with
    // a QID that turns out to be some *other* real Wikidata entity doesn't
    // fail loudly (empty results, an error) — it silently returns that
    // other entity's real players, persisted under the intended club's
    // name (here simulated directly, the same shape WikidataLookupService
    // would have produced against the wrong QID). Confirms the actual
    // regression this class guards against: after the QID is corrected,
    // the wrongly-matched data doesn't linger — a subsequent guess/grid-
    // generation lookup against this club name finds zero cached matches,
    // not a silent match against the unrelated entity's leftover data.
    [Test]
    public async Task REQ111_CleanAsync_RemovesDataFetchedUnderAPreviouslyWrongQid_LeavingZeroCachedMatches()
    {
        // Simulates what actually happened for Napoli/AS Roma/Sevilla/Porto:
        // real players persisted under the club's name, fetched while its
        // WikidataQid pointed at the wrong entity.
        await SeedPlayerWithClubAttributeAsync("Napoli");
        await SeedPlayerWithClubAttributeAsync("Napoli", " Two");

        var (removedAttributeCount, removedDataCount) = await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(removedAttributeCount, Is.EqualTo(2));
        Assert.That(removedDataCount, Is.EqualTo(2));
        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "club" && a.AttributeValue == "Napoli"), Is.EqualTo(0),
            "no cell should ever be able to silently match against data fetched under the wrong QID after it's corrected");
        Assert.That(await _dbContext.PlayerData.CountAsync(d => d.Field == "club" && d.Value == "Napoli"), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ111_CleanAsync_OnlyRemovesTheNamedClubs_LeavesOthersUntouched()
    {
        await SeedPlayerWithClubAttributeAsync("Napoli");
        await SeedPlayerWithClubAttributeAsync("Arsenal");

        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "club" && a.AttributeValue == "Arsenal"), Is.EqualTo(1),
            "cleaning one club's stale data must never touch another club's real data");
    }

    [Test]
    public async Task REQ111_CleanAsync_MultipleClubNamesAtOnce_RemovesAllOfThem()
    {
        await SeedPlayerWithClubAttributeAsync("Napoli");
        await SeedPlayerWithClubAttributeAsync("AS Roma");
        await SeedPlayerWithClubAttributeAsync("Sevilla");
        await SeedPlayerWithClubAttributeAsync("Porto");
        await SeedPlayerWithClubAttributeAsync("Arsenal");

        var (removedAttributeCount, removedDataCount) = await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli", "AS Roma", "Sevilla", "Porto"]);

        Assert.That(removedAttributeCount, Is.EqualTo(4));
        Assert.That(removedDataCount, Is.EqualTo(4));
        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "club" && a.AttributeValue == "Arsenal"), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ111_CleanAsync_DoesNotTouchNonClubAttributes()
    {
        var player = await SeedPlayerWithClubAttributeAsync("Napoli");
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "Napoli" });
        await _dbContext.SaveChangesAsync();

        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "nationality" && a.AttributeValue == "Napoli"), Is.EqualTo(1),
            "AttributeType must be scoped to \"club\" specifically — a same-named nationality value (however contrived) must not be swept up");
    }

    [Test]
    public async Task REQ111_CleanAsync_IsSafeToRunAgain_WhenNothingIsLeftToClean()
    {
        await SeedPlayerWithClubAttributeAsync("Napoli");
        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        var (secondRunRemovedAttributeCount, secondRunRemovedDataCount) = await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        Assert.That(secondRunRemovedAttributeCount, Is.EqualTo(0));
        Assert.That(secondRunRemovedDataCount, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ111_CleanAsync_NoMatchingData_ReturnsZero_DoesNotThrow()
    {
        var (removedAttributeCount, removedDataCount) = await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Nonexistent Club"]);

        Assert.That(removedAttributeCount, Is.EqualTo(0));
        Assert.That(removedDataCount, Is.EqualTo(0));
    }

    // ---- CleanAllSeededClubsAsync (`--all-clubs` mode) ---------------------
    // Added for the truthy-wdt:P54 recovery (see the cleaner's own doc
    // comment): when every seeded club's cached data is suspect at once,
    // the club-name list must come from the ClubDefinition reference table,
    // not a hand-typed ~32-name argument where one typo silently leaves a
    // club stale (indistinguishable from "nothing to clean").

    private async Task SeedClubDefinitionAsync(string name)
    {
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = "Q1" });
        await _dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task REQ111_CleanAllSeededClubsAsync_ResolvesNamesFromClubDefinitionTable_RemovesEverySeededClubsData()
    {
        await SeedClubDefinitionAsync("Napoli");
        await SeedClubDefinitionAsync("AC Milan");
        await SeedPlayerWithClubAttributeAsync("Napoli");
        await SeedPlayerWithClubAttributeAsync("AC Milan");

        var (removedAttributeCount, removedDataCount, clubNames) = await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        Assert.That(clubNames, Is.EquivalentTo(new[] { "Napoli", "AC Milan" }),
            "the swept club list must be resolved from ClubDefinition at runtime, and reported so the operator can eyeball it");
        Assert.That(removedAttributeCount, Is.EqualTo(2));
        Assert.That(removedDataCount, Is.EqualTo(2));
        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "club"), Is.EqualTo(0));
        Assert.That(await _dbContext.PlayerData.CountAsync(d => d.Field == "club"), Is.EqualTo(0));
    }

    [Test]
    public async Task REQ111_CleanAllSeededClubsAsync_MeansAllSeededClubs_NotAllClubAttributeRows()
    {
        // "--all-clubs" is scoped by the reference table, same as the named
        // mode is scoped by its argument — a club attribute value that no
        // ClubDefinition row claims (e.g. legacy data for a since-removed
        // club) is deliberately out of reach of this tool either way.
        await SeedClubDefinitionAsync("Napoli");
        await SeedPlayerWithClubAttributeAsync("Napoli");
        await SeedPlayerWithClubAttributeAsync("Unseeded Legacy Club");

        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "club" && a.AttributeValue == "Unseeded Legacy Club"), Is.EqualTo(1));
        Assert.That(await _dbContext.PlayerData.CountAsync(d => d.Field == "club" && d.Value == "Unseeded Legacy Club"), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ111_CleanAllSeededClubsAsync_DoesNotTouchNonClubAttributes()
    {
        await SeedClubDefinitionAsync("Napoli");
        var player = await SeedPlayerWithClubAttributeAsync("Napoli");
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "Italy" });
        await _dbContext.SaveChangesAsync();

        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        Assert.That(await _dbContext.PlayerAttributes.CountAsync(a => a.AttributeType == "nationality" && a.AttributeValue == "Italy"), Is.EqualTo(1),
            "recovering club data must never remove nationality attributes — those weren't fetched under the broken club pattern's value semantics");
    }

    [Test]
    public void REQ111_CleanAllSeededClubsAsync_NoClubDefinitionRows_ThrowsInsteadOfSilentlyCleaningNothing()
    {
        // Zero seeded clubs is a wrong-database/never-seeded signal, not a
        // real "nothing to clean" case — a quiet "removed 0 rows" success
        // here would read as recovery-complete while leaving every stale
        // row in place on the intended database.
        Assert.ThrowsAsync<InvalidOperationException>(() => StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext));
    }
}

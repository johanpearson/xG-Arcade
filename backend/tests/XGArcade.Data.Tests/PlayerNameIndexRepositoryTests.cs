using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// S-032 (docs/backlog.md, ADR-0007/REQ-207): COMP-10's own repository —
// same "no REQ-xxx exists yet for this repository's own plumbing" naming
// pattern as PlayerStoreRepositoryTests where a test is about the
// repository's mechanics rather than a specific acceptance criterion;
// REQ207-prefixed where a test asserts something the requirement's Given/
// When/Then explicitly calls for (the structural PlayerAttribute-independence
// guarantee below).
public class PlayerNameIndexRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerNameIndexRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PlayerNameIndexRepository(_dbContext);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private static PlayerNameIndex BuildEntry(string primaryName, int? birthYear = null, string? nationality = null, string? photoUrl = null) =>
        new()
        {
            PlayerId = Guid.NewGuid(),
            PrimaryName = primaryName,
            NormalizedName = PlayerNameNormalizer.Normalize(primaryName),
            BirthYear = birthYear,
            PrimaryNationality = nationality,
            PhotoUrl = photoUrl,
        };

    [Test]
    public async Task SearchByPrefixAsync_ReturnsEntriesWhoseNormalizedNameStartsWithQuery()
    {
        await _repository.UpsertManyAsync([BuildEntry("Thierry Henry"), BuildEntry("Theo Hernandez"), BuildEntry("Lionel Messi")]);

        var results = await _repository.SearchByPrefixAsync("the", 10);

        Assert.That(results.Select(r => r.PrimaryName), Is.EquivalentTo(new[] { "Thierry Henry", "Theo Hernandez" }));
    }

    [Test]
    public async Task SearchByPrefixAsync_RespectsLimit()
    {
        await _repository.UpsertManyAsync([BuildEntry("Kylian A"), BuildEntry("Kylian B"), BuildEntry("Kylian C")]);

        var results = await _repository.SearchByPrefixAsync("kylian", 2);

        Assert.That(results, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task SearchByPrefixAsync_NoMatch_ReturnsEmpty()
    {
        await _repository.UpsertManyAsync([BuildEntry("Thierry Henry")]);

        var results = await _repository.SearchByPrefixAsync("zzz", 10);

        Assert.That(results, Is.Empty);
    }

    // REQ-207's explicit acceptance criterion: a PlayerNameIndex row must
    // come back as a suggestion regardless of whether the same player has
    // any PlayerAttribute rows at all — this is the structural separation
    // ADR-0007 exists to guarantee, not just "it returns something."
    [Test]
    public async Task REQ207_SearchByPrefixAsync_ReturnsEntry_WhenPlayerHasZeroPlayerAttributeRows()
    {
        var entry = BuildEntry("Someone Uncached");
        await _repository.UpsertManyAsync([entry]);

        // Deliberately no PlayerAttribute row added anywhere for entry.PlayerId.
        Assert.That(await _dbContext.PlayerAttributes.AnyAsync(pa => pa.PlayerId == entry.PlayerId), Is.False,
            "test setup sanity check: this player must have zero PlayerAttribute rows");

        var results = await _repository.SearchByPrefixAsync("someone", 10);

        Assert.That(results.Select(r => r.PlayerId), Does.Contain(entry.PlayerId));
    }

    [Test]
    public async Task UpsertManyAsync_ExistingPlayerId_UpdatesInPlace_NotDuplicateInsert()
    {
        var playerId = Guid.NewGuid();
        await _repository.UpsertManyAsync([new PlayerNameIndex
        {
            PlayerId = playerId,
            PrimaryName = "Old Name",
            NormalizedName = PlayerNameNormalizer.Normalize("Old Name"),
        }]);

        await _repository.UpsertManyAsync([new PlayerNameIndex
        {
            PlayerId = playerId,
            PrimaryName = "Corrected Name",
            NormalizedName = PlayerNameNormalizer.Normalize("Corrected Name"),
            BirthYear = 1990,
        }]);

        var rowCount = await _dbContext.PlayerNameIndexEntries.CountAsync(p => p.PlayerId == playerId);
        Assert.That(rowCount, Is.EqualTo(1), "a re-imported entry for the same player must update in place, never duplicate");

        var stored = await _dbContext.PlayerNameIndexEntries.SingleAsync(p => p.PlayerId == playerId);
        Assert.That(stored.PrimaryName, Is.EqualTo("Corrected Name"));
        Assert.That(stored.BirthYear, Is.EqualTo(1990));
    }

    [Test]
    public async Task UpsertManyAsync_NewPlayerId_Inserts()
    {
        await _repository.UpsertManyAsync([BuildEntry("Brand New Player")]);

        var stored = await _dbContext.PlayerNameIndexEntries.SingleOrDefaultAsync(p => p.PrimaryName == "Brand New Player");
        Assert.That(stored, Is.Not.Null);
    }

    [Test]
    public async Task UpsertManyAsync_EmptyCollection_DoesNothing()
    {
        await _repository.UpsertManyAsync([]);

        Assert.That(await _dbContext.PlayerNameIndexEntries.CountAsync(), Is.EqualTo(0));
    }
}

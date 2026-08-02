using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// Bug fix (2026-08-02): PlayerAliasNormalizedAliasBackfiller fixes
// PlayerAlias rows whose NormalizedAlias is stale relative to what
// PlayerNameNormalizer would currently compute (originally: PlayerNameNormalizer's
// non-decomposable-Latin-letter fix, Ø/Æ/Œ/Đ/Ł/ß/Þ) — the scenario a
// pre-fix-deployed database row would be in. Same shape as
// PlayerNormalizedFullNameBackfillerTests, adapted for PlayerAlias's
// composite (PlayerId, NormalizedAlias) key (see the backfiller's own doc
// comment for why that key shape rules out "reassign the property in
// place").
public class PlayerAliasNormalizedAliasBackfillerTests
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

    [Test]
    public async Task BackfillAsync_FixesStaleNormalizedAlias()
    {
        var playerId = Guid.NewGuid();
        // Simulates a row persisted under the old, broken normalizer — the
        // real "Ødegaard" bug shape, but as an alias row rather than
        // Player.FullName.
        _dbContext.PlayerAliases.Add(new PlayerAlias { PlayerId = playerId, Alias = "Ødegaard", NormalizedAlias = "ødegaard" });
        await _dbContext.SaveChangesAsync();

        await PlayerAliasNormalizedAliasBackfiller.BackfillAsync(_dbContext);

        var reloaded = await _dbContext.PlayerAliases.AsNoTracking().SingleAsync(a => a.PlayerId == playerId);
        Assert.That(reloaded.NormalizedAlias, Is.EqualTo("odegaard"));
        Assert.That(reloaded.Alias, Is.EqualTo("Ødegaard"), "the human-readable Alias text is preserved, only NormalizedAlias changes");
    }

    [Test]
    public async Task BackfillAsync_IsIdempotent_NoChangeWhenAlreadyCorrect()
    {
        var playerId = Guid.NewGuid();
        _dbContext.PlayerAliases.Add(new PlayerAlias { PlayerId = playerId, Alias = "Pele", NormalizedAlias = "pele" });
        await _dbContext.SaveChangesAsync();

        await PlayerAliasNormalizedAliasBackfiller.BackfillAsync(_dbContext);

        var aliases = await _dbContext.PlayerAliases.AsNoTracking().Where(a => a.PlayerId == playerId).ToListAsync();
        Assert.That(aliases, Has.Count.EqualTo(1));
        Assert.That(aliases[0].NormalizedAlias, Is.EqualTo("pele"));
    }

    [Test]
    public async Task BackfillAsync_NoAliases_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(async () => await PlayerAliasNormalizedAliasBackfiller.BackfillAsync(_dbContext));
    }

    [Test]
    public async Task BackfillAsync_MultipleAliasesForSamePlayer_BackfillsEachIndependently()
    {
        var playerId = Guid.NewGuid();
        _dbContext.PlayerAliases.AddRange(
            new PlayerAlias { PlayerId = playerId, Alias = "Ødegaard", NormalizedAlias = "ødegaard" },
            new PlayerAlias { PlayerId = playerId, Alias = "Minnen", NormalizedAlias = "minnen" });
        await _dbContext.SaveChangesAsync();

        await PlayerAliasNormalizedAliasBackfiller.BackfillAsync(_dbContext);

        var normalizedAliases = await _dbContext.PlayerAliases.AsNoTracking()
            .Where(a => a.PlayerId == playerId)
            .Select(a => a.NormalizedAlias)
            .ToListAsync();
        Assert.That(normalizedAliases, Is.EquivalentTo(new[] { "odegaard", "minnen" }));
    }

    [Test]
    public async Task BackfillAsync_MultipleRowsAcrossDifferentPlayers_BackfillsEachIndependently()
    {
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        _dbContext.PlayerAliases.AddRange(
            new PlayerAlias { PlayerId = playerA, Alias = "Ødegaard", NormalizedAlias = "ødegaard" },
            new PlayerAlias { PlayerId = playerB, Alias = "Pele", NormalizedAlias = "pele" });
        await _dbContext.SaveChangesAsync();

        await PlayerAliasNormalizedAliasBackfiller.BackfillAsync(_dbContext);

        Assert.That((await _dbContext.PlayerAliases.AsNoTracking().SingleAsync(a => a.PlayerId == playerA)).NormalizedAlias, Is.EqualTo("odegaard"));
        Assert.That((await _dbContext.PlayerAliases.AsNoTracking().SingleAsync(a => a.PlayerId == playerB)).NormalizedAlias, Is.EqualTo("pele"));
    }

    // Collision case: a stale row and an already-correct row for the same
    // player converge on the same recomputed NormalizedAlias once the fixed
    // normalizer runs — the backfiller must drop the now-redundant stale
    // row rather than attempt a duplicate composite-key insert (which the
    // real database's unique constraint would reject).
    [Test]
    public async Task BackfillAsync_StaleAliasCollidesWithAlreadyCorrectAlias_DropsTheRedundantRowWithoutThrowing()
    {
        var playerId = Guid.NewGuid();
        _dbContext.PlayerAliases.AddRange(
            // Stale: recomputes to "odegaard", which collides with the row below.
            new PlayerAlias { PlayerId = playerId, Alias = "Ødegaard", NormalizedAlias = "ødegaard" },
            // Already correct.
            new PlayerAlias { PlayerId = playerId, Alias = "Odegaard", NormalizedAlias = "odegaard" });
        await _dbContext.SaveChangesAsync();

        Assert.DoesNotThrowAsync(async () => await PlayerAliasNormalizedAliasBackfiller.BackfillAsync(_dbContext));

        var aliases = await _dbContext.PlayerAliases.AsNoTracking().Where(a => a.PlayerId == playerId).ToListAsync();
        Assert.That(aliases, Has.Count.EqualTo(1));
        Assert.That(aliases[0].NormalizedAlias, Is.EqualTo("odegaard"));
    }
}
